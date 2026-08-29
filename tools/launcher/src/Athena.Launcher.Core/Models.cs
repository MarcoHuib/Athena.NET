using System.Net;
using System.Text.Json;

namespace Athena.Net.Launcher.Core;

public sealed record RagnarokLoginEndpoint(string Host, int Port);

public sealed record ProxyEndpoint(string Name, IPAddress ListenAddress, int ListenPort, string TargetHost, int TargetPort)
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
