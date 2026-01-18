using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Athena.Net.CharServer.Config;
using Athena.Net.CharServer.Logging;

namespace Athena.Net.CharServer.Net;

public sealed class MapServerSession : IDisposable, ISession
{
    private static readonly Dictionary<short, int> PacketLengths = new()
    {
        [PacketConstants.MapLogin] = 60,
        [PacketConstants.MapAuthRequest] = 20,
    };

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly CharConfigStore _configStore;
    private readonly MapServerRegistry _registry;
    private readonly MapAuthManager _authManager;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _authenticated;
    private IPAddress _mapIp = IPAddress.Loopback;
    private int _mapPort;
    private byte[]? _prefetchedHeader;

    public MapServerSession(int sessionId, TcpClient client, CharConfigStore configStore, MapServerRegistry registry, MapAuthManager authManager, byte[]? prefetchedHeader = null)
    {
        SessionId = sessionId;
        _client = client;
        _stream = client.GetStream();
        _configStore = configStore;
        _registry = registry;
        _authManager = authManager;
        _prefetchedHeader = prefetchedHeader;
    }

    public int SessionId { get; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var header = _prefetchedHeader ?? await ReadExactAsync(2, cancellationToken);
            _prefetchedHeader = null;
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

    public void Dispose()
    {
        _stream.Dispose();
        _writeLock.Dispose();
        _registry.Remove(SessionId);
    }

    private async Task HandlePacketAsync(short packetType, byte[] packet, CancellationToken cancellationToken)
    {
        switch (packetType)
        {
            case PacketConstants.MapLogin:
                await HandleLoginAsync(packet, cancellationToken);
                break;
            case PacketConstants.MapSendMaps:
                await HandleMapListAsync(packet, cancellationToken);
                break;
            case PacketConstants.MapAuthRequest:
                await HandleAuthRequestAsync(packet, cancellationToken);
                break;
            default:
                CharLogger.Warning($"Unknown map server packet 0x{packetType:X4}, disconnecting.");
                _client.Close();
                break;
        }
    }

    private async Task HandleLoginAsync(byte[] packet, CancellationToken cancellationToken)
    {
        var userId = ReadFixedString(packet.AsSpan(2, PacketConstants.NameLength));
        var password = ReadFixedString(packet.AsSpan(26, PacketConstants.NameLength));
        var ipBytes = packet.AsSpan(54, 4).ToArray();
        _mapIp = new IPAddress(ipBytes);
        _mapPort = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(58, 2));

        var config = _configStore.Current;
        if (!string.Equals(userId, config.UserId, StringComparison.Ordinal) ||
            !string.Equals(password, config.Password, StringComparison.Ordinal))
        {
            await SendLoginAckAsync(3, cancellationToken);
            CharLogger.Warning($"Map server login rejected for session {SessionId}.");
            _client.Close();
            return;
        }

        _authenticated = true;
        _registry.TryRegister(SessionId, _mapIp, _mapPort, this);
        await SendLoginAckAsync(0, cancellationToken);
        CharLogger.Status($"Map server registered from {_mapIp}:{_mapPort}.");
    }

