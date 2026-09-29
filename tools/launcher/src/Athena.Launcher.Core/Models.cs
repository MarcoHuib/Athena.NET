using System.Net;
using System.Text.Json;

namespace Athena.Net.Launcher.Core;

public sealed record RagnarokLoginEndpoint(string Host, int Port);

// Item 14: `TargetPorts` (never empty - always at least [TargetPort]) is the round-robin backend
// list a new inbound connection picks its target from - see TcpProxy.TunnelAsync's own
// Interlocked-based round-robin selector. `TargetPort` itself is retained ONLY for
// backward-compatible external observation/logging of "the primary configured target" - the proxy
// itself always dials from TargetPorts, even when it holds exactly one entry (the single-port case
// is not a special code path, just a round-robin list of length one).
public sealed record ProxyEndpoint(string Name, IPAddress ListenAddress, int ListenPort, string TargetHost, int TargetPort, IReadOnlyList<int> TargetPorts)
{
    public IPEndPoint ListenEndPoint => new(ListenAddress, ListenPort);
}

public sealed record ManagedAddress(int InterfaceIndex, string InterfaceAlias, IPAddress Address);

public sealed record LauncherSessionState(Guid SessionId, int LauncherProcessId, List<ManagedAddressState> Addresses)
{
    public const int CurrentVersion = 1;
    public int Version { get; init; } = CurrentVersion;
}

public sealed record ManagedAddressState(int InterfaceIndex, string InterfaceAlias, string Address);

public enum LauncherState
{
    Idle, Updating, ValidatingClient, ResolvingOfficialEndpoint, RecoveringNetworkState,
    ConfiguringNetwork, StartingProxy, Ready, StartingAntiCheat, WaitingForGame,
    Playing, CleaningUp, Faulted
}

public sealed class LauncherOptions
{
    public string? RagnarokPath { get; init; }
    public string? UpdaterExecutable { get; init; }
    public string AthenaHost { get; init; } = string.Empty;
    public int LoginTargetPort { get; init; } = 6900;
    public int CharacterTargetPort { get; init; } = 6121;
    public int MapTargetPort { get; init; } = 5121;
    // Item 14: LOCAL-DEVELOPMENT-ONLY opt-in backend list for the Map endpoint - a new inbound
    // Ragexe map connection is round-robin-assigned across these ports, making it possible to prove
    // two clients landed on genuinely different backend MapServer processes (see AppHost's
    // map-server/map-server-b resources). Null (the default) preserves today's single-MapTargetPort
    // behavior byte-for-byte - this is never consulted unless explicitly configured. Never intended
    // as production load-balancer architecture - MapTargetPorts is deliberately a bare port list
    // with no health-checking/weighting/failover semantics.
    public IReadOnlyList<int>? MapTargetPorts { get; init; }
    public string CharacterListenAddress { get; init; } = "198.18.0.1";
    public int CharacterListenPort { get; init; } = 4500;
    public string MapListenAddress { get; init; } = "198.18.0.2";
    public int MapListenPort { get; init; } = 4501;
    public int GameStartTimeoutSeconds { get; init; } = 90;
    public int? NetworkInterfaceIndex { get; init; }
    public string? NetworkInterfaceAlias { get; init; }

    public static LauncherOptions Load(string path)
    {
        if (!File.Exists(path))
        {
            return new LauncherOptions();
        }

        return JsonSerializer.Deserialize<LauncherOptions>(File.ReadAllText(path), JsonDefaults.Options)
            ?? throw new InvalidOperationException($"Launcher configuration '{path}' is empty.");
    }

    public ValidatedLauncherOptions Validate()
    {
        if (string.IsNullOrWhiteSpace(AthenaHost))
        {
            throw new InvalidOperationException("AthenaHost is required.");
        }

        ValidatePort(LoginTargetPort, nameof(LoginTargetPort));
        ValidatePort(CharacterTargetPort, nameof(CharacterTargetPort));
        ValidatePort(MapTargetPort, nameof(MapTargetPort));
        if (MapTargetPorts is { Count: > 0 } mapTargetPorts)
        {
            foreach (var port in mapTargetPorts) ValidatePort(port, nameof(MapTargetPorts));
        }
        else if (MapTargetPorts is { Count: 0 })
        {
            throw new InvalidOperationException("MapTargetPorts, when configured, must contain at least one port.");
        }
        ValidatePort(CharacterListenPort, nameof(CharacterListenPort));
        ValidatePort(MapListenPort, nameof(MapListenPort));

        if (!IPAddress.TryParse(CharacterListenAddress, out var charAddress) || charAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new InvalidOperationException("CharacterListenAddress must be an IPv4 literal.");
        }
        if (!IPAddress.TryParse(MapListenAddress, out var mapAddress) || mapAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new InvalidOperationException("MapListenAddress must be an IPv4 literal.");
        }
        if (charAddress.Equals(mapAddress) && CharacterListenPort == MapListenPort)
        {
            throw new InvalidOperationException("Character and Map listener endpoints must be unique.");
        }
        if (GameStartTimeoutSeconds is < 5 or > 600)
        {
            throw new InvalidOperationException("GameStartTimeoutSeconds must be between 5 and 600.");
        }

        return new ValidatedLauncherOptions(this, charAddress, mapAddress);
    }

    private static void ValidatePort(int value, string name)
    {
        if (value is < 1 or > ushort.MaxValue)
        {
            throw new InvalidOperationException($"{name} must be between 1 and 65535.");
        }
    }
}

public sealed record ValidatedLauncherOptions(LauncherOptions Source, IPAddress CharacterListenAddress, IPAddress MapListenAddress);

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
}
