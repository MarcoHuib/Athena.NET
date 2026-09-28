#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Provisions a normal player account through ASP.NET Core Identity.

.DESCRIPTION
    Cross-platform (PowerShell 7+, Windows/Linux/macOS) equivalent of
    scripts/create-player-account.sh. Never accepts the password as a
    command-line argument (shell history / process listings such as
    "ps aux" or Get-Process would expose it):
      - Interactive: run with just <username> [M|F] [email], then type the
        password at the hidden "Password:" prompt.
      - Automation: pipe the password on stdin instead (e.g. from a secret
        manager or a protected CI secret) - never place it directly in the
        command line.

.PARAMETER Username
    Login name. Credential policy (minimum length, etc.) is enforced once by
    LoginServer's PlayerAccountProvisioningService (LoginConfig.AccountNameMinLength),
    not duplicated here - this script only rejects an empty value, so the
    policy can never drift between this script and the server.

.PARAMETER Sex
    'M' or 'F'. Defaults to 'M'.

.PARAMETER Email
    Defaults to "<username>@players.athena.local".

.EXAMPLE
    ./create-player-account.ps1 alice F alice@example.com
    Password: ****

.EXAMPLE
    "correct horse battery staple" | ./create-player-account.ps1 alice
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Username,

    [Parameter(Position = 1)]
    [string]$Sex = "M",

    [Parameter(Position = 2)]
    [string]$Email
)

$ErrorActionPreference = "Stop"

$Sex = $Sex.ToUpperInvariant()

if ([string]::IsNullOrEmpty($Username)) {
    Write-Error "Username must not be empty."
    exit 1
}

# Credential policy (minimum/maximum length) is validated once, authoritatively,
# by LoginServer's PlayerAccountProvisioningService.ValidateInput - not
# re-implemented here, so this script's rules can never drift from
# LoginConfig.AccountNameMinLength/PasswordMinLength. A policy violation still
# fails cleanly; it just surfaces as LoginServer's own error message below.

if ($Sex -ne "M" -and $Sex -ne "F") {
    Write-Error "Sex must be M or F."
    exit 1
}

if (-not $Email) {
    $Email = "$Username@players.athena.local"
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "dotnet not found. Please install the .NET SDK."
    exit 1
}

# Read the password ourselves (hidden prompt if this is an interactive
# terminal, otherwise the first line of stdin) rather than accepting it as a
# positional argument, so it never ends up in shell history.
if ([Console]::IsInputRedirected) {
    $Password = [Console]::In.ReadLine()
}
else {
    $SecurePassword = Read-Host -Prompt "Password" -AsSecureString
    $Password = [System.Net.NetworkCredential]::new("", $SecurePassword).Password
}

if ([string]::IsNullOrEmpty($Password)) {
    Write-Error "Password must not be empty."
    exit 1
}

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptDir
$LoginServerProject = Join-Path $RepoRoot "src" "LoginServer"

# Player accounts are ASP.NET Core Identity users (password hashing is Identity's
# PBKDF2-based scheme, not something this script can reproduce with plain SQL).
# This runs LoginServer's own one-shot account-creation mode, which uses the same
# configuration/connection-string resolution (secrets.json, conf/inter_athena.conf,
# ATHENA_NET_LOGIN_DB_CONNECTION) as the running server, so credentials are never
# duplicated here. The password is piped to the child process's stdin, never
# passed as a --create-account-password argument, so it never appears in that
# process's argument list (e.g. "ps aux"/Get-Process) either.
$Password | & dotnet run --project $LoginServerProject --configuration Release -- `
    --create-account-username $Username `
    --create-account-sex $Sex `
    --create-account-email $Email

exit $LASTEXITCODE
