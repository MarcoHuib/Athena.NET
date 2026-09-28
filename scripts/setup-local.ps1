[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$AthenaHost,

    [Parameter()]
    [string]$SqlSaPassword,

    [Parameter()]
    [switch]$ForceConfig,

    [Parameter()]
    [switch]$SkipSubmodules
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Ok {
    param([string]$Message)
    Write-Host "    [OK] $Message" -ForegroundColor Green
}

function Write-Warn {
    param([string]$Message)
    Write-Host "    [WARN] $Message" -ForegroundColor Yellow
}

function Get-PlainTextFromSecureString {
    param([Security.SecureString]$SecureString)

    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureString)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
    }
}

function Fill-RandomBytes {
    param([byte[]]$Buffer)

    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($Buffer)
    }
    finally {
        $rng.Dispose()
    }
}

function New-RandomPassword {
    param([int]$Bytes = 24)

    $buffer = New-Object byte[] $Bytes
    Fill-RandomBytes $buffer

    # Prefix guarantees the character classes SQL Server password policy expects.
    $body = [Convert]::ToBase64String($buffer).TrimEnd("=")
    return "Pa!9$body"
}

function New-ServiceToken {
    $buffer = New-Object byte[] 32
    Fill-RandomBytes $buffer
    return [Convert]::ToBase64String($buffer)
}

function New-SqlConnectionString {
    param(
        [string]$Database,
        [string]$Password
    )

    $builder = New-Object System.Data.Common.DbConnectionStringBuilder
    $builder["Server"] = "localhost,58043"
    $builder["Database"] = $Database
    $builder["User ID"] = "sa"
    $builder["Password"] = $Password
    $builder["Encrypt"] = "True"
    $builder["TrustServerCertificate"] = "True"
    return $builder.ConnectionString
}

function Add-GitLocalExclude {
    param(
        [string]$RepoRoot,
        [string]$Entry
    )

    $excludePath = Join-Path $RepoRoot ".git/info/exclude"
    $excludeDir = Split-Path -Parent $excludePath

    if (-not (Test-Path $excludeDir)) {
        New-Item -ItemType Directory -Path $excludeDir -Force | Out-Null
    }

    if (-not (Test-Path $excludePath)) {
        New-Item -ItemType File -Path $excludePath -Force | Out-Null
    }

    $existing = @(Get-Content $excludePath -ErrorAction SilentlyContinue)
    if ($existing -notcontains $Entry) {
        Add-Content -Path $excludePath -Value $Entry
    }
}

