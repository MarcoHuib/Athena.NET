namespace Rathena.LoginServer.Config;

public sealed class LoginConfigStore
{
    private readonly object _lock = new();
    private readonly string? _path;
    private LoginConfig _current;

    public LoginConfigStore(LoginConfig config, string? path = null)
    {
        _current = config;
        _path = path;
    }

    public LoginConfig Current
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
        if (string.IsNullOrEmpty(_path))
        {
            return false;
        }

        var config = LoginConfigLoader.Load(_path);
        lock (_lock)
        {
            _current = config;
        }

        return true;
    }
}
