param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Enable", "Disable", "Status", "Restart")]
    [string]$Action
)

# ============================================================
# Ragnarok / iRO PortProxy
#
# LOGIN:
#   128.241.92.36:6800 -> 192.168.178.59:6900
#
# CHAR:
#   128.241.92.43:4500 -> 192.168.178.59:6121
#
# MAP:
#   128.241.92.42:4501 -> 192.168.178.59:5121
# ============================================================

$InterfaceAlias = "vEthernet (Default Switch)"

$ProxyRules = @(
    @{
        Name           = "Ragnarok LOGIN"
        ListenAddress  = "128.241.92.36"
        ListenPort     = 6800
        ConnectAddress = "192.168.178.59"
        ConnectPort    = 6900
    },
    @{
        Name           = "Ragnarok CHAR"
        ListenAddress  = "128.241.92.43"
        ListenPort     = 4500
        ConnectAddress = "192.168.178.59"
        ConnectPort    = 6121
    },
    @{
        Name           = "Ragnarok MAP"
        ListenAddress  = "128.241.92.42"
        ListenPort     = 4501
        ConnectAddress = "192.168.178.59"
        ConnectPort    = 5121
    }
)

# ============================================================
# Administrator check
# ============================================================

$CurrentUser = [Security.Principal.WindowsIdentity]::GetCurrent()
$Principal = New-Object Security.Principal.WindowsPrincipal($CurrentUser)

if (-not $Principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host ""
    Write-Host "ERROR: Start PowerShell als Administrator." -ForegroundColor Red
    Write-Host ""
    exit 1
}

# ============================================================
# Helper: IP Helper restart
# ============================================================

function Restart-PortProxyService {

    Write-Host ""
    Write-Host "IP Helper controleren..." -ForegroundColor Yellow

    Set-Service iphlpsvc -StartupType Automatic

    $Service = Get-Service iphlpsvc

    if ($Service.Status -eq "Running") {
        Write-Host "IP Helper opnieuw starten..." -ForegroundColor Yellow
        Restart-Service iphlpsvc -Force
    }
    else {
        Write-Host "IP Helper starten..." -ForegroundColor Yellow
        Start-Service iphlpsvc
    }

    Start-Sleep -Seconds 2

    $Service = Get-Service iphlpsvc

    if ($Service.Status -eq "Running") {
        Write-Host "IP Helper draait." -ForegroundColor Green
    }
    else {
        Write-Host "IP Helper draait NIET." -ForegroundColor Red
    }
}

# ============================================================
# Helper: huidige PortProxy output
# ============================================================

function Get-PortProxyRules {

    return @(netsh interface portproxy show v4tov4)
}

# ============================================================
# Helper: IP-adressen controleren/toevoegen
# ============================================================

function Ensure-ProxyIPs {

    Write-Host ""
    Write-Host "Lokale iRO IP-adressen controleren..." -ForegroundColor Cyan

    foreach ($Rule in $ProxyRules) {

        $ExistingIP = Get-NetIPAddress `
            -InterfaceAlias $InterfaceAlias `
            -IPAddress $Rule.ListenAddress `
            -ErrorAction SilentlyContinue

        if ($ExistingIP) {

            Write-Host "  [OK] $($Rule.ListenAddress) bestaat al." -ForegroundColor DarkGray

        }
        else {

            Write-Host "  [ADD] $($Rule.ListenAddress) toevoegen..." -ForegroundColor Yellow

            New-NetIPAddress `
                -InterfaceAlias $InterfaceAlias `
                -IPAddress $Rule.ListenAddress `
                -PrefixLength 32 `
                -SkipAsSource $true `
                -ErrorAction Stop | Out-Null

            Write-Host "  [OK] $($Rule.ListenAddress) toegevoegd." -ForegroundColor Green
        }
    }
}

# ============================================================
# Helper: PortProxy-regels controleren/toevoegen
# ============================================================

