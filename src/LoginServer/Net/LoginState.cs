using System.Collections.Concurrent;

namespace Rathena.LoginServer.Net;

public sealed class LoginState
{
    private readonly ConcurrentDictionary<uint, AuthNode> _authNodes = new();
    private readonly ConcurrentDictionary<uint, OnlineLoginData> _onlineUsers = new();
    private readonly ConcurrentDictionary<uint, CancellationTokenSource> _waitingDisconnect = new();
    private readonly object _regLock = new();
    private int _regCount;
    private DateTime _regWindowEnd = DateTime.MinValue;

    public TimeSpan AuthTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public Func<uint, Task>? OnAutoDisconnect { get; set; }

    public int OnlineCount => _onlineUsers.Count;
    public int AuthCount => _authNodes.Count;

    public void AddAuthNode(AuthNode node)
    {
        _authNodes[node.AccountId] = node;
    }

    public bool TryGetAuthNode(uint accountId, out AuthNode node)
    {
        if (_authNodes.TryGetValue(accountId, out var found))
        {
            node = found;
            return true;
        }

        node = default!;
        return false;
    }

    public void RemoveAuthNode(uint accountId)
    {
        _authNodes.TryRemove(accountId, out _);
    }

    public OnlineLoginData AddOnlineUser(int charServerId, uint accountId)
    {
        var data = _onlineUsers.GetOrAdd(accountId, _ => new OnlineLoginData(accountId));
        data.CharServerId = charServerId;
        CancelWaitingDisconnect(accountId);
        return data;
    }

    public bool TryGetOnlineUser(uint accountId, out OnlineLoginData data)
    {
        if (_onlineUsers.TryGetValue(accountId, out var found))
        {
            data = found;
            return true;
        }

        data = default!;
        return false;
    }

    public void RemoveOnlineUser(uint accountId)
    {
        _onlineUsers.TryRemove(accountId, out _);
        CancelWaitingDisconnect(accountId);
    }

    public void RemoveOnlineUsersByCharServer(int charServerId)
    {
        foreach (var pair in _onlineUsers.ToArray())
        {
            if (pair.Value.CharServerId == charServerId)
            {
                RemoveOnlineUser(pair.Key);
            }
        }
    }

    public void MarkOnlineUsersUnknownByCharServer(int charServerId)
    {
        foreach (var pair in _onlineUsers.ToArray())
        {
            if (pair.Value.CharServerId == charServerId)
            {
                pair.Value.CharServerId = -2;
                pair.Value.MarkedUnknownAt = DateTime.UtcNow;
            }
        }
    }

    public void CleanupUnknownUsers(TimeSpan maxAge)
    {
        var now = DateTime.UtcNow;
        foreach (var pair in _onlineUsers.ToArray())
        {
            if (pair.Value.CharServerId != -2 || pair.Value.MarkedUnknownAt == null)
            {
                continue;
            }

            if (now - pair.Value.MarkedUnknownAt.Value >= maxAge)
            {
                RemoveOnlineUser(pair.Key);
            }
        }
    }

    public void ScheduleWaitingDisconnect(uint accountId)
    {
        CancelWaitingDisconnect(accountId);

        var cts = new CancellationTokenSource();
        _waitingDisconnect[accountId] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(AuthTimeout, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_waitingDisconnect.TryGetValue(accountId, out var current) && current == cts)
            {
                _waitingDisconnect.TryRemove(accountId, out _);
                RemoveOnlineUser(accountId);
                RemoveAuthNode(accountId);
                if (OnAutoDisconnect != null)
                {
                    _ = OnAutoDisconnect(accountId);
                }
            }
        });
    }

    private void CancelWaitingDisconnect(uint accountId)
    {
        if (_waitingDisconnect.TryRemove(accountId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    public bool IsRegistrationAllowed(int allowedRegs, int windowSeconds)
    {
        if (allowedRegs <= 0 || windowSeconds <= 0)
        {
            return false;
        }

        lock (_regLock)
        {
            var now = DateTime.UtcNow;
            if (_regWindowEnd == DateTime.MinValue)
            {
                _regWindowEnd = now.AddSeconds(windowSeconds);
            }

            if (now < _regWindowEnd && _regCount >= allowedRegs)
            {
                return false;
            }

            if (now > _regWindowEnd)
            {
                _regCount = 0;
                _regWindowEnd = now.AddSeconds(windowSeconds);
            }

            return true;
        }
    }

    public void RegisterSuccess(int windowSeconds)
    {
        if (windowSeconds <= 0)
        {
            return;
        }

        lock (_regLock)
        {
            var now = DateTime.UtcNow;
            if (now > _regWindowEnd)
            {
                _regCount = 0;
                _regWindowEnd = now.AddSeconds(windowSeconds);
            }

            _regCount++;
        }
    }
}

public sealed class AuthNode
{
    public uint AccountId { get; init; }
    public uint LoginId1 { get; init; }
    public uint LoginId2 { get; init; }
    public byte Sex { get; init; }
    public byte ClientType { get; init; }
    public uint Ip { get; init; }
}

public sealed class OnlineLoginData
{
    public OnlineLoginData(uint accountId)
    {
        AccountId = accountId;
    }

    public uint AccountId { get; }
    public int CharServerId { get; set; }
    public DateTime? MarkedUnknownAt { get; set; }
}
