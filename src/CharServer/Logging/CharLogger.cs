using System.Globalization;
using Athena.Net.CharServer.Config;

namespace Athena.Net.CharServer.Logging;

public enum LogLevel
{
    Info,
    Status,
    Notice,
    Warning,
    Error,
    Debug
}

public static class CharLogger
{
    private static readonly object LockObj = new();
    private static int _consoleSilentMask;
    private static int _fileMask;
    private static string _filePath = string.Empty;
    private static string _timestampFormat = string.Empty;
    private static bool _configured;

    public static void Configure(CharConfig config)
    {
        _consoleSilentMask = config.ConsoleSilent;
        _fileMask = config.ConsoleMsgLog;
        _filePath = config.ConsoleLogFilePath ?? string.Empty;
        _timestampFormat = config.TimestampFormat ?? string.Empty;
        _configured = true;
    }

    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Status(string message) => Write(LogLevel.Status, message);
    public static void Notice(string message) => Write(LogLevel.Notice, message);
    public static void Warning(string message) => Write(LogLevel.Warning, message);
    public static void Error(string message) => Write(LogLevel.Error, message);
    public static void Debug(string message) => Write(LogLevel.Debug, message);

    private static void Write(LogLevel level, string message)
    {
        var mask = MaskFor(level);
        var line = message;
        var prefix = FormatTimestampPrefix();
        if (!string.IsNullOrEmpty(prefix))
        {
            line = $"{prefix} {message}";
        }

        if (!_configured || (_consoleSilentMask & mask) == 0)
        {
            Console.WriteLine(line);
        }

        if (_configured && (_fileMask & mask) != 0 && !string.IsNullOrWhiteSpace(_filePath))
        {
            AppendToFile(line);
        }
    }

    private static int MaskFor(LogLevel level)
    {
        return level switch
        {
            LogLevel.Info => 1,
            LogLevel.Status => 2,
            LogLevel.Notice => 4,
            LogLevel.Warning => 8,
            LogLevel.Error => 16,
            LogLevel.Debug => 32,
            _ => 1,
        };
    }

    private static void AppendToFile(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            lock (LockObj)
            {
                File.AppendAllText(_filePath, message + Environment.NewLine);
            }
        }
        catch (IOException)
        {
            // Ignore file logging failures.
        }
        catch (UnauthorizedAccessException)
        {
            // Ignore file logging failures.
        }
    }

    private static string FormatTimestampPrefix()
    {
        if (string.IsNullOrWhiteSpace(_timestampFormat))
        {
            return string.Empty;
        }

        var format = ConvertTimestampFormat(_timestampFormat);
        try
        {
            return DateTime.Now.ToString(format, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    private static string ConvertTimestampFormat(string format)
    {
        return format
            .Replace("%Y", "yyyy", StringComparison.Ordinal)
            .Replace("%m", "MM", StringComparison.Ordinal)
            .Replace("%d", "dd", StringComparison.Ordinal)
            .Replace("%H", "HH", StringComparison.Ordinal)
            .Replace("%I", "hh", StringComparison.Ordinal)
            .Replace("%M", "mm", StringComparison.Ordinal)
            .Replace("%S", "ss", StringComparison.Ordinal)
            .Replace("%b", "MMM", StringComparison.Ordinal)
            .Replace("%p", "tt", StringComparison.Ordinal);
    }
}