function Ensure-ProxyRules {

    Write-Host ""
    Write-Host "PortProxy-regels controleren..." -ForegroundColor Cyan

    foreach ($Rule in $ProxyRules) {

        $CurrentRules = Get-PortProxyRules

        $ListenAddressEscaped = [regex]::Escape($Rule.ListenAddress)
        $ConnectAddressEscaped = [regex]::Escape($Rule.ConnectAddress)

        # Exacte verwachte regel
        $ExpectedPattern = "^\s*$ListenAddressEscaped\s+$($Rule.ListenPort)\s+$ConnectAddressEscaped\s+$($Rule.ConnectPort)\s*$"

        # Iedere regel met hetzelfde listen IP + poort
        $ListenPattern = "^\s*$ListenAddressEscaped\s+$($Rule.ListenPort)\s+"

        $ExactRuleExists = $false
        $ListenRuleExists = $false

        foreach ($Line in $CurrentRules) {

            if ($Line -match $ExpectedPattern) {
                $ExactRuleExists = $true
            }

            if ($Line -match $ListenPattern) {
                $ListenRuleExists = $true
            }
        }

        # Een netsh-regel kan correct geconfigureerd zijn terwijl de
        # daadwerkelijke TCP-listener ontbreekt. Controleer daarom beide.
        $Listener = Get-NetTCPConnection `
            -LocalAddress $Rule.ListenAddress `
            -LocalPort $Rule.ListenPort `
            -State Listen `
            -ErrorAction SilentlyContinue

        if ($ExactRuleExists -and $Listener) {

            $Message = "  [OK] $($Rule.Name): $($Rule.ListenAddress):$($Rule.ListenPort) -> $($Rule.ConnectAddress):$($Rule.ConnectPort) (LISTENING)"
            Write-Host $Message -ForegroundColor DarkGray
            continue
        }

        if ($ExactRuleExists -and -not $Listener) {

            Write-Host "  [FIX] $($Rule.Name) is geconfigureerd maar luistert niet; regel opnieuw aanmaken." -ForegroundColor Yellow

            netsh interface portproxy delete v4tov4 `
                listenaddress=$($Rule.ListenAddress) `
                listenport=$($Rule.ListenPort) | Out-Null
        }
        elseif ($ListenRuleExists) {

            # Er staat wel iets op deze listen-address/poort,
            # maar niet naar de juiste destination.
            Write-Host "  [FIX] $($Rule.Name) heeft een afwijkende regel." -ForegroundColor Yellow

            netsh interface portproxy delete v4tov4 `
                listenaddress=$($Rule.ListenAddress) `
                listenport=$($Rule.ListenPort) | Out-Null
        }
        else {

            Write-Host "  [ADD] $($Rule.Name) ontbreekt." -ForegroundColor Yellow
        }

        netsh interface portproxy add v4tov4 `
            listenaddress=$($Rule.ListenAddress) `
            listenport=$($Rule.ListenPort) `
            connectaddress=$($Rule.ConnectAddress) `
            connectport=$($Rule.ConnectPort) | Out-Null

        Start-Sleep -Milliseconds 250

        $Listener = Get-NetTCPConnection `
            -LocalAddress $Rule.ListenAddress `
            -LocalPort $Rule.ListenPort `
            -State Listen `
            -ErrorAction SilentlyContinue

        if ($Listener) {
            $Message = "  [OK] $($Rule.ListenAddress):$($Rule.ListenPort) -> $($Rule.ConnectAddress):$($Rule.ConnectPort) (LISTENING)"
            Write-Host $Message -ForegroundColor Green
        }
        else {
            $Message = "  [FOUT] $($Rule.ListenAddress):$($Rule.ListenPort) is geconfigureerd maar luistert nog niet."
            Write-Host $Message -ForegroundColor Red
        }
    }
}

# ============================================================
# Helper: onze PortProxy-regels verwijderen
# ============================================================

function Remove-ProxyRules {

    foreach ($Rule in $ProxyRules) {

        netsh interface portproxy delete v4tov4 `
            listenaddress=$($Rule.ListenAddress) `
            listenport=$($Rule.ListenPort) 2>$null | Out-Null
    }
}

