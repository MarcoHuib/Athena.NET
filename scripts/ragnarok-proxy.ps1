param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Enable", "Disable", "Status", "Restart")]
    [string]$Action
)

# ============================================================
# Athena.NET / Ragnarok iRO PortProxy
#
# Geen Hyper-V nodig.
# Geen New-NetIPAddress.
#
# LOGIN:
#   128.241.92.36:6800 -> 192.168.178.108:6900
#
# CHAR:
#   128.241.92.43:4500 -> 192.168.178.108:6121
#
# MAP:
#   128.241.92.42:4501 -> 192.168.178.108:5121
#
# De tijdelijke iRO-IP's worden via de native Windows
# IP Helper API toegevoegd:
#
#   CreateUnicastIpAddressEntry
#   DeleteUnicastIpAddressEntry
#
# Daardoor wordt DHCP op Wi-Fi/Ethernet niet uitgezet.
# ============================================================

$BackendAddress = "192.168.178.108"

$ProxyRules = @(
    @{
        Name           = "Ragnarok LOGIN"
        ListenAddress  = "128.241.92.36"
        ListenPort     = 6800
        ConnectAddress = $BackendAddress
        ConnectPort    = 6900
    },
    @{
        Name           = "Ragnarok CHAR"
        ListenAddress  = "128.241.92.43"
        ListenPort     = 4500
        ConnectAddress = $BackendAddress
        ConnectPort    = 6121
    },
    @{
        Name           = "Ragnarok MAP"
        ListenAddress  = "128.241.92.42"
        ListenPort     = 4501
        ConnectAddress = $BackendAddress
        ConnectPort    = 5121
    }
)

# ============================================================
# Administrator check
# ============================================================

$CurrentUser = [Security.Principal.WindowsIdentity]::GetCurrent()

$Principal = New-Object `
    Security.Principal.WindowsPrincipal($CurrentUser)

if (-not $Principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator
    )) {

    Write-Host ""
    Write-Host "ERROR: Start PowerShell als Administrator." `
        -ForegroundColor Red
    Write-Host ""

    exit 1
}

# ============================================================
# Native Windows IP Helper API
# ============================================================

