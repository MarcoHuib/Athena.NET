using System.Text.Json;
using Athena.Net.Launcher.Core;

namespace Athena.Net.Launcher.Core.Tests;

public sealed class TemporaryIpManagerTests
{
    [Fact]
    public async Task StaleManagedStateIsRecoverableAndCleanupIsIdempotent()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"athena-launcher-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "stale.json");
            var state = new LauncherSessionState(Guid.NewGuid(), 123, [new ManagedAddressState(7, "Adapter", "198.18.0.1")]);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(state, JsonDefaults.Options));
            var commands = new RecordingCommands();
            var manager = new WindowsTemporaryIpManager(commands, new NullLog(), directory);
            await manager.RecoverStaleStateAsync(CancellationToken.None);
            await manager.RecoverStaleStateAsync(CancellationToken.None);
            Assert.False(File.Exists(path));
            Assert.Single(commands.Commands);
            Assert.Contains("198.18.0.1", commands.Commands[0]);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class RecordingCommands : INetworkCommandRunner
    {
        public List<string> Commands { get; } = [];
        public Task<string> RunAsync(string command, CancellationToken cancellationToken) { Commands.Add(command); return Task.FromResult(string.Empty); }
    }
}
