using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Athena.Net.Launcher.Core;

public sealed record NetworkAdapterSelection(int InterfaceIndex, string InterfaceAlias);

public interface INetworkCommandRunner
{
    Task<string> RunAsync(string command, CancellationToken cancellationToken);
}

public sealed class PowerShellNetworkCommandRunner : INetworkCommandRunner
{
    public async Task<string> RunAsync(string command, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Temporary IP management is Windows-only.");
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop';" + command));
        var startInfo = new ProcessStartInfo("powershell.exe", $"-NoLogo -NoProfile -NonInteractive -EncodedCommand {encoded}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("PowerShell could not be started for Windows networking.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException($"Windows networking command failed: {error.Trim()}");
        return output.Trim();
    }
}

public sealed class WindowsTemporaryIpManager : ITemporaryIpManager
{
    private readonly INetworkCommandRunner _commands;
    private readonly ILauncherLog _log;
    private readonly string _stateDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NetworkAdapterSelection? _selectedAdapter;

    public WindowsTemporaryIpManager(INetworkCommandRunner commands, ILauncherLog log, string? stateDirectory = null)
    {
        _commands = commands;
        _log = log;
        _stateDirectory = stateDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Athena.NET", "Launcher", "Sessions");
    }

    public async Task RecoverStaleStateAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_stateDirectory);
        foreach (var file in Directory.EnumerateFiles(_stateDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            await CleanupStateFileAsync(file, _commands, _log, cancellationToken);
        }
    }