function Assert-Command {
    param([string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Vereist commando '$Name' is niet gevonden in PATH."
    }
}

function Get-RepoRoot {
    # Script may live in repo root or under scripts/.
    $candidates = @(
        $PSScriptRoot,
        (Split-Path -Parent $PSScriptRoot),
        (Get-Location).Path
    ) | Select-Object -Unique

    foreach ($candidate in $candidates) {
        if (
            (Test-Path (Join-Path $candidate "Athena.NET.sln")) -and
            (Test-Path (Join-Path $candidate ".git"))
        ) {
            return (Resolve-Path $candidate).Path
        }
    }

    throw "pAthena repository root niet gevonden. Plaats dit script in de repository root of in scripts/."
}

function Assert-IPv4 {
    param([string]$Address)

    $parsed = $null
    if (-not [Net.IPAddress]::TryParse($Address, [ref]$parsed)) {
        throw "'$Address' is geen geldig IP-adres."
    }

    if ($parsed.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
        throw "'$Address' is geen IPv4-adres."
    }
}

$repoRoot = Get-RepoRoot
Set-Location $repoRoot

Write-Host ""
Write-Host "pAthena local setup" -ForegroundColor White
Write-Host "Repository : $repoRoot"
Write-Host "Athena host: $AthenaHost"

Assert-IPv4 $AthenaHost
Assert-Command "git"

Write-Step "Lokale bestanden expliciet buiten Git houden"

$localExcludeEntries = @(
    "/solutionfiles/secrets/secret.json",
    "/docs/.env",
    "/conf/login_athena.conf",
    "/conf/inter_athena.conf",
    "/conf/char_athena.conf",
    "/conf/map_athena.conf",
    "/conf/subnet_athena.conf",
    "/.local/"
)

foreach ($entry in $localExcludeEntries) {
    Add-GitLocalExclude -RepoRoot $repoRoot -Entry $entry
}

Write-Ok ".git/info/exclude bijgewerkt"

if (-not $SkipSubmodules) {
    Write-Step "Git submodules initialiseren"

    & git -C $repoRoot submodule update --init --recursive
    if ($LASTEXITCODE -ne 0) {
        throw "git submodule update is mislukt."
    }

    Write-Ok "rAthena/OpenKore submodules staan op de pinned commits"
}

Write-Step "Lokale Athena-configuratie herstellen"

$templateDir = Join-Path $repoRoot "conf/templates"
$configDir = Join-Path $repoRoot "conf"

$configFiles = @(
    "login_athena.conf",
    "inter_athena.conf",
    "char_athena.conf",
    "map_athena.conf",
    "subnet_athena.conf"
)

foreach ($name in $configFiles) {
    $source = Join-Path $templateDir $name
    $target = Join-Path $configDir $name

    if (-not (Test-Path $source)) {
        throw "Template ontbreekt: $source"
    }

    if ((Test-Path $target) -and -not $ForceConfig) {
        Write-Ok "$name bestaat al, behouden"
        continue
    }

    if ((Test-Path $target) -and $ForceConfig) {
        $backupDir = Join-Path $repoRoot ".local/backups"
        New-Item -ItemType Directory -Path $backupDir -Force | Out-Null

        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        Copy-Item $target (Join-Path $backupDir "$name.$stamp.bak") -Force
        Write-Warn "$name geback-upt voordat het template wordt teruggezet"
    }

    Copy-Item $source $target -Force
    Write-Ok "$name aangemaakt vanuit conf/templates"
}

Write-Step "Lokale secrets controleren"

$secretsDir = Join-Path $repoRoot "solutionfiles/secrets"
$secretsPath = Join-Path $secretsDir "secret.json"
New-Item -ItemType Directory -Path $secretsDir -Force | Out-Null

function Test-ValidServiceToken {
    param([string]$Token)

    if ([string]::IsNullOrWhiteSpace($Token)) {
        return $false
    }

    try {
        $decoded = [Convert]::FromBase64String($Token)
    }
    catch {
        return $false
    }

    return $decoded.Length -ge 32
}

$effectiveSaPassword = $null

if (Test-Path $secretsPath) {
    # Idempotent migration path: an existing secret.json is never blindly overwritten.
    # LoginDb/CharDb/SqlServer.SaPassword and any existing valid ServiceAuthentication
    # tokens are preserved as-is; only a genuinely missing/invalid MapServer token is
    # generated, and legacy CharServer/MapServer UserId/Password blocks are dropped.
    try {
        $existingSecrets = Get-Content $secretsPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "Bestaande secret.json is geen geldige JSON: $($_.Exception.Message)"
    }

    $effectiveSaPassword = $existingSecrets.SqlServer.SaPassword
    if ([string]::IsNullOrWhiteSpace($effectiveSaPassword)) {
        throw "Bestaande secret.json mist SqlServer.SaPassword - kan niet veilig migreren."
    }

    $existingCharToken = $null
    $existingMapToken = $null
    if ($existingSecrets.PSObject.Properties.Name -contains "ServiceAuthentication") {
        $auth = $existingSecrets.ServiceAuthentication
        if ($auth.PSObject.Properties.Name -contains "CharServer") {
            $existingCharToken = $auth.CharServer.Token
        }
        if ($auth.PSObject.Properties.Name -contains "MapServer") {
            $existingMapToken = $auth.MapServer.Token
        }
    }

    $needsMigration = $false

    if (-not (Test-ValidServiceToken $existingCharToken)) {
        Write-Warn "ServiceAuthentication.CharServer.Token ontbreekt of is ongeldig - genereer een nieuwe."
        $existingCharToken = New-ServiceToken
        $needsMigration = $true
    }

    if (-not (Test-ValidServiceToken $existingMapToken)) {
        Write-Warn "ServiceAuthentication.MapServer.Token ontbreekt of is ongeldig - genereer een nieuwe."
        $existingMapToken = New-ServiceToken
        $needsMigration = $true
    }

    $hasLegacyCharCreds = $existingSecrets.PSObject.Properties.Name -contains "CharServer"
    $hasLegacyMapCreds = $existingSecrets.PSObject.Properties.Name -contains "MapServer"
    if ($hasLegacyCharCreds -or $hasLegacyMapCreds) {
        Write-Warn "Legacy CharServer/MapServer UserId/Password blokken gevonden - deze worden verwijderd."
        $needsMigration = $true
    }

    if ($needsMigration) {
        $backupDir = Join-Path $repoRoot ".local/backups"
        New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        Copy-Item $secretsPath (Join-Path $backupDir "secret.json.$stamp.bak") -Force
        Write-Warn "Backup van bestaande secret.json gemaakt voordat wordt gemigreerd."

        $loginDb = if ($existingSecrets.PSObject.Properties.Name -contains "LoginDb") { $existingSecrets.LoginDb } else { $null }
        $charDb = if ($existingSecrets.PSObject.Properties.Name -contains "CharDb") { $existingSecrets.CharDb } else { $null }

        $migrated = [ordered]@{
            LoginDb = $loginDb
            CharDb = $charDb
            SqlServer = [ordered]@{
                SaPassword = $effectiveSaPassword
            }
            ServiceAuthentication = [ordered]@{
                CharServer = [ordered]@{
                    Token = $existingCharToken
                }
                MapServer = [ordered]@{
                    Token = $existingMapToken
                }
            }
        }

        $migrated | ConvertTo-Json -Depth 8 | Set-Content -Path $secretsPath -Encoding UTF8
        Write-Ok "solutionfiles/secrets/secret.json gemigreerd naar de nieuwe ServiceToken-structuur (legacy UserId/Password verwijderd)"
    }
    else {
        Write-Ok "Bestaande secret.json behouden (al up-to-date)"
    }
}
else {
    if ([string]::IsNullOrWhiteSpace($SqlSaPassword)) {
        Write-Host ""
        Write-Warn "secret.json ontbreekt."
        Write-Host "    Gebruik hier het OUDE SA-wachtwoord als je een bestaande Aspire/Docker SQL-volume wilt behouden."
        Write-Host "    Gebruik alleen een nieuw wachtwoord als je met een nieuwe database begint."

        $secure = Read-Host "SQL Server SA password" -AsSecureString
        $SqlSaPassword = Get-PlainTextFromSecureString $secure
    }

    if ([string]::IsNullOrWhiteSpace($SqlSaPassword)) {
        throw "SQL Server SA password mag niet leeg zijn."
    }

    $effectiveSaPassword = $SqlSaPassword
    $charServiceToken = New-ServiceToken
    $mapServiceToken = New-ServiceToken

    $loginConnection = New-SqlConnectionString -Database "LoginDb" -Password $effectiveSaPassword
    $charConnection = New-SqlConnectionString -Database "CharDb" -Password $effectiveSaPassword

    $secrets = [ordered]@{
        LoginDb = [ordered]@{
            Provider = "sqlserver"
            ConnectionString = $loginConnection
        }
        CharDb = [ordered]@{
            Provider = "sqlserver"
            ConnectionString = $charConnection
        }
        SqlServer = [ordered]@{
            SaPassword = $effectiveSaPassword
        }
        ServiceAuthentication = [ordered]@{
            CharServer = [ordered]@{
                Token = $charServiceToken
            }
            MapServer = [ordered]@{
                Token = $mapServiceToken
            }
        }
    }

    $secrets | ConvertTo-Json -Depth 8 | Set-Content -Path $secretsPath -Encoding UTF8
    Write-Ok "solutionfiles/secrets/secret.json aangemaakt (ServiceAuthentication.CharServer.Token + ServiceAuthentication.MapServer.Token)"
}

if ([string]::IsNullOrWhiteSpace($effectiveSaPassword)) {
    throw "SqlServer.SaPassword ontbreekt in secret.json."
}

Write-Step "Docker Compose .env synchroniseren"

$docsEnv = Join-Path $repoRoot "docs/.env"
"SA_PASSWORD=$effectiveSaPassword" | Set-Content -Path $docsEnv -Encoding UTF8
Write-Ok "docs/.env aangemaakt/bijgewerkt"

Write-Step "Lokale run-helper maken"

$localDir = Join-Path $repoRoot ".local"
New-Item -ItemType Directory -Path $localDir -Force | Out-Null

$runLocalPath = Join-Path $localDir "run-athena.ps1"
$runLocal = @'
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

# Stock iRO client-side endpoints used by the Athena launcher/proxy.
$env:ATHENA_NET_LOGIN_IRO_CHAR_IP = "198.18.0.1"
$env:ATHENA_NET_LOGIN_IRO_CHAR_PORT = "4500"
$env:ATHENA_NET_CHAR_IRO_MAP_IP = "198.18.0.2"
$env:ATHENA_NET_CHAR_IRO_MAP_PORT = "4501"

dotnet run --project src/AppHost
'@

$runLocal | Set-Content -Path $runLocalPath -Encoding UTF8
Write-Ok ".local/run-athena.ps1 aangemaakt"

Write-Step "Legacy Windows proxy lokaal voorbereiden"

$proxySource = Join-Path $repoRoot "scripts/ragnarok-proxy.ps1"
$proxyTarget = Join-Path $localDir "ragnarok-proxy.ps1"

if (Test-Path $proxySource) {
    $proxy = Get-Content $proxySource -Raw

    # Change every occurrence of the previous LAN backend to this machine.
    $proxy = $proxy -replace '192\.168\.178\.108', [Regex]::Escape($AthenaHost).Replace('\.', '.')

    # Also handle a future changed hardcoded BackendAddress line.
    $proxy = $proxy -replace '\$BackendAddress\s*=\s*"[^"]+"', ('$BackendAddress = "' + $AthenaHost + '"')

    $proxy | Set-Content -Path $proxyTarget -Encoding UTF8
    Write-Ok ".local/ragnarok-proxy.ps1 wijst naar $AthenaHost"
}
else {
    Write-Warn "scripts/ragnarok-proxy.ps1 niet gevonden, legacy proxy overgeslagen"
}

Write-Step "Launcher-instellingen lokaal voorbereiden"

$launcherSettingsSource = Join-Path $repoRoot "tools/launcher/src/Athena.Launcher/launcher.settings.json"
$launcherSettingsTarget = Join-Path $localDir "launcher.settings.json"

if (Test-Path $launcherSettingsSource) {
    $launcherSettings = Get-Content $launcherSettingsSource -Raw | ConvertFrom-Json
    $launcherSettings.AthenaHost = $AthenaHost
    $launcherSettings | ConvertTo-Json -Depth 8 | Set-Content -Path $launcherSettingsTarget -Encoding UTF8

    Write-Ok ".local/launcher.settings.json wijst naar $AthenaHost"
}
else {
    Write-Warn "launcher.settings.json niet gevonden, launcher-config overgeslagen"
}

Write-Step "Tooling controleren"

if (Get-Command "dotnet" -ErrorAction SilentlyContinue) {
    $dotnetVersion = (& dotnet --version).Trim()
    Write-Ok ".NET SDK gevonden: $dotnetVersion"

    $majorText = ($dotnetVersion -split '\.')[0]
    $major = 0
    if ([int]::TryParse($majorText, [ref]$major) -and $major -lt 10) {
        Write-Warn "De huidige documentatie verwacht .NET SDK 10.x."
    }
}
else {
    Write-Warn "dotnet is niet gevonden. Installeer .NET SDK 10.x voordat je Athena start."
}

if (Get-Command "docker" -ErrorAction SilentlyContinue) {
    Write-Ok "Docker gevonden"
}
else {
    Write-Warn "Docker is niet gevonden. Aspire/SQL Server kan daardoor niet volledig starten."
}

Write-Host ""
Write-Host "============================================" -ForegroundColor Green
Write-Host " pAthena local setup gereed" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
Write-Host ""
Write-Host "Server starten:"
Write-Host "  pwsh ./.local/run-athena.ps1" -ForegroundColor White
Write-Host ""
Write-Host "Legacy Windows proxy:"
Write-Host "  .\.local\ragnarok-proxy.ps1 Enable" -ForegroundColor White
Write-Host ""
Write-Host "Nieuwe launcher:"
Write-Host "  kopieer .local/launcher.settings.json naast Athena.Launcher.exe" -ForegroundColor White
Write-Host ""
Write-Host "Belangrijk:"
Write-Host "  - lokale secrets/config staan buiten Git"
Write-Host "  - subnet_athena.conf blijft op de template/default"
Write-Host "  - backend/launcher host staat op $AthenaHost"
Write-Host ""
