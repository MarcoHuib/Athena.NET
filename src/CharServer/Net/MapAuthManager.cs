using System.Collections.Concurrent;

namespace Athena.Net.CharServer.Net;

public sealed class MapAuthManager
{
    private readonly ConcurrentDictionary<uint, MapAuthNode> _nodes = new();

    public void Add(MapAuthNode node)
    {
        _nodes[node.AccountId] = node;
    }

    public bool TryGet(uint accountId, out MapAuthNode node)
    {
        return _nodes.TryGetValue(accountId, out node!);
    }

    public bool TryRemove(uint accountId, out MapAuthNode node)
    {
        return _nodes.TryRemove(accountId, out node!);
    }

    public void Clear()
    {
        _nodes.Clear();
    }
}