    public Task<LauncherSession> CreateSessionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_stateDirectory);
        var id = Guid.NewGuid();
        var path = Path.Combine(_stateDirectory, $"{id:N}.json");
        var state = new LauncherSessionState(id, Environment.ProcessId, []);
        WriteState(path, state);
        return Task.FromResult(new LauncherSession(path, state));
    }

    public async Task<ManagedAddress> EnsurePresentAsync(LauncherSession session, IPAddress address, LauncherOptions options, CancellationToken cancellationToken)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) throw new ArgumentException("Only IPv4 aliases are supported.", nameof(address));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _selectedAdapter ??= await SelectAdapterAsync(options, cancellationToken);
            var adapter = _selectedAdapter;
            _log.Information("network.adapter.selected", "Network adapter selected.", new Dictionary<string, object?> { ["index"] = adapter.InterfaceIndex, ["alias"] = adapter.InterfaceAlias });
            var existingOwned = session.State.Addresses.Any(x => x.InterfaceIndex == adapter.InterfaceIndex && x.Address == address.ToString());
            var exists = (await _commands.RunAsync($"[bool](Get-NetIPAddress -AddressFamily IPv4 -InterfaceIndex {adapter.InterfaceIndex} -IPAddress '{address}' -ErrorAction SilentlyContinue)", cancellationToken)).Equals("True", StringComparison.OrdinalIgnoreCase);
            if (exists && !existingOwned) throw new InvalidOperationException($"IP address {address} already exists on adapter '{adapter.InterfaceAlias}' and is not owned by this launcher session.");
            if (!existingOwned)
            {
                session.State.Addresses.Add(new ManagedAddressState(adapter.InterfaceIndex, adapter.InterfaceAlias, address.ToString()));
                WriteState(session.StateFilePath, session.State);
            }
            if (!exists)
            {
                await _commands.RunAsync($"New-NetIPAddress -InterfaceIndex {adapter.InterfaceIndex} -IPAddress '{address}' -PrefixLength 32 -SkipAsSource $true -PolicyStore ActiveStore | Out-Null", cancellationToken);
                _log.Information("network.address.added", "Temporary address added.", new Dictionary<string, object?> { ["address"] = address.ToString(), ["adapter"] = adapter.InterfaceAlias });
            }
            return new ManagedAddress(adapter.InterfaceIndex, adapter.InterfaceAlias, address);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAllManagedAddressesAsync(LauncherSession session, CancellationToken cancellationToken)
    {
        await CleanupStateFileAsync(session.StateFilePath, _commands, _log, cancellationToken);
        session.State.Addresses.Clear();
    }

    private async Task<NetworkAdapterSelection> SelectAdapterAsync(LauncherOptions options, CancellationToken cancellationToken)
    {
        string filter;
        if (options.NetworkInterfaceIndex.HasValue) filter = $"Get-NetAdapter -InterfaceIndex {options.NetworkInterfaceIndex.Value} -ErrorAction Stop";
        else if (!string.IsNullOrWhiteSpace(options.NetworkInterfaceAlias)) filter = $"Get-NetAdapter -Name '{Escape(options.NetworkInterfaceAlias)}' -ErrorAction Stop";
        else filter = "$r=Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' | Where-Object {$_.State -eq 'Alive'} | Sort-Object RouteMetric,InterfaceMetric | Select-Object -First 1;if($null -eq $r){throw 'No active IPv4 default route found'};Get-NetAdapter -InterfaceIndex $r.InterfaceIndex -ErrorAction Stop";
        var json = await _commands.RunAsync($"{filter} | Select-Object InterfaceIndex,Name,Status | ConvertTo-Json -Compress", cancellationToken);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var status = root.GetProperty("Status").GetString();
        if (!string.Equals(status, "Up", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Selected network adapter is not Up.");
        return new NetworkAdapterSelection(root.GetProperty("InterfaceIndex").GetInt32(), root.GetProperty("Name").GetString() ?? throw new InvalidOperationException("Selected adapter has no name."));
    }

    public static async Task CleanupStateFileAsync(string path, INetworkCommandRunner commands, ILauncherLog log, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return;
        LauncherSessionState state;
        try { state = JsonSerializer.Deserialize<LauncherSessionState>(await File.ReadAllTextAsync(path, cancellationToken), JsonDefaults.Options) ?? throw new InvalidDataException("Empty state."); }
        catch (Exception ex) { log.Error("network.state.invalid", ex, $"Refusing invalid launcher state file '{path}'."); return; }
        if (state.Version != LauncherSessionState.CurrentVersion || state.SessionId == Guid.Empty || state.Addresses.Any(x => x.InterfaceIndex <= 0 || !IPAddress.TryParse(x.Address, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
        {
            log.Error("network.state.invalid", new InvalidDataException("State validation failed."), $"Refusing invalid launcher state file '{path}'.");
            return;
        }
        foreach (var address in state.Addresses.DistinctBy(x => (x.InterfaceIndex, x.Address)))
        {
            await commands.RunAsync($"$ip=Get-NetIPAddress -AddressFamily IPv4 -InterfaceIndex {address.InterfaceIndex} -IPAddress '{address.Address}' -ErrorAction SilentlyContinue;if($null -ne $ip){{$ip | Remove-NetIPAddress -Confirm:$false -ErrorAction Stop}}", cancellationToken);
            log.Information("network.address.removed", "Temporary address removed.", new Dictionary<string, object?> { ["address"] = address.Address, ["adapter"] = address.InterfaceAlias });
        }
        File.Delete(path);
    }

    private static void WriteState(string path, LauncherSessionState state)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonDefaults.Options));
        File.Move(temporary, path, true);
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}

public sealed class WatchdogLauncher : IWatchdogLauncher
{
    private readonly ILauncherLog _log;
    private readonly string _watchdogPath;
    public WatchdogLauncher(ILauncherLog log, string watchdogPath) { _log = log; _watchdogPath = watchdogPath; }
    public Task<Process?> StartAsync(LauncherSession session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_watchdogPath)) throw new FileNotFoundException("Athena Launcher watchdog is missing.", _watchdogPath);
        var process = Process.Start(new ProcessStartInfo(_watchdogPath)
        {
            ArgumentList = { "--parent", Environment.ProcessId.ToString(), "--state", session.StateFilePath },
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process == null) throw new InvalidOperationException("Watchdog could not be started.");
        _log.Information("watchdog.started", "Watchdog started.", new Dictionary<string, object?> { ["pid"] = process.Id });
        return Task.FromResult<Process?>(process);
    }
}
