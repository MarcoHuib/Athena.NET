using Athena.Net.MapServer.Config;

namespace Athena.Net.MapServer.Logging;

public static class MapLogger
{
    private static bool _configured;
    private static Action<string, string>? _telemetrySink;

    public static void Configure(MapConfig config)
    {
        if (_configured)
        {
            return;
        }

        _configured = true;
    }

    public static void SetTelemetrySink(Action<string, string>? sink)
    {
        _telemetrySink = sink;
    }

    public static void Status(string message) => Write("STATUS", message);
    public static void Info(string message) => Write("INFO", message);
    public static void Warning(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        _telemetrySink?.Invoke(level, message);
        Console.WriteLine($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} [{level}] {message}");
    }
}