# ============================================================
# Helper: verbinding testen
# ============================================================

function Test-Proxy {

    Write-Host ""
    Write-Host "=== Verbindingen controleren ===" -ForegroundColor Cyan

    foreach ($Rule in $ProxyRules) {

        Write-Host ""
        Write-Host $Rule.Name -ForegroundColor Yellow

        # ----------------------------------------
        # Backend op Mac
        # ----------------------------------------

        $Backend = Test-NetConnection `
            $Rule.ConnectAddress `
            -Port $Rule.ConnectPort `
            -WarningAction SilentlyContinue

        if ($Backend.TcpTestSucceeded) {

            $Message = "  Backend OK   : $($Rule.ConnectAddress):$($Rule.ConnectPort)"
            Write-Host $Message -ForegroundColor Green

        }
        else {

            $Message = "  Backend FOUT : $($Rule.ConnectAddress):$($Rule.ConnectPort)"
            Write-Host $Message -ForegroundColor Red
        }

        # ----------------------------------------
        # Windows PortProxy
        # ----------------------------------------

        $Proxy = Test-NetConnection `
            $Rule.ListenAddress `
            -Port $Rule.ListenPort `
            -WarningAction SilentlyContinue

        if ($Proxy.TcpTestSucceeded) {

            $Message = "  Proxy OK     : $($Rule.ListenAddress):$($Rule.ListenPort)"
            Write-Host $Message -ForegroundColor Green

        }
        else {

            $Message = "  Proxy FOUT   : $($Rule.ListenAddress):$($Rule.ListenPort)"
            Write-Host $Message -ForegroundColor Red
        }
    }
}

# ============================================================
# ENABLE
# ============================================================

function Enable-RagnarokProxy {

    Write-Host ""
    Write-Host "============================================" -ForegroundColor Cyan
    Write-Host " Ragnarok Proxy INSCHAKELEN" -ForegroundColor Cyan
    Write-Host "============================================" -ForegroundColor Cyan

    # Alleen ontbrekende lokale IP's toevoegen
    Ensure-ProxyIPs

    # Zorg eerst dat IP Helper schoon draait. Daarna controleert
    # Ensure-ProxyRules zowel de configuratie als de echte listener.
    Restart-PortProxyService

    # Ontbrekende/verkeerde regels én 'configured but not listening' herstellen.
    Ensure-ProxyRules

    Write-Host ""
    Write-Host "Actieve PortProxy-regels:" -ForegroundColor Cyan
    netsh interface portproxy show v4tov4

    Test-Proxy

    Write-Host ""
    Write-Host "============================================" -ForegroundColor Green
    Write-Host " Ragnarok Proxy INGESCHAKELD" -ForegroundColor Green
    Write-Host "============================================" -ForegroundColor Green
}

# ============================================================
# DISABLE
# ============================================================