function Initialize-NativeIpApi {

    if (([System.Management.Automation.PSTypeName]'Athena.NativeIp').Type) {
        return
    }

    Write-Host ""
    Write-Host "Native Windows IP Helper API laden..." `
        -ForegroundColor Cyan

    $NativeCode = @'
using System;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Athena
{
    public static class NativeIp
    {
        private const ushort AF_INET = 2;
        private const byte IP_DAD_STATE_PREFERRED = 4;

        // ----------------------------------------------------
        // SOCKADDR_IN
        // ----------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct SOCKADDR_IN
        {
            public ushort sin_family;
            public ushort sin_port;
            public uint sin_addr;
            public ulong sin_zero;
        }

        // ----------------------------------------------------
        // SOCKADDR_INET
        //
        // Native union:
        //   SOCKADDR_IN
        //   SOCKADDR_IN6
        //   ADDRESS_FAMILY
        //
        // SOCKADDR_IN6 is 28 bytes, therefore the union
        // itself is 28 bytes.
        // ----------------------------------------------------

        [StructLayout(LayoutKind.Explicit, Size = 28)]
        private struct SOCKADDR_INET
        {
            [FieldOffset(0)]
            public SOCKADDR_IN Ipv4;

            [FieldOffset(0)]
            public ushort si_family;
        }

        // ----------------------------------------------------
        // MIB_UNICASTIPADDRESS_ROW
        // ----------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_UNICASTIPADDRESS_ROW
        {
            public SOCKADDR_INET Address;

            public ulong InterfaceLuid;
            public uint InterfaceIndex;

            public int PrefixOrigin;
            public int SuffixOrigin;

            public uint ValidLifetime;
            public uint PreferredLifetime;

            public byte OnLinkPrefixLength;
            public byte SkipAsSource;

            public int DadState;

            public uint ScopeId;

            public long CreationTimeStamp;
        }

        // ----------------------------------------------------
        // Native methods
        // ----------------------------------------------------

        [DllImport(
            "iphlpapi.dll",
            ExactSpelling = true,
            CallingConvention = CallingConvention.Winapi
        )]
        private static extern void InitializeUnicastIpAddressEntry(
            ref MIB_UNICASTIPADDRESS_ROW Row
        );

        [DllImport(
            "iphlpapi.dll",
            ExactSpelling = true,
            CallingConvention = CallingConvention.Winapi
        )]
        private static extern uint CreateUnicastIpAddressEntry(
            ref MIB_UNICASTIPADDRESS_ROW Row
        );

        [DllImport(
            "iphlpapi.dll",
            ExactSpelling = true,
            CallingConvention = CallingConvention.Winapi
        )]
        private static extern uint DeleteUnicastIpAddressEntry(
            ref MIB_UNICASTIPADDRESS_ROW Row
        );

        // ----------------------------------------------------
        // Struct validation
        // ----------------------------------------------------

        private static void ValidateLayout()
        {
            int size = Marshal.SizeOf(
                typeof(MIB_UNICASTIPADDRESS_ROW)
            );

            // MIB_UNICASTIPADDRESS_ROW is 80 bytes on
            // supported modern Windows layouts.
            if (size != 80)
            {
                throw new InvalidOperationException(
                    "Unexpected MIB_UNICASTIPADDRESS_ROW size: " +
                    size +
                    " bytes."
                );
            }
        }

        // ----------------------------------------------------
        // IPv4 address -> SOCKADDR_INET
        // ----------------------------------------------------

        private static SOCKADDR_INET CreateSockaddr(
            string address
        )
        {
            IPAddress ip = IPAddress.Parse(address);

            if (ip.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new ArgumentException(
                    "Only IPv4 addresses are supported."
                );
            }

            byte[] bytes = ip.GetAddressBytes();

            SOCKADDR_IN ipv4 = new SOCKADDR_IN();

            ipv4.sin_family = AF_INET;
            ipv4.sin_port = 0;

            // Windows expects sin_addr in network byte order.
            //
            // On little-endian Windows, converting the address
            // bytes this way produces the correct byte layout
            // in unmanaged memory.
            ipv4.sin_addr = BitConverter.ToUInt32(bytes, 0);

            ipv4.sin_zero = 0;

            SOCKADDR_INET result =
                new SOCKADDR_INET();

            result.Ipv4 = ipv4;

            return result;
        }

        // ----------------------------------------------------
        // Create initialized row
        // ----------------------------------------------------

        private static MIB_UNICASTIPADDRESS_ROW CreateRow(
            uint interfaceIndex,
            string address
        )
        {
            ValidateLayout();

            MIB_UNICASTIPADDRESS_ROW row =
                new MIB_UNICASTIPADDRESS_ROW();

            InitializeUnicastIpAddressEntry(ref row);

            row.Address = CreateSockaddr(address);

            // InterfaceLuid remains zero.
            // Windows therefore uses InterfaceIndex.
            row.InterfaceIndex = interfaceIndex;

            row.OnLinkPrefixLength = 32;

            // Do not use these addresses as the source
            // address for normal outgoing traffic.
            row.SkipAsSource = 1;

            // On Windows 10+ this allows the address to become
            // available immediately while Windows performs
            // optimistic duplicate-address detection.
            row.DadState = IP_DAD_STATE_PREFERRED;

            return row;
        }

        // ----------------------------------------------------
        // Add temporary address
        // ----------------------------------------------------

        public static void Add(
            uint interfaceIndex,
            string address
        )
        {
            MIB_UNICASTIPADDRESS_ROW row =
                CreateRow(interfaceIndex, address);

            uint result =
                CreateUnicastIpAddressEntry(ref row);

            if (result != 0)
            {
                throw new Win32Exception(
                    (int)result,
                    "CreateUnicastIpAddressEntry failed for " +
                    address
                );
            }
        }

        // ----------------------------------------------------
        // Remove temporary address
        // ----------------------------------------------------

        public static void Remove(
            uint interfaceIndex,
            string address
        )
        {
            MIB_UNICASTIPADDRESS_ROW row =
                CreateRow(interfaceIndex, address);

            uint result =
                DeleteUnicastIpAddressEntry(ref row);

            // ERROR_NOT_FOUND
            if (result == 1168)
            {
                return;
            }

            if (result != 0)
            {
                throw new Win32Exception(
                    (int)result,
                    "DeleteUnicastIpAddressEntry failed for " +
                    address
                );
            }
        }
    }
}
'@

    Add-Type `
        -TypeDefinition $NativeCode `
        -Language CSharp `
        -ErrorAction Stop

    Write-Host "Native IP Helper API geladen." `
        -ForegroundColor Green
}

# ============================================================
# Bepaal interface richting Athena.NET
#
# We gebruiken niet simpelweg "Wi-Fi".
#
# Windows bepaalt zelf welke lokale interface en welk lokaal
# adres gebruikt wordt om 192.168.178.108 te bereiken.
# ============================================================

function Get-AthenaNetworkInterface {

    $Result = @(
        Find-NetRoute `
            -RemoteIPAddress $BackendAddress `
            -ErrorAction Stop
    )

    # Find-NetRoute retourneert:
    #   1. NetIPAddress
    #   2. NetRoute
    #
    # Zoek het object met een IPAddress property.

    $SourceAddress = $Result |
        Where-Object {
            $_.PSObject.Properties.Name -contains "IPAddress" -and
            $_.IPAddress
        } |
        Select-Object -First 1

    if (-not $SourceAddress) {
        throw "Geen geldige route naar $BackendAddress gevonden."
    }

    $Adapter = Get-NetAdapter `
        -InterfaceIndex $SourceAddress.InterfaceIndex `
        -ErrorAction Stop

    if ($Adapter.Status -ne "Up") {
        throw "Netwerkadapter '$($Adapter.Name)' is niet actief."
    }

    return [PSCustomObject]@{
        InterfaceIndex = [uint32]$SourceAddress.InterfaceIndex
        InterfaceAlias = $SourceAddress.InterfaceAlias
        SourceAddress   = $SourceAddress.IPAddress
        Adapter         = $Adapter
    }
}

# ============================================================
# Veiligheidssnapshot
# ============================================================

function Get-InterfaceSnapshot {

    param(
        [Parameter(Mandatory = $true)]
        [uint32]$InterfaceIndex,

        [Parameter(Mandatory = $true)]
        [string]$SourceAddress
    )

    $IpInterface = Get-NetIPInterface `
        -InterfaceIndex $InterfaceIndex `
        -AddressFamily IPv4 `
        -ErrorAction Stop

    $Configuration = Get-NetIPConfiguration `
        -InterfaceIndex $InterfaceIndex `
        -ErrorAction Stop

    $Gateway = $null

    if ($Configuration.IPv4DefaultGateway) {
        $Gateway =
            $Configuration.IPv4DefaultGateway.NextHop
    }

    return [PSCustomObject]@{
        Dhcp          = $IpInterface.Dhcp
        SourceAddress = $SourceAddress
        Gateway       = $Gateway
    }
}

