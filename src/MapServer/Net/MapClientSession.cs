using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Logging;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Net;

public sealed class MapClientSession : IDisposable
{
    private static readonly Dictionary<short, int> PacketLengths = new()
    {
        [PacketConstants.CzEnter] = 19,
        [PacketConstants.CzEnter2] = 19,
        // The stock-iRO capture carries one opaque trailing byte. Legacy references use 2 bytes.
        [PacketConstants.CzNotifyActorInit] = 3,
        [PacketConstants.CzClientVersion] = 6,
        [PacketConstants.CzPingLive] = 2,
        [PacketConstants.IroCzMapAuth] = PacketConstants.IroCzMapAuthLength,
        // Stock iRO appends one still-opaque byte to these otherwise familiar client packets.
        [PacketConstants.IroCzPostEnter0360] = PacketConstants.IroCzPostEnter0360Length,
        [PacketConstants.IroCzPostEnter08c9] = PacketConstants.IroCzPostEnter08c9Length,
        [PacketConstants.IroCzRequestMove] = PacketConstants.IroCzRequestMoveLength,
        [PacketConstants.IroCzActorInfoRequest] = PacketConstants.IroCzActorInfoRequestLength,
    };

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly CharServerConnector _charConnector;
    private readonly ICharacterPositionPersistence _positionPersistence;
    private readonly WorldMapRegistry _worldMapRegistry;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _sessionCancellation = new();
    private readonly HashSet<uint> _visibleActorIds = new();
    private uint _accountId;
    private uint _charId;
    private uint _loginId1;
    private string _mapName = string.Empty;
    private ushort _x;
    private ushort _y;
    private byte _sex;
    private bool _authRequested;
    private bool _iroAuthRequested;
    private bool _authenticated;
    private bool _positionDirty;
    private int _disposed;

    public MapClientSession(int sessionId, TcpClient client, CharServerConnector charConnector)
        : this(sessionId, client, charConnector, WorldMapRegistry.Tutorial)
    {
    }

    private MapClientSession(
        int sessionId,
        TcpClient client,
        CharServerConnector charConnector,
        WorldMapRegistry worldMapRegistry,
        ICharacterPositionPersistence? positionPersistence = null)
    {
        SessionId = sessionId;
        _client = client;
        _stream = client.GetStream();
        _charConnector = charConnector;
        _positionPersistence = positionPersistence ?? charConnector;
        _worldMapRegistry = worldMapRegistry;
    }

    internal MapClientSession(
        int sessionId,
        TcpClient client,
        CharServerConnector charConnector,
        bool iroAuthenticated,
        string mapName = "",
        ushort x = 0,
        ushort y = 0,
        WorldMapRegistry? worldMapRegistry = null,
        ICharacterPositionPersistence? positionPersistence = null)
        : this(
            sessionId,
            client,
            charConnector,
            worldMapRegistry ?? WorldMapRegistry.Tutorial,
            positionPersistence)
    {
        _iroAuthRequested = iroAuthenticated;
        _authRequested = iroAuthenticated;
        _mapName = mapName;
        _x = x;
        _y = y;
        _authenticated = iroAuthenticated;
    }

    public int SessionId { get; }

