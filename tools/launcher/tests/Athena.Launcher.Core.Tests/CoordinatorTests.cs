using System.Diagnostics;
using System.Net;
using Athena.Net.Launcher.Core;

namespace Athena.Net.Launcher.Core.Tests;

public sealed class CoordinatorTests
{
    [Fact]
    public async Task UpdaterValidationFailureDoesNotModifyNetworkState()
    {
        var ip = new FakeIpManager();
        var coordinator = Create(ip: ip, validator: new ThrowingValidator());
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RunAsync(CancellationToken.None));
        Assert.Equal(0, ip.RecoverCalls + ip.EnsureCalls);
    }

    [Fact]
    public async Task GameLaunchFailureTriggersCleanupAndShutdownIsIdempotent()
    {
        var ip = new FakeIpManager();
        var proxies = new FakeProxyManager();
        var coordinator = Create(ip: ip, proxies: proxies, antiCheat: new ThrowingAntiCheat());
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RunAsync(CancellationToken.None));
        await coordinator.ShutdownAsync();
        await coordinator.ShutdownAsync();
        Assert.Equal(1, proxies.StopCalls);
        Assert.Equal(1, ip.RemoveCalls);
    }

    [Fact]
    public async Task ProxyStartupFailureRollsBackNetworkState()
    {
        var ip = new FakeIpManager();
        var proxies = new FakeProxyManager { FailStart = true };
        var coordinator = Create(ip: ip, proxies: proxies);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RunAsync(CancellationToken.None));
        Assert.Equal(3, ip.EnsureCalls);
        Assert.Equal(1, ip.RemoveCalls);
        Assert.Equal(1, proxies.StopCalls);
    }

    private static LauncherCoordinator Create(FakeIpManager? ip = null, IRagnarokInstallationValidator? validator = null, FakeProxyManager? proxies = null, IEasyAntiCheatLauncher? antiCheat = null) =>
        new(new LauncherOptions { AthenaHost = "server.example" }, new FakeLocator(), new FakeUpdater(), validator ?? new FakeValidator(), new FakeReader(), new FakeResolver(), ip ?? new FakeIpManager(), proxies ?? new FakeProxyManager(), new FakeWatchdog(), antiCheat ?? new ThrowingAntiCheat(), new FakeGameMonitor(), new NullLog());

    private sealed class FakeLocator : IRagnarokInstallationLocator { public Task<RagnarokInstallation> LocateAsync(LauncherOptions o, CancellationToken c) => Task.FromResult(Fakes.Installation); }
    private sealed class FakeUpdater : IRagnarokUpdater { public Task RunAsync(RagnarokInstallation i, CancellationToken c) => Task.CompletedTask; }
    private sealed class FakeValidator : IRagnarokInstallationValidator { public Task ValidateAsync(RagnarokInstallation i, CancellationToken c) => Task.CompletedTask; }
    private sealed class ThrowingValidator : IRagnarokInstallationValidator { public Task ValidateAsync(RagnarokInstallation i, CancellationToken c) => throw new InvalidOperationException("bad install"); }
    private sealed class FakeReader : IRagnarokClientConfigurationReader { public Task<RagnarokLoginEndpoint> ReadAsync(RagnarokInstallation i, CancellationToken c) => Task.FromResult(new RagnarokLoginEndpoint("127.0.0.3", 6800)); }
    private sealed class FakeResolver : IEndpointResolver { public Task<IPAddress> ResolveIpv4Async(RagnarokLoginEndpoint e, CancellationToken c) => Task.FromResult(IPAddress.Parse("127.0.0.3")); }
    private sealed class FakeWatchdog : IWatchdogLauncher { public Task<Process?> StartAsync(LauncherSession s, CancellationToken c) => Task.FromResult<Process?>(null); }
    private sealed class ThrowingAntiCheat : IEasyAntiCheatLauncher { public Task<Process> StartAsync(RagnarokInstallation i, CancellationToken c) => throw new InvalidOperationException("EAC failed"); }
    private sealed class FakeGameMonitor : IGameProcessMonitor
    {
        public IReadOnlySet<int> SnapshotExistingProcesses() => new HashSet<int>();
        public Task<Process> WaitForNewGameAsync(RagnarokInstallation i, IReadOnlySet<int> p, TimeSpan t, CancellationToken c) => throw new NotSupportedException();
        public Task WaitForExitAsync(Process p, CancellationToken c) => Task.CompletedTask;
    }
}

internal sealed class FakeIpManager : ITemporaryIpManager
{
    public int RecoverCalls, EnsureCalls, RemoveCalls;
    private readonly LauncherSession _session = new("state.json", new LauncherSessionState(Guid.NewGuid(), 1, []));
    public Task RecoverStaleStateAsync(CancellationToken c) { RecoverCalls++; return Task.CompletedTask; }
    public Task<LauncherSession> CreateSessionAsync(CancellationToken c) => Task.FromResult(_session);
    public Task<ManagedAddress> EnsurePresentAsync(LauncherSession s, IPAddress a, LauncherOptions o, CancellationToken c) { EnsureCalls++; return Task.FromResult(new ManagedAddress(1, "test", a)); }
    public Task RemoveAllManagedAddressesAsync(LauncherSession s, CancellationToken c) { RemoveCalls++; return Task.CompletedTask; }
}

internal sealed class FakeProxyManager : ITcpProxyManager
{
    public int StopCalls;
    public bool FailStart { get; init; }
    public IReadOnlyList<ProxyEndpoint> Endpoints { get; private set; } = [];
    public void ValidateAvailable(IReadOnlyList<ProxyEndpoint> e) { }
    public Task StartAsync(IReadOnlyList<ProxyEndpoint> e, CancellationToken c) { Endpoints = e; return FailStart ? Task.FromException(new InvalidOperationException("proxy failed")) : Task.CompletedTask; }
    public Task StopAsync() { StopCalls++; Endpoints = []; return Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
