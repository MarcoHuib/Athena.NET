using Athena.Net.LoginServer.Config;

namespace Athena.Net.LoginServer.Logging;

public enum LogLevel
{
    Info,
    Status,
    Notice,
    Warning,
    Error,
    Debug
}

public static class LoginLogger
{
    private static readonly object LockObj = new();
    private static int _consoleSilentMask;
    private static int _fileMask;
    private static string _filePath = string.Empty;
    private static bool _configured;

    public static void Configure(LoginConfig config)
    {
        _consoleSilentMask = config.ConsoleSilent;
        _fileMask = config.ConsoleMsgLog;
        _filePath = config.ConsoleLogFilePath ?? string.Empty;
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
        if (!_configured || (_consoleSilentMask & mask) == 0)
        {
            Console.WriteLine(message);
        }

        if (_configured && (_fileMask & mask) != 0 && !string.IsNullOrWhiteSpace(_filePath))
        {
            AppendToFile(message);
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
}
