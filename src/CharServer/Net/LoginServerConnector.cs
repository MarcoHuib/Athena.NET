using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Athena.Net.CharServer.Config;
using Athena.Net.CharServer.Logging;

namespace Athena.Net.CharServer.Net;

public sealed class LoginServerConnector
{
    private static readonly Dictionary<short, int> PacketLengths = new()
    {
        [PacketConstants.LcCharServerLoginAck] = 3,
        [PacketConstants.LcAuthResponse] = 21,
        [PacketConstants.LcAccountDataResponse] = 75,
        [PacketConstants.LcKeepAliveResponse] = 2,
        [PacketConstants.LcIpSyncRequest] = 2,
    };

    private readonly CharConfigStore _configStore;
    private readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(10);
    private readonly ConcurrentDictionary<int, ClientSession> _authRequests = new();
    private readonly ConcurrentDictionary<uint, ClientSession> _accountRequests = new();
    private LoginConnectionState? _connection;

    public LoginServerConnector(CharConfigStore configStore)
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

    public bool TrySendAuthRequest(ClientSession session, uint accountId, uint loginId1, uint loginId2, byte sex, IPAddress clientIp)
    {
        var connection = _connection;
        if (connection == null)
        {
            return false;
        }

        _authRequests[session.SessionId] = session;

        var buffer = new byte[23];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.LcAuthRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2, 4), accountId);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(6, 4), loginId1);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(10, 4), loginId2);
        buffer[14] = sex;
        var ipBytes = clientIp.MapToIPv4().GetAddressBytes();
        ipBytes.CopyTo(buffer.AsSpan(15, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(19, 4), (uint)session.SessionId);

        _ = connection.WriteAsync(buffer, CancellationToken.None);
        return true;
    }

    public bool TrySendAccountDataRequest(ClientSession session, uint accountId)
    {
        var connection = _connection;
        if (connection == null)
        {
            return false;
        }

        _accountRequests[accountId] = session;

        var buffer = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.LcAccountDataRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2, 4), accountId);

        _ = connection.WriteAsync(buffer, CancellationToken.None);
        return true;
    }

    private async Task<bool> TryConnectAsync(CancellationToken cancellationToken)
    {
        var config = _configStore.Current;

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(config.LoginIp, config.LoginPort, cancellationToken);
            client.NoDelay = true;

            CharLogger.Status($"Connected to login server {config.LoginIp}:{config.LoginPort}.");

            using var stream = client.GetStream();
            var connection = new LoginConnectionState(stream);

            await SendLoginPacketAsync(connection, config, cancellationToken);

            var firstPacket = await ReadPacketAsync(stream, cancellationToken);
            if (firstPacket.Length == 0)
            {
                return false;
            }

            if (!HandlePacket(firstPacket))
            {
                return false;
            }

            _connection = connection;
            await ListenAsync(stream, cancellationToken);
            _connection = null;
            await FailPendingAuthAsync();

            CharLogger.Warning("Login server connection closed. Reconnecting.");
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (SocketException ex)
        {
            CharLogger.Warning($"Login server connect failed: {ex.Message}");
            return false;
        }
        catch (IOException ex)
        {
            CharLogger.Warning($"Login server connection error: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            CharLogger.Warning($"Login server connection error: {ex.Message}");
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
            case PacketConstants.LcCharServerLoginAck:
                return HandleLoginAck(packet);
            case PacketConstants.LcAuthResponse:
                return HandleAuthResponse(packet);
            case PacketConstants.LcAccountDataResponse:
                return HandleAccountDataResponse(packet);
            case PacketConstants.LcIpSyncRequest:
                CharLogger.Info("Login server requested IP sync (not implemented yet).");
                return true;
            case PacketConstants.LcKeepAliveResponse:
                return true;
            default:
                CharLogger.Warning($"Unknown login server packet 0x{packetType:X4}, disconnecting.");
                return false;
        }
    }

    private static bool HandleLoginAck(byte[] packet)
    {
        if (packet.Length < 3)
        {
            return false;
        }

        var result = packet[2];
        if (result == 0)
        {
            CharLogger.Status("Login server accepted char server registration.");
            return true;
        }

        CharLogger.Error($"Login server rejected char server registration (code {result}).");
        return false;
    }

    private bool HandleAuthResponse(byte[] packet)
    {
        if (packet.Length < 21)
        {
            return false;
        }

        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        var loginId1 = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(6, 4));
        var loginId2 = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(10, 4));
        var sex = packet[14];
        var result = packet[15];
        var requestId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(16, 4));
        var clientType = packet[20];

        if (!_authRequests.TryRemove((int)requestId, out var session))
        {
            return true;
        }

        _ = session.HandleAuthResponseAsync(accountId, loginId1, loginId2, sex, result, clientType);
        return true;
    }

    private bool HandleAccountDataResponse(byte[] packet)
    {
        if (packet.Length < 75)
        {
            return false;
        }

        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        if (!_accountRequests.TryRemove(accountId, out var session))
        {
            return true;
        }

        var email = ReadFixedString(packet.AsSpan(6, 40));
        var birthdate = ReadFixedString(packet.AsSpan(52, 11));
        var charSlots = packet[51];
        var isVip = packet[72] != 0;
        var vipSlots = packet[73];
        var billingSlots = packet[74];

        _ = session.HandleAccountDataAsync(accountId, charSlots, isVip, vipSlots, billingSlots, email, birthdate);
        return true;
    }

    private static async Task SendLoginPacketAsync(LoginConnectionState connection, CharConfig config, CancellationToken cancellationToken)
    {
        var buffer = new byte[86];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.LcCharServerLogin);
        WriteFixedString(buffer.AsSpan(2, PacketConstants.NameLength), config.UserId);
        WriteFixedString(buffer.AsSpan(26, PacketConstants.NameLength), config.Password);
        buffer.AsSpan(50, 4).Clear();

        var ipBytes = config.CharIp.MapToIPv4().GetAddressBytes();
        ipBytes.CopyTo(buffer.AsSpan(54, 4));
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(58, 2), (ushort)config.CharPort);

        WriteFixedString(buffer.AsSpan(60, PacketConstants.ServerNameLength), config.ServerName);
        buffer.AsSpan(80, 2).Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(82, 2), (ushort)config.CharMaintenance);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(84, 2), (ushort)config.CharNewDisplay);

        await connection.WriteAsync(buffer, cancellationToken);
    }

    private static void WriteFixedString(Span<byte> buffer, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value ?? string.Empty);
        var length = Math.Min(bytes.Length, buffer.Length);
        if (length > 0)
        {
            bytes.AsSpan(0, length).CopyTo(buffer);
        }
        if (length < buffer.Length)
        {
            buffer[length..].Clear();
        }
    }

    private static string ReadFixedString(ReadOnlySpan<byte> buffer)
    {
        var length = buffer.IndexOf((byte)0);
        if (length < 0)
        {
            length = buffer.Length;
        }

        return Encoding.ASCII.GetString(buffer[..length]);
    }

    private static async Task<byte[]> ReadPacketAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = await ReadExactAsync(stream, 2, cancellationToken);
        if (header.Length == 0)
        {
            return Array.Empty<byte>();
        }

        var packetType = BinaryPrimitives.ReadInt16LittleEndian(header);
        if (!PacketLengths.TryGetValue(packetType, out var length))
        {
            CharLogger.Warning($"Unknown login server packet 0x{packetType:X4}, disconnecting.");
            return Array.Empty<byte>();
        }

        var payloadLength = length - 2;
        var packet = new byte[length];
        Buffer.BlockCopy(header, 0, packet, 0, 2);

        if (payloadLength > 0)
        {
            var payload = await ReadExactAsync(stream, payloadLength, cancellationToken);
            if (payload.Length == 0)
            {
                return Array.Empty<byte>();
            }

            Buffer.BlockCopy(payload, 0, packet, 2, payloadLength);
        }

        return packet;
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

    private async Task FailPendingAuthAsync()
    {
        var sessions = _authRequests.Values.Distinct().ToArray();
        _authRequests.Clear();
        _accountRequests.Clear();

        foreach (var session in sessions)
        {
            await session.SendRefuseEnterAsync(0, CancellationToken.None);
        }
    }

    private sealed class LoginConnectionState
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public LoginConnectionState(NetworkStream stream)
        {
            Stream = stream;
        }

        public NetworkStream Stream { get; }

        public async Task WriteAsync(byte[] payload, CancellationToken cancellationToken)
        {
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await Stream.WriteAsync(payload, cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }
        }
    }
}
