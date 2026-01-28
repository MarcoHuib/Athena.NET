using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Logging;

namespace Athena.Net.MapServer.Net;

public sealed class MapClientSession : IDisposable
{
    private static readonly Dictionary<short, int> PacketLengths = new()
    {
        [PacketConstants.CzEnter] = 19,
        [PacketConstants.CzEnter2] = 19,
        [PacketConstants.CzNotifyActorInit] = 2,
        [PacketConstants.CzClientVersion] = 6,
        [PacketConstants.CzPingLive] = 2,
    };

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly CharServerConnector _charConnector;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private uint _accountId;
    private uint _charId;
    private uint _loginId1;
    private byte _sex;
    private bool _authRequested;

    public MapClientSession(int sessionId, TcpClient client, CharServerConnector charConnector)
    {
        SessionId = sessionId;
        _client = client;
        _stream = client.GetStream();
        _charConnector = charConnector;
    }

    public int SessionId { get; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var header = await ReadExactAsync(2, cancellationToken);
            if (header.Length == 0)
            {
                return;
            }

            var packetType = BinaryPrimitives.ReadInt16LittleEndian(header);
            var packet = await ReadPacketAsync(packetType, header, cancellationToken);
            if (packet.Length == 0)
            {
                return;
            }

            await HandlePacketAsync(packetType, packet, cancellationToken);
        }
    }

    public void HandleAuthOk(MapAuthOkData authOk)
    {
        if (!_authRequested || authOk.AccountId != _accountId || authOk.LoginId1 != _loginId1)
        {
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
        _stream.Dispose();
        _writeLock.Dispose();
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
                await SendNotifyActorInitAsync(cancellationToken);
                break;
            case PacketConstants.CzClientVersion:
                break;
            case PacketConstants.CzPingLive:
                await SendPingLiveAsync(cancellationToken);
                break;
            default:
                MapLogger.Warning($"Unknown map client packet 0x{packetType:X4}, disconnecting.");
                _client.Close();
                break;
        }
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
            _client.Close();
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

    private async Task<byte[]> ReadPacketAsync(short packetType, byte[] header, CancellationToken cancellationToken)
    {
        if (!PacketLengths.TryGetValue(packetType, out var length))
        {
            MapLogger.Warning($"Unknown map client packet 0x{packetType:X4}, disconnecting.");
            return Array.Empty<byte>();
        }

        var payloadLength = length - 2;
        var payload = payloadLength == 0 ? Array.Empty<byte>() : await ReadExactAsync(payloadLength, cancellationToken);
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

    private async Task<byte[]> ReadExactAsync(int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var bytes = await _stream.ReadAsync(buffer.AsMemory(read, length - read), cancellationToken);
            if (bytes == 0)
            {
                return Array.Empty<byte>();
            }

            read += bytes;
        }

        return buffer;
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

    private static void WritePackedPosition(Span<byte> buffer, ushort x, ushort y, byte direction)
    {
        buffer[0] = (byte)(x >> 2);
        buffer[1] = (byte)((x << 6) | ((y >> 4) & 0x3f));
        buffer[2] = (byte)((y << 4) | (direction & 0x0f));
    }
}
