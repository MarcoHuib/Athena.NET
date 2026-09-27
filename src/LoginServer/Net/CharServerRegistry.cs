using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace Athena.Net.LoginServer.Net;

public sealed class CharServerRegistry
{
    private readonly ConcurrentDictionary<int, CharServerInfo> _servers = new();
    private int _nextId;

    public IReadOnlyCollection<CharServerInfo> Servers => _servers.Values.ToArray();

    /// <summary>
    /// Allocates the next opaque registry id for a newly service-authenticated
    /// CharServer connection. Purely an internal bookkeeping counter - it
    /// carries no meaning outside this process and is never derived from any
    /// account/credential concept.
    /// </summary>
    public int NextId()
    {
        return Interlocked.Increment(ref _nextId);
    }

    public void Register(int id, CharServerInfo info)
    {
        _servers[id] = info;
    }

    public bool TryGet(int id, out CharServerInfo info)
    {
        if (_servers.TryGetValue(id, out var found))
        {
            info = found;
            return true;
        }

        info = default!;
        return false;
    }

    public IEnumerable<KeyValuePair<int, CharServerInfo>> All()
    {
        return _servers.ToArray();
    }

    public async Task SendToAllExceptAsync(int? excludeId, byte[] payload)
    {
        foreach (var pair in All())
        {
            if (excludeId.HasValue && pair.Key == excludeId.Value)
            {
                continue;
            }

            var connection = pair.Value.Connection;
            if (connection == null)
            {
                continue;
            }

            await connection.WriteLock.WaitAsync();
            try
            {
                await connection.Stream.WriteAsync(payload);
            }
            catch (IOException)
            {
                // Ignore network errors.
            }
            finally
            {
                connection.WriteLock.Release();
            }
        }
    }

    public Task SendToAllAsync(byte[] payload)
    {
        return SendToAllExceptAsync(null, payload);
    }

    public void Unregister(int id)
    {
        _servers.TryRemove(id, out _);
    }
}
