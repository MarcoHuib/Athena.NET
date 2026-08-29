using System.ComponentModel;
using System.Diagnostics;

namespace Athena.Net.Launcher.Core;

public sealed class EasyAntiCheatLauncher : IEasyAntiCheatLauncher
{
    private readonly ILauncherLog _log;
    public EasyAntiCheatLauncher(ILauncherLog log) => _log = log;
    public Task<Process> StartAsync(RagnarokInstallation installation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var process = Process.Start(new ProcessStartInfo(installation.EasyAntiCheatPath, "1rag1")
        {
            WorkingDirectory = installation.DirectoryPath,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Easy Anti-Cheat could not be started.");
        _log.Information("eac.started", "Easy Anti-Cheat started.", new Dictionary<string, object?> { ["pid"] = process.Id });
        return Task.FromResult(process);
    }
}

public sealed class GameProcessMonitor : IGameProcessMonitor
{
    private readonly ILauncherLog _log;
    public GameProcessMonitor(ILauncherLog log) => _log = log;
    public IReadOnlySet<int> SnapshotExistingProcesses() => Process.GetProcessesByName("Ragexe").Select(x => x.Id).ToHashSet();

    public async Task<Process> WaitForNewGameAsync(RagnarokInstallation installation, IReadOnlySet<int> existingProcessIds, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var process in Process.GetProcessesByName("Ragexe"))
            {
                if (existingProcessIds.Contains(process.Id)) { process.Dispose(); continue; }
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path != null && Path.GetFullPath(path).Equals(Path.GetFullPath(installation.RagexePath), StringComparison.OrdinalIgnoreCase))
                    {
                        _log.Information("game.started", "Ragexe started.", new Dictionary<string, object?> { ["pid"] = process.Id, ["path"] = path });
                        return process;
                    }
                }
                catch (Win32Exception) { process.Dispose(); }
            }
            await Task.Delay(500, cancellationToken);
        }
        throw new TimeoutException($"Easy Anti-Cheat did not start a new Ragexe.exe within {timeout.TotalSeconds:0} seconds.");
    }

    public async Task WaitForExitAsync(Process process, CancellationToken cancellationToken)
    {
        await process.WaitForExitAsync(cancellationToken);
        _log.Information("game.exited", "Ragexe exited.", new Dictionary<string, object?> { ["pid"] = process.Id });
        process.Dispose();
    }
}
