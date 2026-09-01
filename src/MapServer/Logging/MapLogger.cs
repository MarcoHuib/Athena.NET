using System.Threading.Channels;
using Athena.Net.MapServer.Config;

namespace Athena.Net.MapServer.Logging;

public static class MapLogger
{
    private const int QueueCapacity = 4096;

    private static readonly Channel<LogEntry> Queue =
        Channel.CreateBounded<LogEntry>(
            new BoundedChannelOptions(QueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,

                // Logging must never stall the MapServer/gameplay thread if
                // Aspire/DCP temporarily stops consuming stdout.
                FullMode = BoundedChannelFullMode.DropOldest
            });

    private static readonly Task WriterTask =
        Task.Run(ProcessQueueAsync);

    private static bool _configured;

    public static void Configure(MapConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (_configured)
        {
            return;
        }

        _configured = true;

        // Force static initialization of the asynchronous writer.
        _ = WriterTask;
    }

    public static void Status(string message) =>
        Write("STATUS", message);

    public static void Info(string message) =>
        Write("INFO", message);

    public static void Warning(string message) =>
        Write("WARN", message);

    public static void Error(string message) =>
        Write("ERROR", message);

    private static void Write(
        string level,
        string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Capture the timestamp on the caller thread, then enqueue the complete
        // event. Console/stdout I/O happens on the dedicated background writer
        // so a slow Aspire/DCP log pipe can never delay server startup or
        // gameplay execution.
        Queue.Writer.TryWrite(
            new LogEntry(
                DateTime.UtcNow,
                level,
                message));
    }

    private static async Task ProcessQueueAsync()
    {
        await foreach (var entry in Queue.Reader.ReadAllAsync())
        {
            try
            {
                await Console.Out.WriteLineAsync(
                    $"{entry.TimestampUtc:yyyy-MM-dd HH:mm:ss} " +
                    $"[{entry.Level}] {entry.Message}");
            }
            catch (IOException)
            {
                // Logging is best-effort. A broken/closed stdout pipe must not
                // terminate the background writer or the MapServer.
            }
            catch (ObjectDisposedException)
            {
                // Process shutdown can dispose stdout while queued messages
                // still exist. Do not surface that as a server failure.
            }
        }
    }

    private sealed record LogEntry(
        DateTime TimestampUtc,
        string Level,
        string Message);
}