    private Task HandleMapListAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!_authenticated)
        {
            return Task.CompletedTask;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2, 2));
        if (length < 4)
        {
            return Task.CompletedTask;
        }

        var maps = new List<string>();
        var offset = 4;
        while (offset + PacketConstants.MapNameLength <= length && offset + PacketConstants.MapNameLength <= packet.Length)
        {
            var name = ReadFixedString(packet.AsSpan(offset, PacketConstants.MapNameLength));
            if (!string.IsNullOrWhiteSpace(name))
            {
                maps.Add(name);
            }

            offset += PacketConstants.MapNameLength;
        }

        _registry.UpdateMaps(SessionId, maps);
        return Task.CompletedTask;
    }

    private async Task HandleAuthRequestAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!_authenticated)
        {
            return;
        }

        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        var charId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(6, 4));
        var loginId1 = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(10, 4));
        var sex = packet[14];
        var ipBytes = packet.AsSpan(15, 4).ToArray();
        var clientIp = new IPAddress(ipBytes);

        if (_authManager.TryGet(accountId, out var node) &&
            node.CharId == charId &&
            node.LoginId1 == loginId1 &&
            node.Sex == sex)
        {
            _authManager.TryRemove(accountId, out _);
            await SendAuthOkAsync(node, cancellationToken);
            return;
        }

        await SendAuthFailAsync(accountId, charId, loginId1, sex, clientIp, cancellationToken);
    }

    private Task SendLoginAckAsync(byte result, CancellationToken cancellationToken)
    {
        var buffer = new byte[3];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.MapLoginAck);
        buffer[2] = result;
        return WriteAsync(buffer, cancellationToken);
    }

    private Task SendAuthOkAsync(MapAuthNode node, CancellationToken cancellationToken)
    {
        var length = MapAuthOkLength;
        var buffer = new byte[length];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.MapAuthOk);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(2, 2), (ushort)length);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), node.AccountId);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8, 4), node.LoginId1);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12, 4), node.LoginId2);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16, 4), node.ExpirationTime);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(20, 4), node.GroupId);
        buffer[24] = node.ChangingMapServers ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(25, 4), node.CharId);
        WriteFixedString(buffer.AsSpan(29, PacketConstants.MapNameLength), node.MapName);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(45, 2), node.X);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(47, 2), node.Y);
        buffer[49] = node.Direction;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(50, 2), node.Font);
        buffer[52] = node.Sex;

        return WriteAsync(buffer, cancellationToken);
    }

    private Task SendAuthFailAsync(uint accountId, uint charId, uint loginId1, byte sex, IPAddress ip, CancellationToken cancellationToken)
    {
        var buffer = new byte[19];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.MapAuthFail);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2, 4), accountId);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(6, 4), charId);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(10, 4), loginId1);
        buffer[14] = sex;
        var ipBytes = ip.MapToIPv4().GetAddressBytes();
        ipBytes.CopyTo(buffer.AsSpan(15, 4));
        return WriteAsync(buffer, cancellationToken);
    }

    private async Task<byte[]> ReadPacketAsync(short packetType, byte[] header, CancellationToken cancellationToken)
    {
        if (packetType == PacketConstants.MapSendMaps)
        {
            var lengthBytes = await ReadExactAsync(2, cancellationToken);
            if (lengthBytes.Length == 0)
            {
                return Array.Empty<byte>();
            }

            var packetLength = BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes);
            if (packetLength < 4)
            {
                return Array.Empty<byte>();
            }

            var remaining = packetLength - 4;
            var rest = remaining == 0 ? Array.Empty<byte>() : await ReadExactAsync(remaining, cancellationToken);
            if (remaining > 0 && rest.Length == 0)
            {
                return Array.Empty<byte>();
            }

            var packetBuffer = new byte[packetLength];
            Buffer.BlockCopy(header, 0, packetBuffer, 0, 2);
            Buffer.BlockCopy(lengthBytes, 0, packetBuffer, 2, 2);
            if (remaining > 0)
            {
                Buffer.BlockCopy(rest, 0, packetBuffer, 4, remaining);
            }

            return packetBuffer;
        }

        if (!PacketLengths.TryGetValue(packetType, out var fixedLength))
        {
            CharLogger.Warning($"Unknown map server packet 0x{packetType:X4}, disconnecting.");
            return Array.Empty<byte>();
        }

        var payloadLength = fixedLength - 2;
        var payload = payloadLength == 0 ? Array.Empty<byte>() : await ReadExactAsync(payloadLength, cancellationToken);
        if (payloadLength > 0 && payload.Length == 0)
        {
            return Array.Empty<byte>();
        }

        var packet = new byte[fixedLength];
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

    private static string ReadFixedString(ReadOnlySpan<byte> buffer)
    {
        var end = buffer.IndexOf((byte)0);
        if (end < 0)
        {
            end = buffer.Length;
        }

        return Encoding.ASCII.GetString(buffer.Slice(0, end));
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

    private static int MapAuthOkLength => 2 + 2 + 4 + 4 + 4 + 4 + 4 + 1 + 4 + PacketConstants.MapNameLength + 2 + 2 + 1 + 2 + 1;
}
