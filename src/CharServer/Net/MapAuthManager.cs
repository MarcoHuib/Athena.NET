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

    public bool TryConsume(
        uint accountId,
        uint charId,
        uint loginId1,
        byte? sex,
        out MapAuthNode node)
    {
        node = default!;
        if (!_nodes.TryGetValue(accountId, out var candidate) ||
            candidate.CharId != charId ||
            candidate.LoginId1 != loginId1 ||
            (sex.HasValue && candidate.Sex != sex.Value))
        {
            return false;
        }

        if (!_nodes.TryRemove(new KeyValuePair<uint, MapAuthNode>(accountId, candidate)))
        {
            return false;
        }

        node = candidate;
        return true;
    }

    public void Clear()
    {
        _nodes.Clear();
    }
}
