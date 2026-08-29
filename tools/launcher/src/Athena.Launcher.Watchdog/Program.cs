using System.Diagnostics;
using Athena.Net.Launcher.Core;

var arguments = args.Chunk(2).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1], StringComparer.OrdinalIgnoreCase);
if (!arguments.TryGetValue("--parent", out var parentText) || !int.TryParse(parentText, out var parentPid) || parentPid <= 0 ||
    !arguments.TryGetValue("--state", out var statePath) || string.IsNullOrWhiteSpace(statePath)) return 2;

var sessionsRoot = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Athena.NET", "Launcher", "Sessions"));
var fullStatePath = Path.GetFullPath(statePath);
if (!fullStatePath.StartsWith(sessionsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return 3;

try
{
    using var parent = Process.GetProcessById(parentPid);
    await parent.WaitForExitAsync();
}
catch (ArgumentException) { }

using var log = new JsonLineLauncherLog();
try
{
    await WindowsTemporaryIpManager.CleanupStateFileAsync(fullStatePath, new PowerShellNetworkCommandRunner(), log, CancellationToken.None);
    return 0;
}
catch (Exception ex)
{
    log.Error("watchdog.cleanup.failed", ex, "Watchdog cleanup failed; the next launcher startup will retry stale-state recovery.");
    return 1;
}
