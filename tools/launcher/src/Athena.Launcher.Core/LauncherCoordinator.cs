using System.Net;

namespace Athena.Net.Launcher.Core;

public sealed class LauncherCoordinator : IAsyncDisposable
{
    private readonly LauncherOptions _options;
    private readonly IRagnarokInstallationLocator _locator;
    private readonly IRagnarokUpdater _updater;
    private readonly IRagnarokInstallationValidator _validator;
    private readonly IRagnarokClientConfigurationReader _configurationReader;
    private readonly IEndpointResolver _endpointResolver;
    private readonly ITemporaryIpManager _ipManager;
    private readonly ITcpProxyManager _proxyManager;
    private readonly IWatchdogLauncher _watchdog;
    private readonly IEasyAntiCheatLauncher _antiCheat;
    private readonly IGameProcessMonitor _gameMonitor;
    private readonly ILauncherLog _log;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private LauncherSession? _session;
    private int _cleanupStarted;

    public LauncherState State { get; private set; } = LauncherState.Idle;
    public RagnarokLoginEndpoint? OfficialLoginEndpoint { get; private set; }
    public event EventHandler<LauncherState>? StateChanged;

    public LauncherCoordinator(
        LauncherOptions options,
        IRagnarokInstallationLocator locator,
        IRagnarokUpdater updater,
        IRagnarokInstallationValidator validator,
        IRagnarokClientConfigurationReader configurationReader,
        IEndpointResolver endpointResolver,
        ITemporaryIpManager ipManager,
        ITcpProxyManager proxyManager,
        IWatchdogLauncher watchdog,
        IEasyAntiCheatLauncher antiCheat,
        IGameProcessMonitor gameMonitor,
        ILauncherLog log)
    {
        _options = options;
        _locator = locator;
        _updater = updater;
        _validator = validator;
        _configurationReader = configurationReader;
        _endpointResolver = endpointResolver;
        _ipManager = ipManager;
        _proxyManager = proxyManager;
        _watchdog = watchdog;
        _antiCheat = antiCheat;
        _gameMonitor = gameMonitor;
        _log = log;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            var validated = _options.Validate();
            var installation = await _locator.LocateAsync(_options, cancellationToken);
            SetState(LauncherState.Updating);
            await _updater.RunAsync(installation, cancellationToken);

            SetState(LauncherState.ValidatingClient);
            await _validator.ValidateAsync(installation, cancellationToken);
            _log.Information("client.validation.succeeded", "Ragnarok installation validation succeeded.");

            SetState(LauncherState.ResolvingOfficialEndpoint);
            OfficialLoginEndpoint = await _configurationReader.ReadAsync(installation, cancellationToken);
            var officialAddress = await _endpointResolver.ResolveIpv4Async(OfficialLoginEndpoint, cancellationToken);

            var endpoints = BuildProxyEndpoints(validated, officialAddress, OfficialLoginEndpoint.Port);
            ValidateUniqueEndpoints(endpoints);

            SetState(LauncherState.RecoveringNetworkState);
            await _ipManager.RecoverStaleStateAsync(cancellationToken);
            _proxyManager.ValidateAvailable(endpoints);
            _session = await _ipManager.CreateSessionAsync(cancellationToken);
            await _watchdog.StartAsync(_session, cancellationToken);

            SetState(LauncherState.ConfiguringNetwork);
            foreach (var address in endpoints.Select(x => x.ListenAddress).Distinct())
            {
                await _ipManager.EnsurePresentAsync(_session, address, _options, cancellationToken);
            }

            SetState(LauncherState.StartingProxy);
            await _proxyManager.StartAsync(endpoints, cancellationToken);
            SetState(LauncherState.Ready);

            var existing = _gameMonitor.SnapshotExistingProcesses();
            SetState(LauncherState.StartingAntiCheat);
            await _antiCheat.StartAsync(installation, cancellationToken);
            SetState(LauncherState.WaitingForGame);
            var game = await _gameMonitor.WaitForNewGameAsync(
                installation, existing, TimeSpan.FromSeconds(_options.GameStartTimeoutSeconds), cancellationToken);
            SetState(LauncherState.Playing);
            await _gameMonitor.WaitForExitAsync(game, cancellationToken);
            await CleanupCoreAsync();
        }
        catch (Exception exception)
        {
            SetState(LauncherState.Faulted);
            _log.Error("launcher.failure", exception, "Launcher operation failed.");
            await CleanupCoreAsync(preserveFaultedState: true);
            throw;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task ShutdownAsync()
    {
        await _lifecycle.WaitAsync();
        try { await CleanupCoreAsync(); }
        finally { _lifecycle.Release(); }
    }

    private IReadOnlyList<ProxyEndpoint> BuildProxyEndpoints(ValidatedLauncherOptions options, IPAddress officialAddress, int loginPort) =>
    [
        new("Login", officialAddress, loginPort, _options.AthenaHost, _options.LoginTargetPort),
        new("Character", options.CharacterListenAddress, _options.CharacterListenPort, _options.AthenaHost, _options.CharacterTargetPort),
        new("Map", options.MapListenAddress, _options.MapListenPort, _options.AthenaHost, _options.MapTargetPort),
    ];

    public static void ValidateUniqueEndpoints(IReadOnlyList<ProxyEndpoint> endpoints)
    {
        var duplicate = endpoints.GroupBy(x => x.ListenEndPoint).FirstOrDefault(x => x.Count() > 1);
        if (duplicate != null)
        {
            throw new InvalidOperationException($"Duplicate proxy listener endpoint: {duplicate.Key}.");
        }
    }

    private async Task CleanupCoreAsync(bool preserveFaultedState = false)
    {
        if (Interlocked.Exchange(ref _cleanupStarted, 1) != 0)
        {
            return;
        }

        if (!preserveFaultedState) SetState(LauncherState.CleaningUp);
        _log.Information("cleanup.started", "Cleanup started.");
        try { await _proxyManager.StopAsync(); }
        catch (Exception ex) { _log.Error("cleanup.proxy.failed", ex, "Proxy cleanup failed."); }
        if (_session != null)
        {
            try { await _ipManager.RemoveAllManagedAddressesAsync(_session, CancellationToken.None); }
            catch (Exception ex) { _log.Error("cleanup.network.failed", ex, "Network cleanup failed; watchdog will retry."); }
        }
        _log.Information("cleanup.completed", "Cleanup completed.");
        if (!preserveFaultedState) SetState(LauncherState.Idle);
    }

    private void SetState(LauncherState state)
    {
        State = state;
        _log.Information("launcher.state", state.ToString());
        StateChanged?.Invoke(this, state);
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync();
        await _proxyManager.DisposeAsync();
        _lifecycle.Dispose();
    }
}