    internal string CurrentMapName => _mapName;
    internal ushort CurrentX => _x;
    internal ushort CurrentY => _y;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _sessionCancellation.Token);
        var sessionToken = linkedCancellation.Token;

        try
        {
            while (!sessionToken.IsCancellationRequested)
            {
                var packet = await ReadNextPacketAsync(_stream, sessionToken);
                if (packet.Length == 0)
                {
                    return;
                }

                var packetType = BinaryPrimitives.ReadInt16LittleEndian(packet);
                MapLogger.Info($"[iRO MAP DEBUG] Map client packet=0x{packetType:X4} len={packet.Length}");
                await HandlePacketAsync(packetType, packet, sessionToken);
            }
        }
        finally
        {
            await PersistPositionIfDirtyAsync(CancellationToken.None);
        }
    }

    public void HandleAuthOk(MapAuthOkData authOk)
    {
        if (!_authRequested ||
            authOk.AccountId != _accountId ||
            authOk.CharId != _charId ||
            authOk.LoginId1 != _loginId1)
        {
            return;
        }

        if (_iroAuthRequested)
        {
            _mapName = authOk.MapName;
            _x = authOk.X;
            _y = authOk.Y;
            _authenticated = true;
            _positionDirty = false;
            MapLogger.Info(
                $"[iRO MAP DEBUG] 0x0C1F MapAuthNode authentication succeeded accountId={authOk.AccountId} charId={authOk.CharId} sessionMatch=true");
            _ = SendIroInitialBootstrapAsync(authOk, CancellationToken.None);
            return;
        }

        _ = SendAcceptEnterAsync(authOk, CancellationToken.None);
        _ = SendNotifyActorInitAsync(CancellationToken.None);
    }

    public void HandleAuthFail()
    {
        if (!_authRequested)
        {
            return;
        }

        _ = SendRefuseEnterAsync(0, CancellationToken.None);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _sessionCancellation.Cancel();
        _stream.Dispose();
        _writeLock.Dispose();
        _sessionCancellation.Dispose();
    }

    private async Task HandlePacketAsync(short packetType, byte[] packet, CancellationToken cancellationToken)
    {
        switch (packetType)
        {
            case PacketConstants.CzEnter:
            case PacketConstants.CzEnter2:
                await HandleEnterAsync(packet, cancellationToken);
                break;
            case PacketConstants.CzNotifyActorInit:
                if (_iroAuthRequested)
                {
                    MapLogger.Info(
                        $"[iRO MAP DEBUG] Received stock iRO map-loaded packet=0x{packetType:X4} len={packet.Length}");
                    _visibleActorIds.Clear();
                    await SendVisibleWarpActorsAsync(cancellationToken);
                    break;
                }

                await SendNotifyActorInitAsync(cancellationToken);
                break;
            case PacketConstants.CzClientVersion:
                break;
            case PacketConstants.CzPingLive:
                await SendPingLiveAsync(cancellationToken);
                break;
            case PacketConstants.IroCzMapAuth:
                MapLogger.Info(
                    $"[iRO MAP DEBUG] Received stock iRO map auth packet=0x{packetType:X4} len={packet.Length}");
                await HandleIroMapAuthAsync(packet, cancellationToken);
                break;
            case PacketConstants.IroCzPostEnter0360 when _iroAuthRequested:
                MapLogger.Info(
                    $"[iRO MAP DEBUG] Reached next post-enter client boundary packet=0x{packetType:X4} len={packet.Length}");
                break;
            case PacketConstants.IroCzPostEnter08c9 when _iroAuthRequested:
                MapLogger.Info(
                    $"[iRO MAP DEBUG] Received opaque stock iRO packet=0x{packetType:X4} len={packet.Length}");
                break;
            case PacketConstants.IroCzRequestMove when _iroAuthRequested:
                await HandleIroMovementAsync(packet, cancellationToken);
                break;
            case PacketConstants.IroCzActorInfoRequest when _iroAuthRequested:
                MapLogger.Info(
                    $"[iRO MAP DEBUG] Received stock iRO actor-info request packet=0x{packetType:X4} len={packet.Length}");
                break;
            default:
                LogUnsupportedPacket(packetType, packet);
                RequestClose();
                break;
        }
    }

    private async Task HandleIroMapAuthAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (_authRequested || !IroMapAuthPacket.TryParse(packet, out var auth))
        {
            await SendRefuseEnterAsync(0, cancellationToken);
            return;
        }

        _accountId = auth.AccountId;
        _charId = auth.CharId;
        _loginId1 = auth.LoginId1;
        MapLogger.Info(
            $"[iRO MAP DEBUG] Parsed 0x0C1F accountId={_accountId} charId={_charId}");

        if (_accountId == 0 || _charId == 0)
        {
            await SendRefuseEnterAsync(0, cancellationToken);
            return;
        }

        var endpoint = _client.Client.RemoteEndPoint as IPEndPoint;
        var clientIp = endpoint?.Address ?? IPAddress.Loopback;
        if (!_charConnector.TrySendIroAuthRequest(this, _accountId, _charId, _loginId1, clientIp))
        {
            await SendRefuseEnterAsync(0, cancellationToken);
            MapLogger.Warning("iRO auth request to char server failed. Disconnecting map client.");
            RequestClose();
            return;
        }

        _iroAuthRequested = true;
        _authRequested = true;
    }

    private async Task HandleIroMovementAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!IroMovementPackets.TryParseRequest(packet, out var request))
        {
            RequestClose();
            return;
        }

        var fromX = _x;
        var fromY = _y;
        MapLogger.Info(
            $"[iRO MAP DEBUG] Movement request from=({fromX},{fromY}) target=({request.TargetX},{request.TargetY})");

        var intersectsWarp = _worldMapRegistry.TryFindFirstWarpAlongRoute(
            _mapName,
            fromX,
            fromY,
            request.TargetX,
            request.TargetY,
            out var intersection);
        var movementTargetX = intersectsWarp ? intersection.X : request.TargetX;
        var movementTargetY = intersectsWarp ? intersection.Y : request.TargetY;

        var response = IroMovementPackets.BuildResponse(
            unchecked((uint)Environment.TickCount),
            fromX,
            fromY,
            movementTargetX,
            movementTargetY);
        MapLogger.Info(
            $"[iRO MAP DEBUG] Sending 0x0087 len=12 from=({fromX},{fromY}) to=({movementTargetX},{movementTargetY})");
        await WriteAsync(response, cancellationToken);

        _x = movementTargetX;
        _y = movementTargetY;
        _positionDirty = true;

        if (intersectsWarp)
        {
            MapLogger.Info(
                $"[iRO MAP DEBUG] Movement path intersects warp map='{_mapName}' at=({intersection.X},{intersection.Y}) requestedTarget=({request.TargetX},{request.TargetY})");
            await SendSameServerWarpAsync(intersection.Warp, cancellationToken);
        }
        else
        {
            await SendVisibleWarpActorsAsync(cancellationToken);
        }
    }

    private async Task SendSameServerWarpAsync(WarpDefinition warp, CancellationToken cancellationToken)
    {
        foreach (var action in warp.OrderedActions)
        {
            if (action is SetSavePointAction savePoint)
            {
                // The CharServer persistence contract currently owns only last position.
                // Preserve ordering and data, but do not pretend savepoint persistence succeeded.
                MapLogger.Info($"[iRO MAP DEBUG] SetSavePoint deferred map='{savePoint.Map}' x={savePoint.X} y={savePoint.Y}");
                continue;
            }

            if (action is WarpAction warpAction)
            {
                MapLogger.Info($"[iRO MAP DEBUG] Warp triggered map='{_mapName}' at=({_x},{_y}) -> map='{warpAction.Map}' x={warpAction.X} y={warpAction.Y}");
                _mapName = warpAction.Map;
                _x = warpAction.X;
                _y = warpAction.Y;
            }
        }

        var response = IroMapTransitionPackets.BuildSameServerMapChange(_mapName, _x, _y);
        MapLogger.Info(
            $"[iRO MAP DEBUG] Sending 0x0091 len={response.Length} map='{IroMapTransitionPackets.NormalizeWireMapName(_mapName)}' x={_x} y={_y}");
        await WriteAsync(response, cancellationToken);
        await PersistPositionIfDirtyAsync(cancellationToken);
    }

    private async Task SendVisibleWarpActorsAsync(CancellationToken cancellationToken)
    {
        foreach (var actor in _worldMapRegistry.GetVisibleWarpActors(_mapName, _x, _y))
        {
            if (!_visibleActorIds.Add(actor.ActorId))
            {
                continue;
            }

            var packet = IroWorldActorPackets.BuildWarpActor(actor);
            MapLogger.Info(
                $"[iRO MAP DEBUG] Sending warp actor id={actor.ActorId} name='{actor.Name}' class={WarpActor.ClassId} map='{actor.MapName}' x={actor.X} y={actor.Y}");
            await WriteAsync(packet, cancellationToken);
        }
    }

    private async Task PersistPositionIfDirtyAsync(CancellationToken cancellationToken)
    {
        if (!_authenticated || !_positionDirty || string.IsNullOrWhiteSpace(_mapName))
        {
            return;
        }

        try
        {
            MapLogger.Info(
                $"[iRO MAP DEBUG] Persisting character position charId={_charId} map='{_mapName}' x={_x} y={_y}");
            if (await _positionPersistence.SavePositionAsync(
                    _accountId,
                    _charId,
                    _mapName,
                    _x,
                    _y,
                    cancellationToken))
            {
                _positionDirty = false;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            MapLogger.Warning($"Character position persistence failed: {ex.Message}");
        }
    }

    private Task SendIroInitialBootstrapAsync(MapAuthOkData authOk, CancellationToken cancellationToken)
    {
        MapLogger.Info("[iRO MAP DEBUG] Sending 0x0B18 len=4");
        MapLogger.Info($"[iRO MAP DEBUG] Sending 0x0283 len=6 accountId={authOk.AccountId}");
        MapLogger.Info("[iRO MAP DEBUG] Sending 0x0ADE len=6 overweightPercent=70");
        MapLogger.Info(
            $"[iRO MAP DEBUG] Sending 0x02EB len=13 map='{authOk.MapName}' x={authOk.X} y={authOk.Y}");
        var payload = IroMapEnterPackets.BuildInitialBootstrap(
            authOk,
            unchecked((uint)Environment.TickCount));
        return WriteAsync(payload, cancellationToken);
    }

    private async Task HandleEnterAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (_authRequested)
        {
            return;
        }

        _accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        _charId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(6, 4));
        _loginId1 = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(10, 4));
        _sex = packet[18];

        if (_accountId == 0)
        {
            await SendRefuseEnterAsync(2, cancellationToken);
            return;
        }

        if (_charId == 0)
        {
            await SendRefuseEnterAsync(3, cancellationToken);
            return;
        }

        if (_sex != 0 && _sex != 1)
        {
            await SendRefuseEnterAsync(6, cancellationToken);
            return;
        }

        var endpoint = _client.Client.RemoteEndPoint as IPEndPoint;
        var clientIp = endpoint?.Address ?? IPAddress.Loopback;

        if (!_charConnector.TrySendAuthRequest(this, _accountId, _charId, _loginId1, _sex, clientIp))
        {
            await SendRefuseEnterAsync(0, cancellationToken);
            MapLogger.Warning("Auth request to char server failed. Disconnecting map client.");
            RequestClose();
            return;
        }

        _authRequested = true;
    }

    private Task SendAcceptEnterAsync(MapAuthOkData authOk, CancellationToken cancellationToken)
    {
        var buffer = new byte[13];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.ZcAcceptEnter);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2, 4), (uint)Environment.TickCount);
        WritePackedPosition(buffer.AsSpan(6, 3), authOk.X, authOk.Y, authOk.Direction);
        buffer[9] = 5;
        buffer[10] = 5;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(11, 2), authOk.Font);
        return WriteAsync(buffer, cancellationToken);
    }

    private Task SendNotifyActorInitAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.ZcNotifyActorInit);
        return WriteAsync(buffer, cancellationToken);
    }

    private Task SendPingLiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.ZcPingLive);
        return WriteAsync(buffer, cancellationToken);
    }

    private Task SendRefuseEnterAsync(byte errorCode, CancellationToken cancellationToken)
    {
        var buffer = new byte[3];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.ZcRefuseEnter);
        buffer[2] = errorCode;
        return WriteAsync(buffer, cancellationToken);
    }

    internal static async Task<byte[]> ReadNextPacketAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = await ReadExactAsync(stream, 2, cancellationToken);
        if (header.Length == 0)
        {
            return Array.Empty<byte>();
        }

        var packetType = BinaryPrimitives.ReadInt16LittleEndian(header);
        return await ReadPacketAsync(stream, packetType, header, cancellationToken);
    }

    private static async Task<byte[]> ReadPacketAsync(
        Stream stream,
        short packetType,
        byte[] header,
        CancellationToken cancellationToken)
    {
        if (!PacketLengths.TryGetValue(packetType, out var length))
        {
            LogUnsupportedPacket(packetType, header);
            return Array.Empty<byte>();
        }

        var payloadLength = length - 2;
        var payload = payloadLength == 0
            ? Array.Empty<byte>()
            : await ReadExactAsync(stream, payloadLength, cancellationToken);
        if (payloadLength > 0 && payload.Length == 0)
        {
            return Array.Empty<byte>();
        }

        var packet = new byte[length];
        Buffer.BlockCopy(header, 0, packet, 0, 2);
        if (payloadLength > 0)
        {
            Buffer.BlockCopy(payload, 0, packet, 2, payloadLength);
        }

        return packet;
    }

    private static async Task<byte[]> ReadExactAsync(
        Stream stream,
        int length,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var bytes = await stream.ReadAsync(buffer.AsMemory(read, length - read), cancellationToken);
            if (bytes == 0)
            {
                return Array.Empty<byte>();
            }

            read += bytes;
        }

        return buffer;
    }

    private static void LogUnsupportedPacket(short packetType, ReadOnlySpan<byte> packet)
    {
        MapLogger.Warning(
            $"[iRO MAP DEBUG] Unsupported map client packet=0x{packetType:X4} len={packet.Length}");
    }

    private async Task WriteAsync(byte[] payload, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _stream.WriteAsync(payload, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void RequestClose()
    {
        if (!_sessionCancellation.IsCancellationRequested)
        {
            _sessionCancellation.Cancel();
        }
    }

    private static void WritePackedPosition(Span<byte> buffer, ushort x, ushort y, byte direction)
    {
        buffer[0] = (byte)(x >> 2);
        buffer[1] = (byte)((x << 6) | ((y >> 4) & 0x3f));
        buffer[2] = (byte)((y << 4) | (direction & 0x0f));
    }
}
