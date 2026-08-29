using System.Text.Json;

namespace Athena.Net.Launcher.Core;

public sealed class JsonLineLauncherLog : ILauncherLog, IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    public string FilePath { get; }

    public JsonLineLauncherLog(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Athena.NET", "Launcher", "Logs");
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, $"launcher-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.jsonl");
        _writer = new StreamWriter(new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    }

    public void Information(string eventName, string message, IReadOnlyDictionary<string, object?>? properties = null) => Write("Information", eventName, message, null, properties);
    public void Error(string eventName, Exception exception, string message) => Write("Error", eventName, message, exception, null);

    private void Write(string level, string eventName, string message, Exception? exception, IReadOnlyDictionary<string, object?>? properties)
    {
        var record = new Dictionary<string, object?>
        {
            ["timestamp"] = DateTimeOffset.UtcNow,
            ["level"] = level,
            ["event"] = eventName,
            ["message"] = message,
            ["exception"] = exception?.ToString(),
            ["properties"] = properties,
        };
        lock (_gate) _writer.WriteLine(JsonSerializer.Serialize(record));
    }
    public void Dispose() => _writer.Dispose();
}
