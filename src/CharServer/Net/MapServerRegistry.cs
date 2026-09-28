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

    // Each MapServer connection reports its OWN authenticated-player count as an absolute snapshot,
    // never an increment/decrement - this call always overwrites, so a lost/duplicate/out-of-order
    // report from the SAME MapServer connection can never accumulate stale state. TotalUsers below
    // sums across every currently-registered connection, so multiple MapServer gateway replicas
    // aggregate correctly (MapServer A=3, MapServer B=2 -> 5) without any one of them overwriting
    // the others' contribution.
    public void UpdateUserCount(int sessionId, int users)
    {
        if (_servers.TryGetValue(sessionId, out var info))
        {
            _servers[sessionId] = info with { Users = users };
        }
    }

    // Removing a MapServer connection's entry entirely (never merely zeroing Users while keeping the
    // row) is what guarantees a disconnected MapServer's last-reported count stops contributing to
    // TotalUsers immediately - there is no stale row left behind for a reconnect to accumulate onto.
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

    /// <summary>Diagnostic/test-only count of currently registered MapServer sessions.</summary>
    public int Count => _servers.Count;

    /// <summary>Sum of every currently-registered MapServer connection's own last-reported authenticated-player count.</summary>
    public int TotalUsers => _servers.Values.Sum(info => info.Users);
}

public sealed record MapServerInfo(int SessionId, IPAddress Ip, int Port, MapServerSession Session)
{
    public IReadOnlyList<string> Maps { get; init; } = Array.Empty<string>();
    public int Users { get; init; }
}
