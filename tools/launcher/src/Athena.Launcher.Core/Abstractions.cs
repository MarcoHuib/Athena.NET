using System.Diagnostics;
using System.Net;

namespace Athena.Net.Launcher.Core;

public interface ILauncherLog
{
    void Information(string eventName, string message, IReadOnlyDictionary<string, object?>? properties = null);
    void Error(string eventName, Exception exception, string message);
}

public interface IRagnarokInstallationLocator
{
    Task<RagnarokInstallation> LocateAsync(LauncherOptions options, CancellationToken cancellationToken);
}

public interface IRagnarokUpdater
{
    Task RunAsync(RagnarokInstallation installation, CancellationToken cancellationToken);
}

public interface IRagnarokInstallationValidator
{
    Task ValidateAsync(RagnarokInstallation installation, CancellationToken cancellationToken);
}

public interface IRagnarokClientConfigurationReader
{
    Task<RagnarokLoginEndpoint> ReadAsync(RagnarokInstallation installation, CancellationToken cancellationToken);
}

public interface IEndpointResolver
{
    Task<IPAddress> ResolveIpv4Async(RagnarokLoginEndpoint endpoint, CancellationToken cancellationToken);
}

public interface ITemporaryIpManager
{
    Task RecoverStaleStateAsync(CancellationToken cancellationToken);
    Task<LauncherSession> CreateSessionAsync(CancellationToken cancellationToken);
    Task<ManagedAddress> EnsurePresentAsync(LauncherSession session, IPAddress address, LauncherOptions options, CancellationToken cancellationToken);
    Task RemoveAllManagedAddressesAsync(LauncherSession session, CancellationToken cancellationToken);
}

public interface ITcpProxy : IAsyncDisposable
{
    ProxyState State { get; }
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();
}

public enum ProxyState { Stopped, Starting, Running, Stopping, Faulted }

public interface ITcpProxyManager : IAsyncDisposable
{
    IReadOnlyList<ProxyEndpoint> Endpoints { get; }
    void ValidateAvailable(IReadOnlyList<ProxyEndpoint> endpoints);
    Task StartAsync(IReadOnlyList<ProxyEndpoint> endpoints, CancellationToken cancellationToken);
    Task StopAsync();
}

public interface IWatchdogLauncher
{
    Task<Process?> StartAsync(LauncherSession session, CancellationToken cancellationToken);
}

public interface IEasyAntiCheatLauncher
{
    Task<Process> StartAsync(RagnarokInstallation installation, CancellationToken cancellationToken);
}

public interface IGameProcessMonitor
{
    IReadOnlySet<int> SnapshotExistingProcesses();
    Task<Process> WaitForNewGameAsync(RagnarokInstallation installation, IReadOnlySet<int> existingProcessIds, TimeSpan timeout, CancellationToken cancellationToken);
    Task WaitForExitAsync(Process process, CancellationToken cancellationToken);
}

public sealed record RagnarokInstallation(string DirectoryPath, string RagexePath, string EasyAntiCheatPath, string UpdaterPath, string DataIniPath);

public sealed record LauncherSession(string StateFilePath, LauncherSessionState State);
