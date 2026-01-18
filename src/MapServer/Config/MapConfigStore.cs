namespace Athena.Net.MapServer.Config;

public sealed class MapConfigStore
{
    private MapConfig _current;
    private readonly string _path;

    public MapConfigStore(MapConfig config, string path)
    {
        _current = config;
        _path = path;
    }

    public MapConfig Current => _current;

    public void Reload()
    {
        _current = MapConfigLoader.Load(_path);
    }
}
