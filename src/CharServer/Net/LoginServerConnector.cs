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
        [PacketConstants.LcServiceAuthChallenge] = 2 + PacketConstants.ServiceNonceLength,
        [PacketConstants.LcServiceAuthResult] = 3,
        [PacketConstants.LcAuthResponse] = 21,
        [PacketConstants.LcAccountDataResponse] = 75,
        [PacketConstants.LcKeepAliveResponse] = 2,
        [PacketConstants.LcIpSyncRequest] = 2,
    };

    private readonly CharConfigStore _configStore;
    private readonly CharServerServiceTokenProvider _serviceTokenProvider;
    private readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(10);
    private readonly ConcurrentDictionary<int, ClientSession> _authRequests = new();
    private readonly ConcurrentDictionary<uint, ClientSession> _accountRequests = new();
    private LoginConnectionState? _connection;

    public LoginServerConnector(CharConfigStore configStore, CharServerServiceTokenProvider serviceTokenProvider)
    {
        _configStore = configStore;
        _serviceTokenProvider = serviceTokenProvider;
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

    // Reuses the existing stock LoginServer packet LcUserCount (0x2714, "2-byte id + 4-byte uint32
    // count") - see src/LoginServer/Net/ClientSession.cs's own HandleUserCount. `totalUsers` is the
    // ALREADY-AGGREGATED sum across every currently-registered MapServer connection
    // (MapServerRegistry.TotalUsers) - this method sends exactly one absolute total, never a
    // per-MapServer delta, so LoginServer's own CharServerInfo.Users always reflects the current
    // aggregate regardless of which MapServer's report triggered this call. False (no-op) when not
    // currently connected to LoginServer - the very next successful (re)connect has no way to know
    // the count changed while disconnected, but the next real count-changing event on THIS process
    // will send the correct current total anyway.
    public bool TrySendUserCount(uint totalUsers)
    {
        var connection = _connection;
        if (connection == null)
        {
            return false;
        }

        var buffer = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.LcUserCount);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2, 4), totalUsers);
        _ = connection.WriteAsync(buffer, CancellationToken.None);
        return true;
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

    public bool TrySendPincodeUpdate(uint accountId, string pincode)
    {
        var connection = _connection;
        if (connection == null)
        {
            return false;
        }

        var buffer = new byte[13];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.LcPincodeUpdate);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(2, 2), 13);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), accountId);
        WriteFixedString(buffer.AsSpan(8, 5), pincode);

        _ = connection.WriteAsync(buffer, CancellationToken.None);
        return true;
    }

    public bool TrySendPincodeAuthFail(uint accountId)
    {
        var connection = _connection;
        if (connection == null)
        {
            return false;
        }

        var buffer = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.LcPincodeAuthFail);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2, 4), accountId);

        _ = connection.WriteAsync(buffer, CancellationToken.None);
        return true;
    }

    private async Task<bool> TryConnectAsync(CancellationToken cancellationToken)
    {
        var config = _configStore.Current;

        if (!_serviceTokenProvider.IsConfigured)
        {
            CharLogger.Error(
                "CharServer ServiceToken is not configured (ServiceAuthentication.CharServer.Token in " +
                "solutionfiles/secrets/secret.json, or the ATHENA_NET_CHAR_SERVER_SERVICE_TOKEN environment " +
                "variable). Cannot authenticate to the login server.");
            return false;
        }

        if (!ServiceHelloFieldValidator.TryValidate(config.ServiceId, config.ServerName, out var helloFields, out var validationError))
        {
            // Fail before ever attempting the handshake: a value that cannot
            // be represented losslessly on the wire would otherwise make the
            // HMAC proof diverge from what LoginServer actually receives.
            CharLogger.Error($"CharServer service-auth registration fields are invalid: {validationError} Cannot authenticate to the login server.");
            return false;
        }

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(config.LoginIp, config.LoginPort, cancellationToken);
            client.NoDelay = true;

            CharLogger.Status($"Connected to login server {config.LoginIp}:{config.LoginPort}.");

            using var stream = client.GetStream();
            var connection = new LoginConnectionState(stream);

            if (!await AuthenticateAsync(connection, stream, config, helloFields, cancellationToken))
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
            CharLogger.Warning(
                $"Login server connect failed ({config.LoginIp}:{config.LoginPort}): {ex.Message}");
            return false;
        }
        catch (IOException ex)
        {
            CharLogger.Warning(
                $"Login server connection error ({config.LoginIp}:{config.LoginPort}): {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            CharLogger.Warning(
                $"Login server connection error ({config.LoginIp}:{config.LoginPort}): {ex.Message}");
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

    /// <summary>
    /// Drives the Athena.NET-internal HMAC-SHA256 service authentication
    /// handshake against LoginServer (see ai/login-server.md, "Inter-server
    /// service authentication"): send ServiceId -&gt; receive a one-time nonce
    /// challenge -&gt; send an HMAC-SHA256 proof derived from the shared
    /// ServiceToken (never the token itself) -&gt; receive the result. Any
    /// failure - malformed/unexpected packet, or a non-zero result byte -
    /// aborts the connection attempt; the outer retry loop reconnects.
    /// </summary>
    private async Task<bool> AuthenticateAsync(LoginConnectionState connection, NetworkStream stream, CharConfig config, ValidatedServiceHelloFields helloFields, CancellationToken cancellationToken)
    {
        await SendServiceHelloAsync(connection, config, helloFields, cancellationToken);

        var challengePacket = await ReadPacketAsync(stream, cancellationToken);
        if (challengePacket.Length == 0)
        {
            return false;
        }

        var challengeType = BinaryPrimitives.ReadInt16LittleEndian(challengePacket.AsSpan(0, 2));
        if (challengeType != PacketConstants.LcServiceAuthChallenge)
        {
            CharLogger.Warning($"Expected service auth challenge, got 0x{challengeType:X4}.");
            return false;
        }

        var nonce = challengePacket.AsSpan(2, PacketConstants.ServiceNonceLength).ToArray();
        // ServiceId/ServerName come from the same validated helloFields that
        // SendServiceHelloAsync wrote to the wire (never re-read from
        // config), so the proof is guaranteed to be computed over exactly
        // what LoginServer will decode.
        var proof = ServiceAuthProofCalculator.ComputeProof(
            _serviceTokenProvider.TokenBytes!,
            helloFields.ServiceId,
            config.CharIp,
            (ushort)config.CharPort,
            helloFields.ServerName,
            (ushort)config.CharMaintenance,
            (ushort)config.CharNewDisplay,
            nonce);

        await SendServiceAuthProofAsync(connection, proof, cancellationToken);

        var resultPacket = await ReadPacketAsync(stream, cancellationToken);
        if (resultPacket.Length == 0)
        {
            return false;
        }

        var resultType = BinaryPrimitives.ReadInt16LittleEndian(resultPacket.AsSpan(0, 2));
        if (resultType != PacketConstants.LcServiceAuthResult)
        {
            CharLogger.Warning($"Expected service auth result, got 0x{resultType:X4}.");
            return false;
        }

        var result = resultPacket[2];
        if (result != 0)
        {
            CharLogger.Error($"Login server rejected char server service authentication (code {result}).");
            return false;
        }

        CharLogger.Status("Login server accepted char server service authentication.");
        return true;
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

        if (result != 0)
        {
            CharLogger.Warning($"Login auth failed (sex={sex}, result={result}).");
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
        var pincode = ReadFixedString(packet.AsSpan(63, 5));
        var pincodeChange = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(68, 4));
        var charSlots = packet[51];
        var isVip = packet[72] != 0;
        var vipSlots = packet[73];
        var billingSlots = packet[74];

        _ = session.HandleAccountDataAsync(accountId, charSlots, isVip, vipSlots, billingSlots, email, birthdate, pincode, pincodeChange);
        return true;
    }

    /// <summary>
    /// LcServiceHello wire layout (56 bytes total; must match
    /// src/LoginServer/Net/ClientSession.cs's HandleServiceHelloAsync exactly):
    /// 2 header + 24 ServiceId + 4 IP + 2 port + 20 server name + 2 maintenance + 2 new-display.
    /// ServiceId/ServerName come from <paramref name="helloFields"/> - already
    /// validated by <see cref="ServiceHelloFieldValidator"/> to fit losslessly
    /// in these fixed-width ASCII fields - never re-read from
    /// <see cref="CharConfig"/> directly, so this and the HMAC proof
    /// computation in <see cref="AuthenticateAsync"/> can never diverge.
    /// </summary>
    private static async Task SendServiceHelloAsync(LoginConnectionState connection, CharConfig config, ValidatedServiceHelloFields helloFields, CancellationToken cancellationToken)
    {
        var buffer = new byte[2 + PacketConstants.NameLength + 4 + 2 + PacketConstants.ServerNameLength + 2 + 2];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.LcServiceHello);
        WriteFixedString(buffer.AsSpan(2, PacketConstants.NameLength), helloFields.ServiceId);

        var ipOffset = 2 + PacketConstants.NameLength;
        var ipBytes = config.CharIp.MapToIPv4().GetAddressBytes();
        ipBytes.CopyTo(buffer.AsSpan(ipOffset, 4));
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(ipOffset + 4, 2), (ushort)config.CharPort);

        var nameOffset = ipOffset + 4 + 2;
        WriteFixedString(buffer.AsSpan(nameOffset, PacketConstants.ServerNameLength), helloFields.ServerName);

        var maintenanceOffset = nameOffset + PacketConstants.ServerNameLength;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(maintenanceOffset, 2), (ushort)config.CharMaintenance);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(maintenanceOffset + 2, 2), (ushort)config.CharNewDisplay);

        await connection.WriteAsync(buffer, cancellationToken);
    }

    private static async Task SendServiceAuthProofAsync(LoginConnectionState connection, byte[] proof, CancellationToken cancellationToken)
    {
        var buffer = new byte[2 + PacketConstants.ServiceProofLength];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.LcServiceAuthProof);
        proof.CopyTo(buffer.AsSpan(2, PacketConstants.ServiceProofLength));
        await connection.WriteAsync(buffer, cancellationToken);
    }

    /// <summary>
    /// Internal (not private) so tests can prove this exact wire-encoding
    /// function round-trips losslessly for a <see cref="ServiceHelloFieldValidator"/>-validated
    /// value, and that the resulting bytes are exactly what
    /// <see cref="ServiceAuthProofCalculator"/> was given - see
    /// ServiceHelloWireCanonicalizationTests in CharServer.Tests.
    /// </summary>
    internal static void WriteFixedString(Span<byte> buffer, string value)
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

    internal static string ReadFixedString(ReadOnlySpan<byte> buffer)
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
