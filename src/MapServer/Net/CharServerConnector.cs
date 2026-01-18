using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Logging;

namespace Athena.Net.MapServer.Net;

public sealed class CharServerConnector
{
    private static readonly Dictionary<short, int> PacketLengths = new()
    {
        [PacketConstants.MapLoginAck] = 3,
        [PacketConstants.MapAuthFail] = 19,
    };

    private readonly MapConfigStore _configStore;
    private readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(10);
    private readonly ConcurrentDictionary<uint, PendingAuthRequest> _pendingAuth = new();
    private CharServerConnectionState? _connection;

    public CharServerConnector(MapConfigStore configStore)
    {
        _configStore = configStore;
    }

    public bool IsConnected => _connection != null;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var connected = await TryConnectAsync(cancellationToken);
            if (!connected && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_retryDelay, cancellationToken);
            }
        }
    }

    public bool TrySendAuthRequest(MapClientSession session, uint accountId, uint charId, uint loginId1, byte sex, IPAddress clientIp)
    {
        var connection = _connection;
        if (connection == null)
        {
            return false;
        }

        _pendingAuth[accountId] = new PendingAuthRequest(session, loginId1);

        var buffer = new byte[20];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.MapAuthRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2, 4), accountId);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(6, 4), charId);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(10, 4), loginId1);
        buffer[14] = sex;
        var ipBytes = clientIp.MapToIPv4().GetAddressBytes();
        ipBytes.CopyTo(buffer.AsSpan(15, 4));
        buffer[19] = 0;

        _ = connection.WriteAsync(buffer, CancellationToken.None);
        return true;
    }

    private async Task<bool> TryConnectAsync(CancellationToken cancellationToken)
    {
        var config = _configStore.Current;

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(config.CharIp, config.CharPort, cancellationToken);
            client.NoDelay = true;

            MapLogger.Status($"Connected to char server {config.CharIp}:{config.CharPort}.");

            using var stream = client.GetStream();
            var connection = new CharServerConnectionState(stream);

            await SendLoginPacketAsync(connection, config, cancellationToken);

            var firstPacket = await ReadPacketAsync(stream, cancellationToken);
            if (firstPacket.Length == 0)
            {
                return false;
            }

            _connection = connection;
            if (!HandlePacket(firstPacket))
            {
                _connection = null;
                return false;
            }

            await ListenAsync(stream, cancellationToken);
            _connection = null;
            FailPendingAuth();

            MapLogger.Warning("Char server connection closed. Reconnecting.");
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (SocketException ex)
        {
            MapLogger.Warning(
                $"Char server connect failed ({config.CharIp}:{config.CharPort}): {ex.Message}");
            return false;
        }
        catch (IOException ex)
        {
            MapLogger.Warning(
                $"Char server connection error ({config.CharIp}:{config.CharPort}): {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            MapLogger.Warning(
                $"Char server connection error ({config.CharIp}:{config.CharPort}): {ex.Message}");
            return false;
        }
    }

    private async Task ListenAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var packet = await ReadPacketAsync(stream, cancellationToken);
            if (packet.Length == 0)
            {
                return;
            }

            if (!HandlePacket(packet))
            {
                return;
            }
        }
    }

    private bool HandlePacket(byte[] packet)
    {
        if (packet.Length < 2)
        {
            return false;
        }

        var packetType = BinaryPrimitives.ReadInt16LittleEndian(packet.AsSpan(0, 2));
        switch (packetType)
        {
            case PacketConstants.MapLoginAck:
                return HandleLoginAck(packet);
            case PacketConstants.MapAuthOk:
                return HandleAuthOk(packet);
            case PacketConstants.MapAuthFail:
                return HandleAuthFail(packet);
            default:
                MapLogger.Warning($"Unknown char server packet 0x{packetType:X4}, disconnecting.");
                return false;
        }
    }

    private bool HandleLoginAck(byte[] packet)
    {
        if (packet.Length < 3)
        {
            return false;
        }

        var result = packet[2];
        if (result != 0)
        {
            MapLogger.Warning($"Char server rejected map server login (code {result}).");
            return false;
        }

        MapLogger.Status("Char server accepted map server registration.");
        _ = TrySendMapListAsync();
        return true;
    }

    private bool HandleAuthOk(byte[] packet)
    {
        if (packet.Length < 4)
        {
            return false;
        }

        if (!TryParseAuthOk(packet, out var authOk))
        {
            return false;
        }

        if (_pendingAuth.TryRemove(authOk.AccountId, out var pending) && pending.LoginId1 == authOk.LoginId1)
        {
            pending.Session.HandleAuthOk(authOk);
        }

        return true;
    }

    private bool HandleAuthFail(byte[] packet)
    {
        if (packet.Length < 19)
        {
            return false;
        }

        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        if (_pendingAuth.TryRemove(accountId, out var pending))
        {
            pending.Session.HandleAuthFail();
        }

        return true;
    }

    private static bool TryParseAuthOk(byte[] packet, out MapAuthOkData authOk)
    {
        authOk = default!;
        if (packet.Length < 4)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2, 2));
        if (length != packet.Length)
        {
            return false;
        }

        if (packet.Length < MapAuthOkData.MinimumLength)
        {
            return false;
        }

        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4, 4));
        var loginId1 = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8, 4));
        var loginId2 = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12, 4));
        var expirationTime = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(16, 4));
        var groupId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(20, 4));
        var changing = packet[24] != 0;
        var charId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(25, 4));
        var mapName = ReadFixedString(packet.AsSpan(29, PacketConstants.MapNameLength));
        var x = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(45, 2));
        var y = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(47, 2));
        var direction = packet[49];
        var font = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(50, 2));
        var sex = packet[52];

        authOk = new MapAuthOkData(
            accountId,
            charId,
            loginId1,
            loginId2,
            expirationTime,
            groupId,
            changing,
            mapName,
            x,
            y,
            direction,
            font,
            sex);

        return true;
    }

    private static string ReadFixedString(ReadOnlySpan<byte> buffer)
    {
        var end = buffer.IndexOf((byte)0);
        if (end < 0)
        {
            end = buffer.Length;
        }

        return Encoding.ASCII.GetString(buffer.Slice(0, end));
    }

    private void FailPendingAuth()
    {
        foreach (var pending in _pendingAuth.Values)
        {
            pending.Session.HandleAuthFail();
        }

        _pendingAuth.Clear();
    }

    private static async Task<byte[]> ReadPacketAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = await ReadExactAsync(stream, 2, cancellationToken);
        if (header.Length == 0)
        {
            return Array.Empty<byte>();
        }

        var packetType = BinaryPrimitives.ReadInt16LittleEndian(header.AsSpan(0, 2));
        if (packetType == PacketConstants.MapAuthOk)
        {
            var lengthBytes = await ReadExactAsync(stream, 2, cancellationToken);
            if (lengthBytes.Length == 0)
            {
                return Array.Empty<byte>();
            }

            var packetLength = BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes.AsSpan(0, 2));
            if (packetLength < 4)
            {
                return Array.Empty<byte>();
            }

            var remaining = packetLength - 4;
            var rest = remaining == 0 ? Array.Empty<byte>() : await ReadExactAsync(stream, remaining, cancellationToken);
            if (remaining > 0 && rest.Length == 0)
            {
                return Array.Empty<byte>();
            }

            var packet = new byte[packetLength];
            Buffer.BlockCopy(header, 0, packet, 0, 2);
            Buffer.BlockCopy(lengthBytes, 0, packet, 2, 2);
            if (remaining > 0)
            {
                Buffer.BlockCopy(rest, 0, packet, 4, remaining);
            }

            return packet;
        }

        if (!PacketLengths.TryGetValue(packetType, out var length))
        {
            MapLogger.Warning($"Unknown char server packet 0x{packetType:X4}, disconnecting.");
            return Array.Empty<byte>();
        }

        var payloadLength = length - 2;
        var payload = payloadLength == 0 ? Array.Empty<byte>() : await ReadExactAsync(stream, payloadLength, cancellationToken);
        if (payloadLength > 0 && payload.Length == 0)
        {
            return Array.Empty<byte>();
        }

        var fullPacket = new byte[length];
        Buffer.BlockCopy(header, 0, fullPacket, 0, 2);
        if (payloadLength > 0)
        {
            Buffer.BlockCopy(payload, 0, fullPacket, 2, payloadLength);
        }

        return fullPacket;
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int length, CancellationToken cancellationToken)
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

    private static async Task SendLoginPacketAsync(CharServerConnectionState connection, MapConfig config, CancellationToken cancellationToken)
    {
        var buffer = new byte[60];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.MapLogin);
        WriteFixedString(buffer.AsSpan(2, PacketConstants.NameLength), config.UserId);
        WriteFixedString(buffer.AsSpan(26, PacketConstants.NameLength), config.Password);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(50, 4), 0);
        var ipBytes = config.MapIp.MapToIPv4().GetAddressBytes();
        ipBytes.CopyTo(buffer.AsSpan(54, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(58, 2), (ushort)config.MapPort);

        await connection.WriteAsync(buffer, cancellationToken);
    }

    private static void WriteFixedString(Span<byte> buffer, string value)
    {
        buffer.Clear();
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var bytes = Encoding.ASCII.GetBytes(value);
        var length = Math.Min(bytes.Length, buffer.Length);
        bytes.AsSpan(0, length).CopyTo(buffer);
    }

    private Task TrySendMapListAsync()
    {
        var connection = _connection;
        if (connection == null)
        {
            return Task.CompletedTask;
        }

        var buffer = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.MapSendMaps);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(2, 2), 4);
        return connection.WriteAsync(buffer, CancellationToken.None);
    }

    private sealed record PendingAuthRequest(MapClientSession Session, uint LoginId1);

    private sealed class CharServerConnectionState
    {
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public CharServerConnectionState(NetworkStream stream)
        {
            _stream = stream;
        }

        public async Task WriteAsync(byte[] payload, CancellationToken cancellationToken)
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
    }
}