function Disable-RagnarokProxy {

    Write-Host ""
    Write-Host "============================================" -ForegroundColor Cyan
    Write-Host " Ragnarok Proxy UITSCHAKELEN" -ForegroundColor Cyan
    Write-Host "============================================" -ForegroundColor Cyan

    Write-Host ""
    Write-Host "PortProxy-regels verwijderen..." -ForegroundColor Yellow

    Remove-ProxyRules

    Write-Host ""
    Write-Host "Lokale iRO IP-adressen verwijderen..." -ForegroundColor Yellow

    foreach ($Rule in $ProxyRules) {

        $ExistingIP = Get-NetIPAddress `
            -InterfaceAlias $InterfaceAlias `
            -IPAddress $Rule.ListenAddress `
            -ErrorAction SilentlyContinue

        if ($ExistingIP) {

            Remove-NetIPAddress `
                -InterfaceAlias $InterfaceAlias `
                -IPAddress $Rule.ListenAddress `
                -Confirm:$false `
                -ErrorAction SilentlyContinue

            Write-Host "  [REMOVED] $($Rule.ListenAddress)" -ForegroundColor Green
        }
        else {

            Write-Host "  [SKIP] $($Rule.ListenAddress) was niet aanwezig." -ForegroundColor DarkGray
        }
    }

    Restart-PortProxyService

    Write-Host ""
    Write-Host "============================================" -ForegroundColor Green
    Write-Host " Ragnarok Proxy UITGESCHAKELD" -ForegroundColor Green
    Write-Host " Originele iRO servers weer bereikbaar." -ForegroundColor Green
    Write-Host "============================================" -ForegroundColor Green
}

# ============================================================
# STATUS
# ============================================================

function Show-RagnarokProxyStatus {

    Write-Host ""
    Write-Host "============================================" -ForegroundColor Cyan
    Write-Host " Ragnarok Proxy STATUS" -ForegroundColor Cyan
    Write-Host "============================================" -ForegroundColor Cyan

    Write-Host ""
    Write-Host "IP Helper:" -ForegroundColor Yellow

    Get-Service iphlpsvc |
        Format-Table Status, Name, DisplayName

    # ----------------------------------------
    # Lokale IP-adressen
    # ----------------------------------------

    Write-Host ""
    Write-Host "Lokale iRO IP-adressen:" -ForegroundColor Yellow

    foreach ($Rule in $ProxyRules) {

        $IP = Get-NetIPAddress `
            -InterfaceAlias $InterfaceAlias `
            -IPAddress $Rule.ListenAddress `
            -ErrorAction SilentlyContinue

        if ($IP) {

            Write-Host "  [OK] $($Rule.ListenAddress)" -ForegroundColor Green

        }
        else {

            Write-Host "  [FOUT] $($Rule.ListenAddress) ontbreekt" -ForegroundColor Red
        }
    }

    # ----------------------------------------
    # PortProxy regels
    # ----------------------------------------

    Write-Host ""
    Write-Host "PortProxy-regels:" -ForegroundColor Yellow

    netsh interface portproxy show v4tov4

    # ----------------------------------------
    # TCP listeners
    # ----------------------------------------

    Write-Host ""
    Write-Host "TCP listeners:" -ForegroundColor Yellow

    foreach ($Rule in $ProxyRules) {

        $Listener = Get-NetTCPConnection `
            -LocalAddress $Rule.ListenAddress `
            -LocalPort $Rule.ListenPort `
            -State Listen `
            -ErrorAction SilentlyContinue

        if ($Listener) {

            $Message = "  [OK] $($Rule.ListenAddress):$($Rule.ListenPort) LISTENING"
            Write-Host $Message -ForegroundColor Green

        }
        else {

            $Message = "  [FOUT] $($Rule.ListenAddress):$($Rule.ListenPort) NIET LISTENING"
            Write-Host $Message -ForegroundColor Red
        }
    }

    Test-Proxy
}

# ============================================================
# RESTART
#
# Bewust anders dan Enable:
# bouwt alle drie de PortProxy-regels opnieuw op.
# De lokale IP-adressen blijven staan.
# ============================================================

function Restart-RagnarokProxy {

    Write-Host ""
    Write-Host "============================================" -ForegroundColor Cyan
    Write-Host " Ragnarok Proxy HERSTARTEN" -ForegroundColor Cyan
    Write-Host "============================================" -ForegroundColor Cyan

    Ensure-ProxyIPs

    Write-Host ""
    Write-Host "PortProxy-regels opnieuw opbouwen..." -ForegroundColor Yellow

    Remove-ProxyRules

    Restart-PortProxyService

    Ensure-ProxyRules

    Restart-PortProxyService

    Write-Host ""
    Write-Host "Actieve PortProxy-regels:" -ForegroundColor Cyan
    netsh interface portproxy show v4tov4

    Test-Proxy
}

# ============================================================
# Execute
# ============================================================

switch ($Action) {

    "Enable" {
        Enable-RagnarokProxy
    }

    "Disable" {
        Disable-RagnarokProxy
    }

    "Status" {
        Show-RagnarokProxyStatus
    }

    "Restart" {
        Restart-RagnarokProxy
    }
}