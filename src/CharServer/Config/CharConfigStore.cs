namespace Athena.Net.CharServer.Config;

public sealed class CharConfigStore
{
    public CharConfigStore(CharConfig config, string configPath)
    {
        Current = config;
        ConfigPath = configPath;
    }

    public string ConfigPath { get; }
    public CharConfig Current { get; private set; }

    public void Reload()
    {
        Current = CharConfigLoader.Load(ConfigPath);
    }
}
