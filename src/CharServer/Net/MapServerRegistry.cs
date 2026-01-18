using System.Collections.Concurrent;
using System.Net;

namespace Athena.Net.CharServer.Net;

public sealed class MapServerRegistry
{
    private readonly ConcurrentDictionary<int, MapServerInfo> _servers = new();

    public bool TryRegister(int sessionId, IPAddress ip, int port, MapServerSession session)
    {
        return _servers.TryAdd(sessionId, new MapServerInfo(sessionId, ip, port, session));
    }

    public void UpdateMaps(int sessionId, IReadOnlyList<string> maps)
    {
        if (_servers.TryGetValue(sessionId, out var info))
        {
            _servers[sessionId] = info with { Maps = maps };
        }
    }

    public void Remove(int sessionId)
    {
        _servers.TryRemove(sessionId, out _);
    }

    public bool TryGetAny(out MapServerInfo info)
    {
        foreach (var entry in _servers.Values)
        {
            info = entry;
            return true;
        }

        info = default!;
        return false;
    }
}

public sealed record MapServerInfo(int SessionId, IPAddress Ip, int Port, MapServerSession Session)
{
    public IReadOnlyList<string> Maps { get; init; } = Array.Empty<string>();
}
