namespace Athena.Net.LoginServer.Config;

public sealed class LoginConfigStore
{
    private readonly object _lock = new();
    private readonly string? _path;
    private LoginConfig _current;
    private bool _loginCaseSensitive;

    public LoginConfigStore(LoginConfig config, string? path = null, bool loginCaseSensitive = false)
    {
        _current = config;
        _path = path;
        _loginCaseSensitive = loginCaseSensitive;
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

    public bool LoginCaseSensitive
    {
        get
        {
            lock (_lock)
            {
                return _loginCaseSensitive;
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

    public void UpdateLoginCaseSensitive(bool value)
    {
        lock (_lock)
        {
            _loginCaseSensitive = value;
        }
    }
}
