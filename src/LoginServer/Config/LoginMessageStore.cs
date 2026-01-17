using Athena.Net.LoginServer.Logging;

namespace Athena.Net.LoginServer.Config;

public sealed class LoginMessageStore
{
    private readonly object _lock = new();
    private readonly string? _path;
    private LoginMessageCatalog _current;

    public LoginMessageStore(LoginMessageCatalog catalog, string? path = null)
    {
        _current = catalog;
        _path = path;
    }

    public LoginMessageCatalog Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    public bool Reload()
    {
        if (string.IsNullOrWhiteSpace(_path))
        {
            return false;
        }

        var catalog = LoginMessageLoader.Load(_path);
        lock (_lock)
        {
            _current = catalog;
        }

        return true;
    }
}

public static class LoginMessageLoader
{
    public static LoginMessageCatalog Load(string path)
    {
        if (!File.Exists(path))
        {
            LoginLogger.Info($"Login messages not found: {path}. Using defaults.");
            return new LoginMessageCatalog(new Dictionary<uint, string>());
        }

        var messages = new Dictionary<uint, string>();
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            if (!uint.TryParse(key, out var code))
            {
                continue;
            }

            var message = line[(separator + 1)..].Trim();
            if (message.Length == 0)
            {
                continue;
            }

            messages[code] = message;
        }

        return new LoginMessageCatalog(messages);
    }

    private static string StripComment(string line)
    {
        var index = line.IndexOf("//", StringComparison.Ordinal);
        return index < 0 ? line : line[..index];
    }
}