# ============================================================
# Controleer dat we DHCP NIET hebben aangeraakt
# ============================================================

function Confirm-InterfaceUnchanged {

    param(
        [Parameter(Mandatory = $true)]
        $Before,

        [Parameter(Mandatory = $true)]
        [uint32]$InterfaceIndex
    )

    $Current = Get-NetIPInterface `
        -InterfaceIndex $InterfaceIndex `
        -AddressFamily IPv4 `
        -ErrorAction Stop

    if ($Current.Dhcp -ne $Before.Dhcp) {

        throw @"
VEILIGHEIDSCONTROLE MISLUKT.

DHCP-status is veranderd:
Voor : $($Before.Dhcp)
Na   : $($Current.Dhcp)

Het script stopt.
"@
    }

    $OriginalAddress = Get-NetIPAddress `
        -InterfaceIndex $InterfaceIndex `
        -IPAddress $Before.SourceAddress `
        -AddressFamily IPv4 `
        -ErrorAction SilentlyContinue

    if (-not $OriginalAddress) {

        throw @"
VEILIGHEIDSCONTROLE MISLUKT.

Het oorspronkelijke IPv4-adres:
$($Before.SourceAddress)

is verdwenen.

Het script stopt.
"@
    }

    Write-Host ""
    Write-Host "Netwerkveiligheidscontrole:" `
        -ForegroundColor Cyan

    Write-Host "  [OK] DHCP blijft $($Current.Dhcp)" `
        -ForegroundColor Green

    Write-Host "  [OK] Primair IPv4 blijft $($Before.SourceAddress)" `
        -ForegroundColor Green

    if ($Before.Gateway) {
        Write-Host "  [OK] Gateway was $($Before.Gateway)" `
            -ForegroundColor Green
    }
}

# ============================================================
# IP Helper service
# ============================================================

function Restart-PortProxyService {

    Write-Host ""
    Write-Host "IP Helper controleren..." `
        -ForegroundColor Yellow

    Set-Service `
        iphlpsvc `
        -StartupType Automatic

    $Service = Get-Service iphlpsvc

    if ($Service.Status -eq "Running") {

        Write-Host "IP Helper opnieuw starten..." `
            -ForegroundColor Yellow

        Restart-Service `
            iphlpsvc `
            -Force
    }
    else {

        Write-Host "IP Helper starten..." `
            -ForegroundColor Yellow

        Start-Service iphlpsvc
    }

    Start-Sleep -Seconds 1

    $Service = Get-Service iphlpsvc

    if ($Service.Status -eq "Running") {

        Write-Host "IP Helper draait." `
            -ForegroundColor Green
    }
    else {

        throw "IP Helper service kon niet worden gestart."
    }
}

# ============================================================
# PortProxy output
# ============================================================

function Get-PortProxyRules {

    return @(
        netsh interface portproxy show v4tov4
    )
}

# ============================================================
# Wacht totdat tijdelijk IP zichtbaar/bruikbaar is
# ============================================================

function Wait-NativeIPAddress {

    param(
        [Parameter(Mandatory = $true)]
        [uint32]$InterfaceIndex,

        [Parameter(Mandatory = $true)]
        [string]$IPAddress
    )

    for ($Attempt = 0; $Attempt -lt 20; $Attempt++) {

        $IP = Get-NetIPAddress `
            -InterfaceIndex $InterfaceIndex `
            -IPAddress $IPAddress `
            -AddressFamily IPv4 `
            -ErrorAction SilentlyContinue

        if ($IP) {

            if (
                $IP.AddressState -eq "Preferred" -or
                $IP.AddressState -eq "Tentative"
            ) {
                return
            }
        }

        Start-Sleep -Milliseconds 250
    }

    throw "IP-adres $IPAddress werd niet actief."
}

# ============================================================
# Tijdelijke proxy-IP's toevoegen
# ============================================================

function Ensure-ProxyIPs {

    param(
        [Parameter(Mandatory = $true)]
        [uint32]$InterfaceIndex,

        [Parameter(Mandatory = $true)]
        [string]$InterfaceAlias
    )

    Write-Host ""
    Write-Host "Tijdelijke iRO IP-adressen controleren..." `
        -ForegroundColor Cyan

    foreach ($Rule in $ProxyRules) {

        $Existing = @(
            Get-NetIPAddress `
                -IPAddress $Rule.ListenAddress `
                -AddressFamily IPv4 `
                -ErrorAction SilentlyContinue
        )

        if ($Existing.Count -gt 0) {

            $CorrectInterface = $Existing |
                Where-Object {
                    $_.InterfaceIndex -eq $InterfaceIndex
                } |
                Select-Object -First 1

            if ($CorrectInterface) {

                Write-Host `
                    "  [OK] $($Rule.ListenAddress) bestaat al op $InterfaceAlias." `
                    -ForegroundColor DarkGray

                continue
            }

            $ExistingDescription =
                ($Existing |
                    ForEach-Object {
                        "$($_.InterfaceAlias) (index $($_.InterfaceIndex))"
                    }) -join ", "

            throw @"
$($Rule.ListenAddress) bestaat al op een andere netwerkadapter:

$ExistingDescription

Voer eerst uit:

.\ragnarok-proxy.ps1 Disable

en probeer daarna opnieuw.
"@
        }

        Write-Host `
            "  [ADD] $($Rule.ListenAddress)/32 op $InterfaceAlias..." `
            -ForegroundColor Yellow

        [Athena.NativeIp]::Add(
            $InterfaceIndex,
            $Rule.ListenAddress
        )

        Wait-NativeIPAddress `
            -InterfaceIndex $InterfaceIndex `
            -IPAddress $Rule.ListenAddress

        Write-Host `
            "  [OK] $($Rule.ListenAddress) tijdelijk toegevoegd." `
            -ForegroundColor Green
    }
}

# ============================================================
# Tijdelijke proxy-IP's verwijderen
# ============================================================

function Remove-ProxyIPs {

    Write-Host ""
    Write-Host "Tijdelijke iRO IP-adressen verwijderen..." `
        -ForegroundColor Yellow

    foreach ($Rule in $ProxyRules) {

        $Existing = @(
            Get-NetIPAddress `
                -IPAddress $Rule.ListenAddress `
                -AddressFamily IPv4 `
                -ErrorAction SilentlyContinue
        )

        if ($Existing.Count -eq 0) {

            Write-Host `
                "  [SKIP] $($Rule.ListenAddress) was niet aanwezig." `
                -ForegroundColor DarkGray

            continue
        }

        foreach ($Entry in $Existing) {

            # We verwijderen alleen het type adres dat Athena gebruikt.
            #
            # Hierdoor vermijden we dat we per ongeluk een normaal
            # netwerkadres verwijderen.

            if (
                $Entry.PrefixLength -ne 32 -or
                -not $Entry.SkipAsSource
            ) {

                Write-Host `
                    "  [SKIP] $($Rule.ListenAddress) op $($Entry.InterfaceAlias) lijkt niet van Athena te zijn." `
                    -ForegroundColor Yellow

                continue
            }

            Write-Host `
                "  [REMOVE] $($Rule.ListenAddress) van $($Entry.InterfaceAlias)..." `
                -ForegroundColor Yellow

            try {

                [Athena.NativeIp]::Remove(
                    [uint32]$Entry.InterfaceIndex,
                    $Rule.ListenAddress
                )

                Write-Host `
                    "  [REMOVED] $($Rule.ListenAddress)" `
                    -ForegroundColor Green
            }
            catch {

                Write-Host `
                    "  [FOUT] $($Rule.ListenAddress): $($_.Exception.Message)" `
                    -ForegroundColor Red
            }
        }
    }
}

# ============================================================
# PortProxy-regels controleren/toevoegen
# ============================================================

function Ensure-ProxyRules {

    Write-Host ""
    Write-Host "PortProxy-regels controleren..." `
        -ForegroundColor Cyan

    foreach ($Rule in $ProxyRules) {

        $CurrentRules = Get-PortProxyRules

        $ListenAddressEscaped =
            [regex]::Escape($Rule.ListenAddress)

        $ConnectAddressEscaped =
            [regex]::Escape($Rule.ConnectAddress)

        $ExpectedPattern =
            "^\s*$ListenAddressEscaped\s+$($Rule.ListenPort)\s+$ConnectAddressEscaped\s+$($Rule.ConnectPort)\s*$"

        $ListenPattern =
            "^\s*$ListenAddressEscaped\s+$($Rule.ListenPort)\s+"

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

        $Listener = Get-NetTCPConnection `
            -LocalAddress $Rule.ListenAddress `
            -LocalPort $Rule.ListenPort `
            -State Listen `
            -ErrorAction SilentlyContinue

        if ($ExactRuleExists -and $Listener) {

            Write-Host `
                "  [OK] $($Rule.Name): $($Rule.ListenAddress):$($Rule.ListenPort) -> $($Rule.ConnectAddress):$($Rule.ConnectPort) (LISTENING)" `
                -ForegroundColor DarkGray

            continue
        }

        if ($ExactRuleExists -and -not $Listener) {

            Write-Host `
                "  [FIX] $($Rule.Name) bestaat maar luistert niet." `
                -ForegroundColor Yellow

            netsh interface portproxy delete v4tov4 `
                listenaddress=$($Rule.ListenAddress) `
                listenport=$($Rule.ListenPort) |
                Out-Null
        }
        elseif ($ListenRuleExists) {

            Write-Host `
                "  [FIX] $($Rule.Name) heeft een afwijkende bestemming." `
                -ForegroundColor Yellow

            netsh interface portproxy delete v4tov4 `
                listenaddress=$($Rule.ListenAddress) `
                listenport=$($Rule.ListenPort) |
                Out-Null
        }
        else {

            Write-Host `
                "  [ADD] $($Rule.Name) ontbreekt." `
                -ForegroundColor Yellow
        }

        netsh interface portproxy add v4tov4 `
            listenaddress=$($Rule.ListenAddress) `
            listenport=$($Rule.ListenPort) `
            connectaddress=$($Rule.ConnectAddress) `
            connectport=$($Rule.ConnectPort) |
            Out-Null

        Start-Sleep -Milliseconds 500

        $Listener = Get-NetTCPConnection `
            -LocalAddress $Rule.ListenAddress `
            -LocalPort $Rule.ListenPort `
            -State Listen `
            -ErrorAction SilentlyContinue

        if ($Listener) {

            Write-Host `
                "  [OK] $($Rule.ListenAddress):$($Rule.ListenPort) -> $($Rule.ConnectAddress):$($Rule.ConnectPort) (LISTENING)" `
                -ForegroundColor Green
        }
        else {

            Write-Host `
                "  [FOUT] $($Rule.ListenAddress):$($Rule.ListenPort) is geconfigureerd maar luistert niet." `
                -ForegroundColor Red
        }
    }
}

# ============================================================
# PortProxy-regels verwijderen
# ============================================================

function Remove-ProxyRules {

    foreach ($Rule in $ProxyRules) {

        netsh interface portproxy delete v4tov4 `
            listenaddress=$($Rule.ListenAddress) `
            listenport=$($Rule.ListenPort) `
            2>$null |
            Out-Null
    }
}

# ============================================================
# Backend testen
# ============================================================

function Test-Backend {

    Write-Host ""
    Write-Host "=== Athena.NET backend controleren ===" `
        -ForegroundColor Cyan

    foreach ($Rule in $ProxyRules) {

        $Backend = Test-NetConnection `
            $Rule.ConnectAddress `
            -Port $Rule.ConnectPort `
            -WarningAction SilentlyContinue

        if ($Backend.TcpTestSucceeded) {

            Write-Host `
                "  [OK] $($Rule.Name): $($Rule.ConnectAddress):$($Rule.ConnectPort)" `
                -ForegroundColor Green
        }
        else {

            Write-Host `
                "  [FOUT] $($Rule.Name): $($Rule.ConnectAddress):$($Rule.ConnectPort)" `
                -ForegroundColor Red
        }
    }
}

# ============================================================
# Volledige verbinding testen
# ============================================================

function Test-Proxy {

    Write-Host ""
    Write-Host "=== Verbindingen controleren ===" `
        -ForegroundColor Cyan

    foreach ($Rule in $ProxyRules) {

        Write-Host ""
        Write-Host $Rule.Name `
            -ForegroundColor Yellow

        # Backend

        $Backend = Test-NetConnection `
            $Rule.ConnectAddress `
            -Port $Rule.ConnectPort `
            -WarningAction SilentlyContinue

        if ($Backend.TcpTestSucceeded) {

            Write-Host `
                "  Backend OK   : $($Rule.ConnectAddress):$($Rule.ConnectPort)" `
                -ForegroundColor Green
        }
        else {

            Write-Host `
                "  Backend FOUT : $($Rule.ConnectAddress):$($Rule.ConnectPort)" `
                -ForegroundColor Red
        }

        # Proxy

        $Proxy = Test-NetConnection `
            $Rule.ListenAddress `
            -Port $Rule.ListenPort `
            -WarningAction SilentlyContinue

        if ($Proxy.TcpTestSucceeded) {

            Write-Host `
                "  Proxy OK     : $($Rule.ListenAddress):$($Rule.ListenPort)" `
                -ForegroundColor Green
        }
        else {

            Write-Host `
                "  Proxy FOUT   : $($Rule.ListenAddress):$($Rule.ListenPort)" `
                -ForegroundColor Red
        }
    }
}

# ============================================================
# ENABLE
# ============================================================

function Enable-RagnarokProxy {

    Write-Host ""
    Write-Host "============================================" `
        -ForegroundColor Cyan

    Write-Host " Ragnarok Proxy INSCHAKELEN" `
        -ForegroundColor Cyan

    Write-Host "============================================" `
        -ForegroundColor Cyan

    Initialize-NativeIpApi

    # --------------------------------------------------------
    # Bepaal welke interface Windows gebruikt naar Athena.NET
    # --------------------------------------------------------

    $Network = Get-AthenaNetworkInterface

    Write-Host ""
    Write-Host "Netwerkinterface:" `
        -ForegroundColor Cyan

    Write-Host `
        "  Adapter : $($Network.InterfaceAlias)" `
        -ForegroundColor Green

    Write-Host `
        "  Index   : $($Network.InterfaceIndex)" `
        -ForegroundColor DarkGray

    Write-Host `
        "  Lokaal  : $($Network.SourceAddress)" `
        -ForegroundColor Green

    Write-Host `
        "  Backend : $BackendAddress" `
        -ForegroundColor Green

    # --------------------------------------------------------
    # Snapshot voordat Athena iets verandert
    # --------------------------------------------------------

    $Before = Get-InterfaceSnapshot `
        -InterfaceIndex $Network.InterfaceIndex `
        -SourceAddress $Network.SourceAddress

    Write-Host `
        "  DHCP    : $($Before.Dhcp)" `
        -ForegroundColor Green

    if ($Before.Gateway) {

        Write-Host `
            "  Gateway : $($Before.Gateway)" `
            -ForegroundColor DarkGray
    }

    # --------------------------------------------------------
    # Eerst IP Helper schoon starten.
    #
    # Belangrijk:
    # dit gebeurt VOORDAT we de tijdelijke IP's toevoegen.
    # --------------------------------------------------------

    Restart-PortProxyService

    # --------------------------------------------------------
    # Tijdelijke IP aliases
    # --------------------------------------------------------

    Ensure-ProxyIPs `
        -InterfaceIndex $Network.InterfaceIndex `
        -InterfaceAlias $Network.InterfaceAlias

    # --------------------------------------------------------
    # Kritieke controle:
    #
    # DHCP en oorspronkelijke IPv4 moeten nog bestaan.
    # --------------------------------------------------------

    Confirm-InterfaceUnchanged `
        -Before $Before `
        -InterfaceIndex $Network.InterfaceIndex

    # --------------------------------------------------------
    # PortProxy
    # --------------------------------------------------------

    Ensure-ProxyRules

    Write-Host ""
    Write-Host "Actieve PortProxy-regels:" `
        -ForegroundColor Cyan

    netsh interface portproxy show v4tov4

    Test-Proxy

    Write-Host ""
    Write-Host "============================================" `
        -ForegroundColor Green

    Write-Host " Ragnarok Proxy INGESCHAKELD" `
        -ForegroundColor Green

    Write-Host "============================================" `
        -ForegroundColor Green
}

# ============================================================
# DISABLE
# ============================================================

function Disable-RagnarokProxy {

    Write-Host ""
    Write-Host "============================================" `
        -ForegroundColor Cyan

    Write-Host " Ragnarok Proxy UITSCHAKELEN" `
        -ForegroundColor Cyan

    Write-Host "============================================" `
        -ForegroundColor Cyan

    Initialize-NativeIpApi

    Write-Host ""
    Write-Host "PortProxy-regels verwijderen..." `
        -ForegroundColor Yellow

    Remove-ProxyRules

    Remove-ProxyIPs

    # Zorg dat eventuele stale portproxy listeners ook weg zijn.

    Restart-PortProxyService

    Write-Host ""
    Write-Host "============================================" `
        -ForegroundColor Green

    Write-Host " Ragnarok Proxy UITGESCHAKELD" `
        -ForegroundColor Green

    Write-Host " Originele iRO servers weer bereikbaar." `
        -ForegroundColor Green

    Write-Host "============================================" `
        -ForegroundColor Green
}

# ============================================================
# STATUS
# ============================================================

function Show-RagnarokProxyStatus {

    Write-Host ""
    Write-Host "============================================" `
        -ForegroundColor Cyan

    Write-Host " Ragnarok Proxy STATUS" `
        -ForegroundColor Cyan

    Write-Host "============================================" `
        -ForegroundColor Cyan

    Initialize-NativeIpApi

    # --------------------------------------------------------
    # Huidige route
    # --------------------------------------------------------

    try {

        $Network = Get-AthenaNetworkInterface

        Write-Host ""
        Write-Host "Route naar Athena.NET:" `
            -ForegroundColor Yellow

        Write-Host `
            "  Adapter : $($Network.InterfaceAlias)"

        Write-Host `
            "  Index   : $($Network.InterfaceIndex)"

        Write-Host `
            "  Lokaal  : $($Network.SourceAddress)"

        Write-Host `
            "  Backend : $BackendAddress"

        $Interface =
            Get-NetIPInterface `
                -InterfaceIndex $Network.InterfaceIndex `
                -AddressFamily IPv4

        Write-Host `
            "  DHCP    : $($Interface.Dhcp)"
    }
    catch {

        Write-Host ""
        Write-Host `
            "Route naar Athena.NET niet beschikbaar: $($_.Exception.Message)" `
            -ForegroundColor Red
    }

    # --------------------------------------------------------
    # IP Helper
    # --------------------------------------------------------

    Write-Host ""
    Write-Host "IP Helper:" `
        -ForegroundColor Yellow

    Get-Service iphlpsvc |
        Format-Table Status, Name, DisplayName

    # --------------------------------------------------------
    # Tijdelijke IP aliases
    # --------------------------------------------------------

    Write-Host ""
    Write-Host "Lokale iRO IP-adressen:" `
        -ForegroundColor Yellow

    foreach ($Rule in $ProxyRules) {

        $IPs = @(
            Get-NetIPAddress `
                -IPAddress $Rule.ListenAddress `
                -AddressFamily IPv4 `
                -ErrorAction SilentlyContinue
        )

        if ($IPs.Count -gt 0) {

            foreach ($IP in $IPs) {

                Write-Host `
                    "  [OK] $($IP.IPAddress)/$($IP.PrefixLength) -> $($IP.InterfaceAlias) (SkipAsSource=$($IP.SkipAsSource))" `
                    -ForegroundColor Green
            }
        }
        else {

            Write-Host `
                "  [FOUT] $($Rule.ListenAddress) ontbreekt" `
                -ForegroundColor Red
        }
    }

    # --------------------------------------------------------
    # PortProxy
    # --------------------------------------------------------

    Write-Host ""
    Write-Host "PortProxy-regels:" `
        -ForegroundColor Yellow

    netsh interface portproxy show v4tov4

    # --------------------------------------------------------
    # TCP listeners
    # --------------------------------------------------------

    Write-Host ""
    Write-Host "TCP listeners:" `
        -ForegroundColor Yellow

    foreach ($Rule in $ProxyRules) {

        $Listener = Get-NetTCPConnection `
            -LocalAddress $Rule.ListenAddress `
            -LocalPort $Rule.ListenPort `
            -State Listen `
            -ErrorAction SilentlyContinue

        if ($Listener) {

            Write-Host `
                "  [OK] $($Rule.ListenAddress):$($Rule.ListenPort) LISTENING" `
                -ForegroundColor Green
        }
        else {

            Write-Host `
                "  [FOUT] $($Rule.ListenAddress):$($Rule.ListenPort) NIET LISTENING" `
                -ForegroundColor Red
        }
    }

    Test-Proxy
}

# ============================================================
# RESTART
# ============================================================

function Restart-RagnarokProxy {

    Write-Host ""
    Write-Host "============================================" `
        -ForegroundColor Cyan

    Write-Host " Ragnarok Proxy HERSTARTEN" `
        -ForegroundColor Cyan

    Write-Host "============================================" `
        -ForegroundColor Cyan

    Initialize-NativeIpApi

    Remove-ProxyRules
    Remove-ProxyIPs

    Start-Sleep -Milliseconds 500

    Enable-RagnarokProxy
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