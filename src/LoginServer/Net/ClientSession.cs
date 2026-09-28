using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Athena.Net.LoginServer.Application;
using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Db;
using Athena.Net.LoginServer.Db.Entities;
using Athena.Net.LoginServer.Logging;

namespace Athena.Net.LoginServer.Net;

public sealed class ClientSession : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly LoginConfigStore _configStore;
    private readonly LoginMessageStore _messageStore;
    private readonly Func<LoginDbContext?> _dbFactory;
    private readonly Func<Db.Identity.AthenaIdentityDbContext?> _identityDbFactory;
    private readonly CharServerRegistry _charServers;
    private readonly LoginState _state;
    private readonly Config.SubnetConfig _subnetConfig;
    private readonly IPlayerAuthenticationService _playerAuth;
    private readonly IServiceAuthenticationService _serviceAuth;
    private readonly IPlayerIdentityAccountService _identityAccountService;
    private readonly TimeSpan _serviceAuthTimeout;
    private int? _charServerId;
    private CancellationTokenSource? _handshakeCts;
    private LoginConfig Config => _configStore.Current;
    private bool IsCaseSensitive => _configStore.LoginCaseSensitive;

    private static readonly Dictionary<short, int> PacketLengths = new()
    {
        [PacketConstants.CaLogin] = 2 + 4 + PacketConstants.NameLength + PacketConstants.NameLength + 1,
        [PacketConstants.CaConnectInfoChanged] = 2 + PacketConstants.NameLength,
        [PacketConstants.CaLoginPcBang] = 2 + 4 + PacketConstants.NameLength + PacketConstants.NameLength + 1 + 16 + 13,
        [PacketConstants.CaLoginChannel] = 2 + 4 + PacketConstants.NameLength + PacketConstants.NameLength + 1 + 16 + 13 + 1,
        [PacketConstants.LcServiceHello] = 2 + PacketConstants.NameLength + 4 + 2 + PacketConstants.ServerNameLength + 2 + 2,
        [PacketConstants.LcServiceAuthProof] = 2 + PacketConstants.ServiceProofLength,
        [PacketConstants.LcAuthRequest] = 23,
        [PacketConstants.LcUserCount] = 6,
        [PacketConstants.LcAccountDataRequest] = 6,
        [PacketConstants.LcKeepAliveRequest] = 2,
        [PacketConstants.LcChangeEmailRequest] = 86,
        [PacketConstants.LcUpdateAccountState] = 10,
        [PacketConstants.LcBanAccount] = 10,
        [PacketConstants.LcChangeSex] = 6,
        [PacketConstants.LcUnbanAccount] = 6,
        [PacketConstants.LcVipRequest] = 15,
        [PacketConstants.LcSetAccountOnline] = 6,
        [PacketConstants.LcSetAccountOffline] = 6,
        [PacketConstants.LcOnlineList] = 8,
        [PacketConstants.LcAccountInfoRequest] = 18,
        [PacketConstants.LcGlobalAccRegRequest] = 10,
        [PacketConstants.LcCharIpUpdate] = 6,
        [PacketConstants.LcSetAllOffline] = 2,
        [PacketConstants.LcPincodeAuthFail] = 6,
    };

    public ClientSession(TcpClient client, LoginConfigStore configStore, LoginMessageStore messageStore, Func<LoginDbContext?> dbFactory, CharServerRegistry charServers, LoginState state, Config.SubnetConfig subnetConfig)
        : this(client, configStore, messageStore, dbFactory, () => null, charServers, state, subnetConfig, new UnavailablePlayerAuthenticationService(), new ServiceAuthenticationService(new CharServerServiceTokenProvider(new SecretConfig())))
    {
    }

    public ClientSession(
        TcpClient client,
        LoginConfigStore configStore,
        LoginMessageStore messageStore,
        Func<LoginDbContext?> dbFactory,
        Func<Db.Identity.AthenaIdentityDbContext?> identityDbFactory,
        CharServerRegistry charServers,
        LoginState state,
        Config.SubnetConfig subnetConfig,
        IPlayerAuthenticationService playerAuth,
        IServiceAuthenticationService serviceAuth,
        IPlayerIdentityAccountService? identityAccountService = null,
        TimeSpan? serviceAuthTimeout = null)
    {
        _client = client;
        _configStore = configStore;
        _messageStore = messageStore;
        _dbFactory = dbFactory;
        _identityDbFactory = identityDbFactory;
        _charServers = charServers;
        _state = state;
        _subnetConfig = subnetConfig;
        _playerAuth = playerAuth;
        _serviceAuth = serviceAuth;
        _identityAccountService = identityAccountService ?? new UnavailablePlayerIdentityAccountService();
        _serviceAuthTimeout = serviceAuthTimeout ?? TimeSpan.FromSeconds(30);
        _stream = client.GetStream();
    }

    /// <summary>
    /// Runs the packet loop for this connection's lifetime. A per-connection
    /// <see cref="CancellationTokenSource"/>, linked to the server-lifetime
    /// <paramref name="cancellationToken"/>, is used for every read so the
    /// service-auth handshake can impose a real wall-clock timeout (see
    /// <see cref="HandleServiceHelloAsync"/>) on just the "waiting for
    /// LcServiceAuthProof" window, without affecting the outer token or the
    /// normal long-lived authenticated CharServer connection once that
    /// window closes.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _handshakeCts = linkedCts;
        var token = linkedCts.Token;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var header = await ReadExactAsync(2, token);
                if (header.Length == 0)
                {
                    return;
                }

                var packetType = BinaryPrimitives.ReadInt16LittleEndian(header);
                var packet = await ReadPacketAsync(packetType, header, token);
                if (packet.Length == 0)
                {
                    return;
                }

                await HandlePacketAsync(packetType, packet, token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The outer server-lifetime token is still alive, so this
            // cancellation came only from the per-connection handshake
            // timeout: a challenge was issued but LcServiceAuthProof never
            // arrived within the timeout window. No CharServer is registered
            // and no service-auth state remains active - the connection is
            // simply closed like any other malformed/abandoned connection.
            LoginLogger.Warning("Service authentication handshake timed out waiting for LcServiceAuthProof. Closing connection.");
        }
        finally
        {
            _handshakeCts = null;
        }
    }

    public void Dispose()
    {
        if (_charServerId.HasValue)
        {
            _charServers.Unregister(_charServerId.Value);
            _state.MarkOnlineUsersUnknownByCharServer(_charServerId.Value);
        }

        _stream.Dispose();
    }

    private async Task<byte[]> ReadPacketAsync(short packetType, byte[] header, CancellationToken cancellationToken)
    {
        if (packetType == PacketConstants.CaSsoLoginReq)
        {
            var lengthBytes = await ReadExactAsync(2, cancellationToken);
            if (lengthBytes.Length == 0)
            {
                return Array.Empty<byte>();
            }

            var packetLength = BinaryPrimitives.ReadInt16LittleEndian(lengthBytes);
            if (packetLength < 4)
            {
                return Array.Empty<byte>();
            }

            var packet = new byte[packetLength];
            Buffer.BlockCopy(header, 0, packet, 0, 2);
            Buffer.BlockCopy(lengthBytes, 0, packet, 2, 2);

            var remaining = packetLength - 4;
            var body = await ReadExactAsync(remaining, cancellationToken);
            if (body.Length == 0)
            {
                return Array.Empty<byte>();
            }

            Buffer.BlockCopy(body, 0, packet, 4, remaining);
            return packet;
        }

        if (packetType == PacketConstants.LcAccountReg2Update)
        {
            var lengthBytes = await ReadExactAsync(2, cancellationToken);
            if (lengthBytes.Length == 0)
            {
                return Array.Empty<byte>();
            }

            var packetLength = BinaryPrimitives.ReadInt16LittleEndian(lengthBytes);
            if (packetLength < 4)
            {
                return Array.Empty<byte>();
            }

            var packet = new byte[packetLength];
            Buffer.BlockCopy(header, 0, packet, 0, 2);
            Buffer.BlockCopy(lengthBytes, 0, packet, 2, 2);

            var remaining = packetLength - 4;
            var body = await ReadExactAsync(remaining, cancellationToken);
            if (body.Length == 0)
            {
                return Array.Empty<byte>();
            }

            Buffer.BlockCopy(body, 0, packet, 4, remaining);
            return packet;
        }

        if (packetType == PacketConstants.LcPincodeUpdate)
        {
            var lengthBytes = await ReadExactAsync(2, cancellationToken);
            if (lengthBytes.Length == 0)
            {
                return Array.Empty<byte>();
            }

            var packetLength = BinaryPrimitives.ReadInt16LittleEndian(lengthBytes);
            if (packetLength < 4)
            {
                return Array.Empty<byte>();
            }

            var packet = new byte[packetLength];
            Buffer.BlockCopy(header, 0, packet, 0, 2);
            Buffer.BlockCopy(lengthBytes, 0, packet, 2, 2);

            var remaining = packetLength - 4;
            var body = await ReadExactAsync(remaining, cancellationToken);
            if (body.Length == 0)
            {
                return Array.Empty<byte>();
            }

            Buffer.BlockCopy(body, 0, packet, 4, remaining);
            return packet;
        }

        if (!PacketLengths.TryGetValue(packetType, out var length))
        {
            LoginLogger.Warning($"Unknown packet 0x{packetType:X4}, closing session.");
            return Array.Empty<byte>();
        }

        var payloadLength = length - 2;
        byte[] payload;
        if (payloadLength == 0)
        {
            payload = Array.Empty<byte>();
        }
        else
        {
            payload = await ReadExactAsync(payloadLength, cancellationToken);
            if (payload.Length == 0)
            {
                return Array.Empty<byte>();
            }
        }

        var packetFixed = new byte[length];
        Buffer.BlockCopy(header, 0, packetFixed, 0, 2);
        if (payloadLength > 0)
        {
            Buffer.BlockCopy(payload, 0, packetFixed, 2, payloadLength);
        }
        return packetFixed;
    }

    /// <summary>
    /// Inter-server packets that must never execute for a socket that has not
    /// successfully completed the LcServiceHello -&gt; LcServiceAuthChallenge -&gt;
    /// LcServiceAuthProof HMAC handshake. LcServiceHello and LcServiceAuthProof
    /// are intentionally excluded - they are the only two packets an
    /// unauthenticated service socket is allowed to send (the handshake
    /// itself).
    /// </summary>
    private static readonly HashSet<short> ServiceOnlyPackets = new()
    {
        PacketConstants.LcAuthRequest,
        PacketConstants.LcUserCount,
        PacketConstants.LcAccountDataRequest,
        PacketConstants.LcKeepAliveRequest,
        PacketConstants.LcChangeEmailRequest,
        PacketConstants.LcUpdateAccountState,
        PacketConstants.LcBanAccount,
        PacketConstants.LcChangeSex,
        PacketConstants.LcUnbanAccount,
        PacketConstants.LcVipRequest,
        PacketConstants.LcSetAccountOnline,
        PacketConstants.LcSetAccountOffline,
        PacketConstants.LcOnlineList,
        PacketConstants.LcAccountInfoRequest,
        PacketConstants.LcAccountReg2Update,
        PacketConstants.LcGlobalAccRegRequest,
        PacketConstants.LcCharIpUpdate,
        PacketConstants.LcSetAllOffline,
        PacketConstants.LcPincodeUpdate,
        PacketConstants.LcPincodeAuthFail,
    };

    private async Task HandlePacketAsync(short packetType, byte[] packet, CancellationToken cancellationToken)
    {
        if (ServiceOnlyPackets.Contains(packetType) && !_serviceAuth.IsAuthenticated)
        {
            LoginLogger.Warning($"Rejected inter-server packet 0x{packetType:X4} from an unauthenticated socket.");
            return;
        }

        switch (packetType)
        {
            case PacketConstants.CaConnectInfoChanged:
                break;
            case PacketConstants.CaLogin:
            {
                if (packet.Length < 55)
                {
                    _client.Close();
                    break;
                }
                var request = ParsePlainLogin(packet);
                LoginLogger.Info($"Client login request: packet=0x{packetType:X4} len={packet.Length} clientType={request.ClientType}");
                await HandleLoginAsync(request, cancellationToken);
                break;
            }
            case PacketConstants.CaLoginPcBang:
            {
                if (packet.Length < 55)
                {
                    _client.Close();
                    break;
                }
                var request = ParsePlainLogin(packet);
                LoginLogger.Info($"Client login request: packet=0x{packetType:X4} len={packet.Length} clientType={request.ClientType}");
                await HandleLoginAsync(request, cancellationToken);
                break;
            }
            case PacketConstants.CaLoginChannel:
            {
                if (packet.Length < 55)
                {
                    _client.Close();
                    break;
                }
                var request = ParsePlainLogin(packet);
                LoginLogger.Info($"Client login request: packet=0x{packetType:X4} len={packet.Length} clientType={request.ClientType}");
                await HandleLoginAsync(request, cancellationToken);
                break;
            }
            case PacketConstants.CaSsoLoginReq:
            {
                if (packet.Length < 92)
                {
                    _client.Close();
                    break;
                }
                var request = ParseSsoLogin(packet);
                LoginLogger.Info($"Client login request: packet=0x{packetType:X4} len={packet.Length} clientType={request.ClientType}");
                await HandleLoginAsync(request, cancellationToken);
                break;
            }
            case PacketConstants.LcServiceHello:
                await HandleServiceHelloAsync(packet, cancellationToken);
                break;
            case PacketConstants.LcServiceAuthProof:
                await HandleServiceAuthProofAsync(packet, cancellationToken);
                break;
            case PacketConstants.LcAuthRequest:
                await HandleAuthRequestAsync(packet, cancellationToken);
                break;
            case PacketConstants.LcUserCount:
                HandleUserCount(packet);
                break;
            case PacketConstants.LcAccountDataRequest:
                await HandleAccountDataRequestAsync(packet, cancellationToken);
                break;
            case PacketConstants.LcKeepAliveRequest:
                await SendKeepAliveAsync(cancellationToken);
                break;
            case PacketConstants.LcChangeEmailRequest:
                await HandleChangeEmailAsync(packet, cancellationToken);
                break;
            case PacketConstants.LcUpdateAccountState:
                await HandleUpdateAccountStateAsync(packet, cancellationToken);
                break;
            case PacketConstants.LcBanAccount:
                await HandleBanAccountAsync(packet, cancellationToken);
                break;
            case PacketConstants.LcChangeSex:
                await HandleChangeSexAsync(packet, cancellationToken);
                break;
            case PacketConstants.LcUnbanAccount:
                await HandleUnbanAccountAsync(packet, cancellationToken);
                break;
            case PacketConstants.LcVipRequest:
                await HandleVipRequestAsync(packet, cancellationToken);
                break;
            case PacketConstants.LcSetAccountOnline:
                HandleSetAccountOnline(packet);
                break;
            case PacketConstants.LcSetAccountOffline:
                HandleSetAccountOffline(packet);
                break;
            case PacketConstants.LcOnlineList:
                await HandleOnlineListAsync(packet, cancellationToken);
                break;
            case PacketConstants.LcAccountInfoRequest:
                await HandleAccountInfoRequestAsync(packet, cancellationToken);
                break;
            case PacketConstants.LcAccountReg2Update:
                HandleAccountReg2Update(packet);
                break;
            case PacketConstants.LcGlobalAccRegRequest:
                await HandleGlobalAccRegRequestAsync(packet, cancellationToken);
                break;
            case PacketConstants.LcCharIpUpdate:
                HandleCharIpUpdate(packet);
                break;
            case PacketConstants.LcSetAllOffline:
                HandleSetAllOffline(packet);
                break;
            case PacketConstants.LcPincodeUpdate:
                await HandlePincodeUpdateAsync(packet, cancellationToken);
                break;
            case PacketConstants.LcPincodeAuthFail:
                await HandlePincodeAuthFailAsync(packet, cancellationToken);
                break;
            default:
                break;
        }
    }

    private async Task HandleLoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var remoteIp = (_client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "0.0.0.0";
        var result = await AuthenticateAsync(request, remoteIp, cancellationToken);

        if (!result.Success)
        {
            LoginLogger.Warning($"Login failed (code={result.ErrorCode}).");
            await SendRefuseLoginAsync(result.ErrorCode, result.UnblockTime, cancellationToken);
            if (result.ErrorCode is 0 or 1)
            {
                await ApplyDynamicIpBanAsync(remoteIp, cancellationToken);
            }

            return;
        }

        if (Config.GroupIdToConnect >= 0 && result.GroupId != Config.GroupIdToConnect)
        {
            await SendNotifyBanAsync(1, cancellationToken);
            return;
        }

        if (Config.MinGroupIdToConnect >= 0 && Config.GroupIdToConnect == -1 && result.GroupId < Config.MinGroupIdToConnect)
        {
            await SendNotifyBanAsync(1, cancellationToken);
            return;
        }

        if (_charServers.Servers.Count == 0)
        {
            await SendNotifyBanAsync(1, cancellationToken);
            return;
        }

        if (_state.CheckDuplicateLogin(result.AccountId) == DuplicateLoginCheckResult.AlreadyOnline)
        {
            await SendKickRequestAsync(result.AccountId, cancellationToken);
            _state.ScheduleWaitingDisconnect(result.AccountId);
            await SendNotifyBanAsync(8, cancellationToken);
            return;
        }

        _state.BeginPendingHandoff(new AuthNode
        {
            AccountId = result.AccountId,
            LoginId1 = result.LoginId1,
            LoginId2 = result.LoginId2,
            Sex = result.Sex,
            ClientType = request.ClientType,
            Ip = result.Ip
        });

        var remoteAddress = (_client.Client.RemoteEndPoint as IPEndPoint)?.Address;
        await SendAcceptLoginAsync(result, remoteAddress, cancellationToken);
    }

    /// <summary>
    /// First step of Athena.NET's internal CharServer &lt;-&gt; LoginServer HMAC-SHA256
    /// service handshake (see ai/login-server.md): a CharServer identifies itself
    /// with a non-secret ServiceId and its registration info, and receives a
    /// one-time nonce challenge - cryptographically bound to the complete hello
    /// payload, not just the ServiceId - in return. Only valid from
    /// <see cref="ServiceAuthConnectionState.Unauthenticated"/>: a second hello
    /// while a challenge is outstanding, or one arriving after this connection
    /// already authenticated or failed, is rejected and the connection is
    /// closed rather than silently issuing a fresh challenge. Registration is
    /// not finalized here - only after LcServiceAuthProof verifies
    /// successfully (see <see cref="HandleServiceAuthProofAsync"/>) is this
    /// socket trusted with anything.
    /// </summary>
    private async Task HandleServiceHelloAsync(byte[] packet, CancellationToken cancellationToken)
    {
        var serviceId = ReadFixedString(packet, 2, PacketConstants.NameLength);
        var ip = new IPAddress(packet.AsSpan(2 + PacketConstants.NameLength, 4));
        var port = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2 + PacketConstants.NameLength + 4, 2));
        var name = ReadFixedString(packet, 2 + PacketConstants.NameLength + 4 + 2, PacketConstants.ServerNameLength);
        var maintenanceOffset = 2 + PacketConstants.NameLength + 4 + 2 + PacketConstants.ServerNameLength;
        var maintenance = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(maintenanceOffset, 2));
        var newDisplay = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(maintenanceOffset + 2, 2));

        var hello = new ServiceHelloInfo(serviceId, ip, port, name, maintenance, newDisplay);

        var nonce = _serviceAuth.GenerateChallenge(hello);
        if (nonce == null)
        {
            LoginLogger.Warning($"Rejected LcServiceHello: connection is not in a state that accepts a new challenge (state={_serviceAuth.State}). Closing connection.");
            await SendServiceAuthResultAsync(1, cancellationToken);
            _client.Close();
            return;
        }

        // The handshake timeout starts only once a challenge is actually
        // issued - a real wall-clock bound on "how long may this connection
        // wait before sending LcServiceAuthProof", enforced by cancelling the
        // next read rather than merely checked when a (possibly very late)
        // proof happens to arrive.
        _handshakeCts?.CancelAfter(_serviceAuthTimeout);

        LoginLogger.Info($"Service hello received (serviceId='{serviceId}'), challenge issued.");
        await SendServiceAuthChallengeAsync(nonce, cancellationToken);
    }

    /// <summary>
    /// Second/final step of the handshake: verifies the submitted HMAC-SHA256
    /// proof - recomputed over the complete ServiceHello payload bound when
    /// the challenge was issued - against the outstanding one-time challenge.
    /// Only valid from <see cref="ServiceAuthConnectionState.ChallengeIssued"/>;
    /// a proof with no outstanding challenge (never issued, already consumed,
    /// or arriving after this connection already authenticated) is always
    /// rejected. Registration and authentication happen in that order - the
    /// CharServer is registered in <see cref="CharServerRegistry"/> first, and
    /// <see cref="IServiceAuthenticationService.MarkAuthenticated"/> (which
    /// unlocks every <c>ServiceOnlyPackets</c> handler) is only called if that
    /// registration actually succeeds - so if registration were ever to fail
    /// for any reason, the socket can never become authenticated. Any failure
    /// along this path leaves IsAuthenticated false and closes the connection
    /// (rather than allowing unlimited retries), matching the security
    /// invariant that protected Lc* packets must be unreachable before a
    /// fully successful service login.
    /// </summary>
    private async Task HandleServiceAuthProofAsync(byte[] packet, CancellationToken cancellationToken)
    {
        var proof = packet.AsSpan(2, PacketConstants.ServiceProofLength).ToArray();

        var result = _serviceAuth.VerifyProof(proof);

        // Whether this proof succeeds or fails, the pending-challenge phase
        // is over: disable the handshake timeout so it can never fire against
        // the connection again (a long-lived authenticated CharServer
        // connection must not inherit this timeout; a failing connection is
        // about to be closed here directly).
        _handshakeCts?.CancelAfter(Timeout.InfiniteTimeSpan);

        if (!result.Success || result.Hello == null)
        {
            // Never log the proof or the token - only the classification.
            LoginLogger.Warning($"Service authentication failed (reason={result.Outcome}). Closing connection.");
            await SendServiceAuthResultAsync(1, cancellationToken);
            _client.Close();
            return;
        }

        // Register before authenticating: if registration ever fails (defense
        // in depth - the state machine above already makes this path
        // unreachable twice on the same connection), the socket must never
        // become authenticated.
        if (!TryRegisterCharServer(result.Hello))
        {
            LoginLogger.Warning("CharServer registration failed after a verified proof. Closing connection.");
            await SendServiceAuthResultAsync(1, cancellationToken);
            _client.Close();
            return;
        }

        // Only at this point has the HMAC proof been verified against the
        // one-time challenge and the complete bound ServiceHello payload, AND
        // the CharServer been registered. IServiceAuthenticationService.MarkAuthenticated
        // itself only succeeds immediately after this connection's own
        // successful VerifyProof call (see ServiceAuthConnectionState.ProofVerified),
        // so it cannot be reached from any other state even by a caller bug -
        // ServiceOnlyPackets must never be gated on anything earlier than a
        // true result from this call.
        if (!_serviceAuth.MarkAuthenticated())
        {
            // Unreachable given the flow above, but fail closed rather than
            // silently treating the connection as authenticated regardless.
            LoginLogger.Warning("MarkAuthenticated was rejected unexpectedly after a successful proof verification. Closing connection.");
            await SendServiceAuthResultAsync(1, cancellationToken);
            _client.Close();
            return;
        }

        LoginLogger.Status($"Char server registered (serviceId='{result.Hello.ServiceId}', name='{result.Hello.ServerName}').");
        await SendServiceAuthResultAsync(0, cancellationToken);
    }

    private async Task HandleAuthRequestAsync(byte[] packet, CancellationToken cancellationToken)
    {
        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        var loginId1 = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(6, 4));
        var loginId2 = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(10, 4));
        var sex = packet[14];
        var requestId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(19, 4));

        byte result = 1;
        byte clientType;

        if (_state.TryConsumeAuthNode(accountId, loginId1, loginId2, sex, out clientType))
        {
            result = 0;
        }

        if (result != 0)
        {
            LoginLogger.Warning($"Auth request denied (sex={sex}).");
        }

        var buffer = new byte[21];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.LcAuthResponse);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2, 4), accountId);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(6, 4), loginId1);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(10, 4), loginId2);
        buffer[14] = sex;
        buffer[15] = result;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16, 4), requestId);
        buffer[20] = clientType;

        await _stream.WriteAsync(buffer, cancellationToken);
    }

    private void HandleUserCount(byte[] packet)
    {
        var users = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        if (_charServerId.HasValue && _charServers.TryGet(_charServerId.Value, out var server))
        {
            server.Users = (ushort)Math.Min(ushort.MaxValue, users);
        }
    }

    private async Task HandleAccountDataRequestAsync(byte[] packet, CancellationToken cancellationToken)
    {
        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        var db = _identityDbFactory();
        if (db == null)
        {
            return;
        }

        await using (db)
        {
            var account = await db.GameAccounts
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.RagnarokAccountId == accountId, cancellationToken);

            if (account == null)
            {
                return;
            }

            var email = await GetIdentityEmailAsync(db, account.IdentityUserId, cancellationToken);
            await SendAccountDataAsync(account, email, cancellationToken);
        }
    }

    /// <summary>
    /// Wire-format validation (does this look like an email at all) stays here -
    /// it is rejecting malformed packet data, not an Identity concern. The
    /// actual mutation goes through IPlayerIdentityAccountService/UserManager
    /// (see PlayerIdentityAccountService), never hand-written EF property
    /// assignment, so normalization/validation always match whatever ASP.NET
    /// Core Identity is actually configured to do.
    /// </summary>
    private async Task HandleChangeEmailAsync(byte[] packet, CancellationToken cancellationToken)
    {
        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        var actualEmail = ReadFixedString(packet, 6, 40);
        var newEmail = ReadFixedString(packet, 46, 40);

        if (!IsValidEmail(actualEmail) || !IsValidEmail(newEmail))
        {
            return;
        }

        var result = await _identityAccountService.ChangeEmailAsync(accountId, actualEmail, newEmail, cancellationToken);
        if (!result.Success)
        {
            LoginLogger.Warning($"Email change rejected for account {accountId} ({result.FailureReason}).");
        }
    }

    private async Task HandleUpdateAccountStateAsync(byte[] packet, CancellationToken cancellationToken)
    {
        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        var state = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(6, 4));

        var db = _identityDbFactory();
        if (db == null)
        {
            return;
        }

        await using (db)
        {
            var account = await db.GameAccounts.FirstOrDefaultAsync(a => a.RagnarokAccountId == accountId, cancellationToken);
            if (account == null)
            {
                return;
            }

            account.State = state;
            await db.SaveChangesAsync(cancellationToken);
            await BroadcastAccountStatusAsync(accountId, 0, state, cancellationToken);
        }
    }

    private async Task HandleBanAccountAsync(byte[] packet, CancellationToken cancellationToken)
    {
        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        var timeDiff = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(6, 4));

        if (timeDiff <= 0)
        {
            return;
        }

        var db = _identityDbFactory();
        if (db == null)
        {
            return;
        }

        await using (db)
        {
            var account = await db.GameAccounts.FirstOrDefaultAsync(a => a.RagnarokAccountId == accountId, cancellationToken);
            if (account == null)
            {
                return;
            }

            var now = ToUnixTime(DateTime.UtcNow);
            var baseTime = account.UnbanTime > now ? account.UnbanTime : now;
            var newTime = baseTime + (uint)timeDiff;
            if (newTime <= now)
            {
                return;
            }

            account.UnbanTime = newTime;
            await db.SaveChangesAsync(cancellationToken);
            await BroadcastAccountStatusAsync(accountId, 1, newTime, cancellationToken);
        }
    }

    private async Task HandleChangeSexAsync(byte[] packet, CancellationToken cancellationToken)
    {
        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        var db = _identityDbFactory();
        if (db == null)
        {
            return;
        }

        await using (db)
        {
            var account = await db.GameAccounts.FirstOrDefaultAsync(a => a.RagnarokAccountId == accountId, cancellationToken);
            if (account == null)
            {
                return;
            }

            account.Sex = account.Sex.Equals("M", StringComparison.OrdinalIgnoreCase) ? "F" : "M";
            await db.SaveChangesAsync(cancellationToken);
            await BroadcastSexChangeAsync(accountId, account.Sex, cancellationToken);
        }
    }

    private async Task HandleUnbanAccountAsync(byte[] packet, CancellationToken cancellationToken)
    {
        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        var db = _identityDbFactory();
        if (db == null)
        {
            return;
        }

        await using (db)
        {
            var account = await db.GameAccounts.FirstOrDefaultAsync(a => a.RagnarokAccountId == accountId, cancellationToken);
            if (account == null)
            {
                return;
            }

            if (account.UnbanTime == 0)
            {
                return;
            }

            account.UnbanTime = 0;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task HandleVipRequestAsync(byte[] packet, CancellationToken cancellationToken)
    {
        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        var flag = packet[6];
        var timeDiff = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(7, 4));
        var mapFd = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(11, 4));

        var db = _identityDbFactory();
        if (db == null)
        {
            return;
        }

        await using (db)
        {
            var account = await db.GameAccounts.FirstOrDefaultAsync(a => a.RagnarokAccountId == accountId, cancellationToken);
            if (account == null)
            {
                return;
            }

            var now = ToUnixTime(DateTime.UtcNow);
            var vipTime = account.VipTime;

            if ((flag & 0x2) != 0)
            {
                if (vipTime < now)
                {
                    vipTime = now;
                }

                vipTime = (uint)Math.Max(0, (long)vipTime + timeDiff);
            }

            var isVip = vipTime > now;
            if (isVip)
            {
                if (account.GroupId != Config.VipGroupId)
                {
                    account.OldGroup = account.GroupId;
                    if (account.CharacterSlots == 0)
                    {
                        account.CharacterSlots = (byte)Config.CharPerAccount;
                    }

                    account.CharacterSlots = (byte)Math.Min(byte.MaxValue, account.CharacterSlots + Config.VipCharIncrease);
                }

                account.GroupId = Config.VipGroupId;
            }
            else
            {
                vipTime = 0;
                if (account.GroupId == Config.VipGroupId)
                {
                    account.GroupId = account.OldGroup;
                    if (account.CharacterSlots == 0)
                    {
                        account.CharacterSlots = (byte)Config.CharPerAccount;
                    }
                    else
                    {
                        account.CharacterSlots = (byte)Math.Max(0, account.CharacterSlots - Config.VipCharIncrease);
                    }
                }

                account.OldGroup = 0;
            }

            account.VipTime = vipTime;
            await db.SaveChangesAsync(cancellationToken);

            if ((flag & 0x1) != 0)
            {
                var responseFlag = (byte)((isVip ? 0x1 : 0x0) | ((flag & 0x8) != 0 ? 0x4 : 0x0));
                await SendVipDataAsync(account, responseFlag, mapFd, cancellationToken);
            }

            var email = await GetIdentityEmailAsync(db, account.IdentityUserId, cancellationToken);
            await SendAccountDataAsync(account, email, cancellationToken);
        }
    }

    private void HandleSetAccountOnline(byte[] packet)
    {
        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        if (_charServerId.HasValue)
        {
            _state.AddOnlineUser(_charServerId.Value, accountId);
        }
    }

    private void HandleSetAccountOffline(byte[] packet)
    {
        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        _state.RemoveOnlineUser(accountId);
        _ = ScheduleDisableWebAuthTokenAsync(accountId);
    }

    private async Task HandleOnlineListAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (_charServerId == null)
        {
            return;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2, 2));
        var count = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4, 4));

        var totalLength = length;
        if (totalLength < 8)
        {
            return;
        }

        var remaining = totalLength - 8;
        var expected = (int)count * 4;
        if (remaining < expected)
        {
            var extra = await ReadExactAsync(expected - remaining, cancellationToken);
            if (extra.Length == 0)
            {
                return;
            }

            var combined = new byte[totalLength + extra.Length];
            Buffer.BlockCopy(packet, 0, combined, 0, packet.Length);
            Buffer.BlockCopy(extra, 0, combined, packet.Length, extra.Length);
            packet = combined;
            remaining += extra.Length;
        }

        var offset = 8;
        for (var i = 0; i < count; i++)
        {
            if (offset + 4 > packet.Length)
            {
                break;
            }

            var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(offset, 4));
            _state.AddOnlineUser(_charServerId.Value, accountId);
            offset += 4;
        }
    }

    private async Task HandleAccountInfoRequestAsync(byte[] packet, CancellationToken cancellationToken)
    {
        var mapFd = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        var userFd = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(6, 4));
        var userAid = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(10, 4));
        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(14, 4));

        var db = _identityDbFactory();
        if (db == null)
        {
            return;
        }

        await using (db)
        {
            var account = await db.GameAccounts
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.RagnarokAccountId == accountId, cancellationToken);

            if (account == null)
            {
                await SendAccountInfoResponseAsync(mapFd, userFd, userAid, accountId, null, string.Empty, string.Empty, cancellationToken);
                return;
            }

            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == account.IdentityUserId, cancellationToken);
            await SendAccountInfoResponseAsync(mapFd, userFd, userAid, accountId, account, user?.Email ?? string.Empty, user?.UserName ?? string.Empty, cancellationToken);
        }
    }

    private void HandleAccountReg2Update(byte[] packet)
    {
        if (packet.Length < 8)
        {
            return;
        }

        var db = _dbFactory();
        if (db == null)
        {
            return;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2, 2));
        if (length > packet.Length)
        {
            length = (ushort)packet.Length;
        }

        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4, 4));
        var offset = 8;

        _ = Task.Run(async () =>
        {
            await using (db)
            {
                while (offset < length)
                {
                    if (offset + 1 > length)
                    {
                        break;
                    }

                    var keyLength = packet[offset];
                    offset += 1;
                    if (keyLength == 0 || offset + keyLength > length)
                    {
                        break;
                    }

                    var key = Encoding.ASCII.GetString(packet, offset, keyLength);
                    offset += keyLength;

                    if (offset + 4 > length)
                    {
                        break;
                    }

                    var index = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(offset, 4));
                    offset += 4;

                    if (offset + 1 > length)
                    {
                        break;
                    }

                    var type = packet[offset];
                    offset += 1;

                    if (type == 0 || type == 1)
                    {
                        if (offset + 8 > length)
                        {
                            break;
                        }

                        var value = BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(offset, 8));
                        offset += 8;

                        await UpsertAccountRegNumAsync(db, accountId, key, index, value);
                    }
                    else
                    {
                        if (offset + 1 > length)
                        {
                            break;
                        }

                        var valueLength = packet[offset];
                        offset += 1;
                        if (offset + valueLength > length)
                        {
                            break;
                        }

                        var value = Encoding.ASCII.GetString(packet, offset, valueLength);
                        offset += valueLength;

                        await UpsertAccountRegStrAsync(db, accountId, key, index, value);
                    }
                }

                await db.SaveChangesAsync();
            }
        });
    }

    private async Task HandleGlobalAccRegRequestAsync(byte[] packet, CancellationToken cancellationToken)
    {
        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        var charId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(6, 4));
        var db = _dbFactory();
        if (db == null)
        {
            return;
        }

        await using (db)
        {
            var regStr = await db.AccountRegStrs.Where(r => r.AccountId == accountId).ToListAsync(cancellationToken);
            await SendGlobalAccRegAsync(accountId, charId, true, regStr, false, cancellationToken);

            var regNum = await db.AccountRegNums.Where(r => r.AccountId == accountId).ToListAsync(cancellationToken);
            await SendGlobalAccRegAsync(accountId, charId, false, regNum, true, cancellationToken);
        }
    }

    private void HandleCharIpUpdate(byte[] packet)
    {
        if (_charServerId == null)
        {
            return;
        }

        if (_charServers.TryGet(_charServerId.Value, out var server))
        {
            var ip = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(2, 4));
            server.Ip = new IPAddress(ip);
        }
    }

    private void HandleSetAllOffline(byte[] packet)
    {
        if (_charServerId == null)
        {
            return;
        }

        _state.MarkOnlineUsersUnknownByCharServer(_charServerId.Value);
    }

    private async Task HandlePincodeUpdateAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (packet.Length < 13)
        {
            return;
        }

        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4, 4));
        var pin = ReadFixedString(packet, 8, 5);
        var db = _identityDbFactory();
        if (db == null)
        {
            return;
        }

        await using (db)
        {
            var account = await db.GameAccounts.FirstOrDefaultAsync(a => a.RagnarokAccountId == accountId, cancellationToken);
            if (account == null)
            {
                return;
            }

            account.Pincode = pin;
            account.PincodeChange = ToUnixTime(DateTime.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task HandlePincodeAuthFailAsync(byte[] packet, CancellationToken cancellationToken)
    {
        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        var identityDb = _identityDbFactory();
        if (identityDb == null)
        {
            return;
        }

        string userName;
        string lastIp;
        await using (identityDb)
        {
            var account = await identityDb.GameAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.RagnarokAccountId == accountId, cancellationToken);
            if (account == null)
            {
                return;
            }

            var user = await identityDb.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == account.IdentityUserId, cancellationToken);
            userName = user?.UserName ?? string.Empty;
            lastIp = account.LastIp;
        }

        var db = _dbFactory();
        if (db == null)
        {
            return;
        }

        await using (db)
        {
            await LogLoginAsync(db, userName, lastIp, 100, "PIN Code check failed", cancellationToken);
        }

        _state.RemoveOnlineUser(accountId);
    }

    private static async Task UpsertAccountRegNumAsync(LoginDbContext db, uint accountId, string key, uint index, long value)
    {
        var existing = await db.AccountRegNums.FindAsync(accountId, key, index);
        if (existing == null)
        {
            db.AccountRegNums.Add(new AccountRegNum
            {
                AccountId = accountId,
                Key = key,
                Index = index,
                Value = value,
            });
        }
        else
        {
            existing.Value = value;
            db.Entry(existing).Property(e => e.Value).IsModified = true;
        }
    }

    private static async Task UpsertAccountRegStrAsync(LoginDbContext db, uint accountId, string key, uint index, string value)
    {
        var existing = await db.AccountRegStrs.FindAsync(accountId, key, index);
        if (existing == null)
        {
            db.AccountRegStrs.Add(new AccountRegStr
            {
                AccountId = accountId,
                Key = key,
                Index = index,
                Value = value,
            });
        }
        else
        {
            existing.Value = value;
            db.Entry(existing).Property(e => e.Value).IsModified = true;
        }
    }

    private async Task<AuthResult> AuthenticateAsync(LoginRequest request, string remoteIp, CancellationToken cancellationToken)
    {
        var db = _dbFactory();
        if (db == null)
        {
            return AuthResult.Fail(1);
        }

        await using (db)
        {
            return await AuthenticatePlayerAsync(db, request, remoteIp, cancellationToken);
        }
    }

    /// <summary>
    /// Player login through ASP.NET Core Identity (via <see cref="_playerAuth"/>).
    /// IP ban/DNSBL checks and login audit logging stay here since they are
    /// connection/audit concerns, not player-identity concerns. The legacy
    /// "login as name_M/name_F to auto-create an account" convenience is not
    /// reimplemented: it wrote directly into the legacy login table, which player
    /// login no longer reads. Account creation now goes exclusively through
    /// IPlayerAccountProvisioningService.
    /// </summary>
    private async Task<AuthResult> AuthenticatePlayerAsync(LoginDbContext db, LoginRequest request, string remoteIp, CancellationToken cancellationToken)
    {
        if (Config.IpBanEnabled && await IsIpBannedAsync(db, remoteIp, cancellationToken))
        {
            return AuthResult.Fail(3);
        }

        if (Config.UseDnsbl && Config.DnsblServers.Length > 0 && await IsDnsblListedAsync(remoteIp, cancellationToken))
        {
            await LogLoginAsync(db, request.UserId, remoteIp, 3, string.Empty, cancellationToken);
            return AuthResult.Fail(3);
        }

        var userId = request.UserId;
        var playerResult = await _playerAuth.AuthenticateAsync(userId, request.Password, remoteIp, cancellationToken);
        if (!playerResult.Success)
        {
            var errorCode = PlayerAuthenticationErrorCodeMapper.ToErrorCode(playerResult);
            await LogLoginAsync(db, userId, remoteIp, errorCode, string.Empty, cancellationToken);
            var unblockTime = playerResult.UnblockAtLocal.HasValue ? FormatDate(playerResult.UnblockAtLocal.Value) : string.Empty;
            return AuthResult.Fail(errorCode, unblockTime);
        }

        await LogLoginAsync(db, userId, remoteIp, 100, "login ok", cancellationToken);

        var (loginId1, loginId2) = _state.GenerateLoginIds();
        return AuthResult.FromGameAccount(playerResult.Account!, loginId1, loginId2, remoteIp);
    }

    /// <summary>
    /// Finalizes CharServer registration once LcServiceAuthProof has verified
    /// successfully, called before <see cref="IServiceAuthenticationService.MarkAuthenticated"/>
    /// so that a registration failure can never leave the socket authenticated
    /// (see <see cref="HandleServiceAuthProofAsync"/>). The int registry key is
    /// Athena.NET's own internal bookkeeping id (assigned by
    /// <see cref="CharServerRegistry.NextId"/>) - it carries no meaning outside
    /// this process and is never derived from any account/credential concept.
    /// <para>
    /// Defense in depth against a stale registry leak: the service-auth state
    /// machine already guarantees at most one successful proof per
    /// connection (a second LcServiceAuthProof after authentication is
    /// rejected before this method could ever be reached again), so
    /// <see cref="_charServerId"/> should never already be set here - but if
    /// it somehow were, registering a second entry and only ever unregistering
    /// the latest one on <see cref="Dispose"/> would leak the first
    /// registration forever. Guard against that explicitly (returning
    /// <c>false</c>, which the caller treats as a fatal registration failure)
    /// rather than relying solely on the caller-side invariant.
    /// </para>
    /// </summary>
    private bool TryRegisterCharServer(ServiceHelloInfo hello)
    {
        if (_charServerId.HasValue)
        {
            LoginLogger.Warning("RegisterCharServer called more than once on the same connection; rejecting the redundant registration.");
            return false;
        }

        var info = new CharServerInfo
        {
            Name = hello.ServerName,
            Ip = hello.Ip,
            Port = hello.Port,
            Type = hello.CharMaintenance,
            IsNew = hello.CharNewDisplay,
            Users = 0,
            Connection = new CharServerConnection(_stream),
        };

        _charServerId = _charServers.NextId();
        _charServers.Register(_charServerId.Value, info);
        LoginLogger.Status($"Registered char server '{info.Name}' at {info.Ip}:{info.Port} (type={info.Type}, new={info.IsNew}).");
        return true;
    }

    private async Task LogLoginAsync(LoginDbContext db, string userId, string ip, uint resultCode, string message, CancellationToken cancellationToken)
    {
        if (!Config.LogLogin)
        {
            return;
        }

        var entry = new LoginLogEntry
        {
            Time = DateTime.Now,
            Ip = ip,
            User = userId,
            ResultCode = (byte)Math.Min(byte.MaxValue, resultCode),
            Log = ResolveLoginMessage(resultCode, message),
        };
        var tableName = db.Model.FindEntityType(typeof(LoginLogEntry))?.GetTableName() ?? "loginlog";
        var quoted = QuoteTableName(tableName);
        var sql = $"INSERT INTO {quoted} ([time],[ip],[user],[rcode],[log]) VALUES (@p0,@p1,@p2,@p3,@p4)";
        await db.Database.ExecuteSqlRawAsync(sql, new object[]
        {
            entry.Time,
            entry.Ip,
            entry.User,
            entry.ResultCode,
            entry.Log,
        }, cancellationToken);
    }

    private static string QuoteTableName(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName))
        {
            return "[loginlog]";
        }

        var parts = tableName.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return "[loginlog]";
        }

        return string.Join(".", parts.Select(part => $"[{part.Replace("]", "]]", StringComparison.Ordinal)}]"));
    }

    private string ResolveLoginMessage(uint resultCode, string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            return message;
        }

        if (resultCode is >= 99 and <= 104)
        {
            var remapped = resultCode - 83;
            if (_messageStore.Current.TryGet(remapped, out var mapped))
            {
                return mapped;
            }
        }

        if (_messageStore.Current.TryGet(resultCode, out var resolved))
        {
            return resolved;
        }

        if (_messageStore.Current.TryGet(22, out var fallback))
        {
            return fallback;
        }

        return "Unknown Error";
    }

    private async Task<bool> IsIpBannedAsync(LoginDbContext db, string ip, CancellationToken cancellationToken)
    {
        if (!TryParseIpv4(ip, out var octets))
        {
            return false;
        }

        var now = DateTime.Now;
        var p1 = $"{octets[0]}.*.*.*";
        var p2 = $"{octets[0]}.{octets[1]}.*.*";
        var p3 = $"{octets[0]}.{octets[1]}.{octets[2]}.*";
        var p4 = $"{octets[0]}.{octets[1]}.{octets[2]}.{octets[3]}";

        return await db.IpBanList.AnyAsync(entry =>
            entry.ReleaseTime > now &&
            (entry.List == p1 || entry.List == p2 || entry.List == p3 || entry.List == p4),
            cancellationToken);
    }

    private async Task ApplyDynamicIpBanAsync(string ip, CancellationToken cancellationToken)
    {
        if (!Config.IpBanEnabled || !Config.DynamicPassFailureBan)
        {
            return;
        }

        if (!TryParseIpv4(ip, out var octets))
        {
            return;
        }

        var db = _dbFactory();
        if (db == null)
        {
            return;
        }

        await using (db)
        {
            var since = DateTime.Now.AddMinutes(-Config.DynamicPassFailureBanIntervalMinutes);
            var failures = await db.LoginLogs.CountAsync(entry =>
                entry.Ip == ip &&
                (entry.ResultCode == 0 || entry.ResultCode == 1) &&
                entry.Time > since, cancellationToken);

            if (failures < Config.DynamicPassFailureBanLimit)
            {
                return;
            }

            var list = $"{octets[0]}.{octets[1]}.{octets[2]}.*";
            var ban = new IpBanEntry
            {
                List = list,
                BanTime = DateTime.Now,
                ReleaseTime = DateTime.Now.AddMinutes(Config.DynamicPassFailureBanDurationMinutes),
                Reason = "Password error ban",
            };

            await db.IpBanList.AddAsync(ban, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            LoginLogger.Info($"IPBan added ({Config.DynamicPassFailureBanDurationMinutes}m).");
        }
    }

    private async Task SendRefuseLoginAsync(uint error, string unblockTime, CancellationToken cancellationToken)
    {
        var buffer = new byte[2 + 4 + 20];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.AcRefuseLogin);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2, 4), error);

        if (!string.IsNullOrEmpty(unblockTime))
        {
            var bytes = Encoding.ASCII.GetBytes(unblockTime);
            Buffer.BlockCopy(bytes, 0, buffer, 6, Math.Min(20, bytes.Length));
        }

        await _stream.WriteAsync(buffer, cancellationToken);
    }

    private async Task SendNotifyBanAsync(byte result, CancellationToken cancellationToken)
    {
        var buffer = new byte[3];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.ScNotifyBan);
        buffer[2] = result;
        await _stream.WriteAsync(buffer, cancellationToken);
    }

    private async Task SendServiceAuthChallengeAsync(byte[] nonce, CancellationToken cancellationToken)
    {
        var buffer = new byte[2 + PacketConstants.ServiceNonceLength];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.LcServiceAuthChallenge);
        Buffer.BlockCopy(nonce, 0, buffer, 2, nonce.Length);
        await _stream.WriteAsync(buffer, cancellationToken);
    }

    private async Task SendServiceAuthResultAsync(byte result, CancellationToken cancellationToken)
    {
        var buffer = new byte[3];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.LcServiceAuthResult);
        buffer[2] = result;
        await _stream.WriteAsync(buffer, cancellationToken);
    }

    private async Task SendKeepAliveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.LcKeepAliveResponse);
        await _stream.WriteAsync(buffer, cancellationToken);
    }

    private static async Task<string> GetIdentityEmailAsync(Db.Identity.AthenaIdentityDbContext db, Guid identityUserId, CancellationToken cancellationToken)
    {
        return await db.Users
            .AsNoTracking()
            .Where(u => u.Id == identityUserId)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
    }

    private async Task SendVipDataAsync(Db.Identity.AthenaGameAccount account, byte flag, uint mapFd, CancellationToken cancellationToken)
    {
        var buffer = new byte[19];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.LcVipResponse);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2, 4), account.RagnarokAccountId);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(6, 4), account.VipTime);
        buffer[10] = flag;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(11, 4), (uint)account.GroupId);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(15, 4), mapFd);
        await _stream.WriteAsync(buffer, cancellationToken);
    }

    private async Task SendGlobalAccRegAsync<T>(uint accountId, uint charId, bool isString, List<T> items, bool isLastBatch, CancellationToken cancellationToken)
    {
        var maxPayload = 60000;
        var buffer = new byte[60000 + 300];
        var offset = 16;
        var count = 0;

        async Task FlushAsync(bool last)
        {
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.LcGlobalAccRegResponse);
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(2, 2), (short)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), accountId);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8, 4), charId);
            buffer[12] = last ? (byte)1 : (byte)0;
            buffer[13] = isString ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14, 2), (ushort)count);

            await _stream.WriteAsync(buffer.AsMemory(0, offset), cancellationToken);
            offset = 16;
            count = 0;
        }

        foreach (var item in items)
        {
            if (isString && item is AccountRegStr str)
            {
                var keyBytes = Encoding.ASCII.GetBytes(str.Key);
                var valBytes = Encoding.ASCII.GetBytes(str.Value);
                var entrySize = 1 + keyBytes.Length + 1 + 4 + 1 + valBytes.Length + 1;
                if (offset + entrySize > maxPayload)
                {
                    await FlushAsync(false);
                }

                buffer[offset++] = (byte)(keyBytes.Length + 1);
                Buffer.BlockCopy(keyBytes, 0, buffer, offset, keyBytes.Length);
                offset += keyBytes.Length;
                buffer[offset++] = 0;

                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), str.Index);
                offset += 4;

                buffer[offset++] = (byte)(valBytes.Length + 1);
                Buffer.BlockCopy(valBytes, 0, buffer, offset, valBytes.Length);
                offset += valBytes.Length;
                buffer[offset++] = 0;

                count++;
            }
            else if (!isString && item is AccountRegNum num)
            {
                var keyBytes = Encoding.ASCII.GetBytes(num.Key);
                var entrySize = 1 + keyBytes.Length + 1 + 4 + 8;
                if (offset + entrySize > maxPayload)
                {
                    await FlushAsync(false);
                }

                buffer[offset++] = (byte)(keyBytes.Length + 1);
                Buffer.BlockCopy(keyBytes, 0, buffer, offset, keyBytes.Length);
                offset += keyBytes.Length;
                buffer[offset++] = 0;

                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), num.Index);
                offset += 4;

                BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset, 8), num.Value);
                offset += 8;

                count++;
            }
        }

        await FlushAsync(isLastBatch);
    }

    private async Task BroadcastAccountStatusAsync(uint accountId, byte type, uint value, CancellationToken cancellationToken)
    {
        var buffer = new byte[11];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.LcAccountStatusNotify);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2, 4), accountId);
        buffer[6] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(7, 4), value);
        await _charServers.SendToAllExceptAsync(_charServerId, buffer);
    }

    private async Task BroadcastSexChangeAsync(uint accountId, string sex, CancellationToken cancellationToken)
    {
        var buffer = new byte[7];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.LcAccountSexNotify);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2, 4), accountId);
        buffer[6] = sex.Equals("F", StringComparison.OrdinalIgnoreCase) ? (byte)0 : (byte)1;
        await _charServers.SendToAllExceptAsync(_charServerId, buffer);
    }

    private async Task SendAccountInfoResponseAsync(uint mapFd, uint userFd, uint userAid, uint accountId, Db.Identity.AthenaGameAccount? account, string email, string userName, CancellationToken cancellationToken)
    {
        if (account == null)
        {
            var buffer = new byte[19];
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.LcAccountInfoResponse);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2, 4), mapFd);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(6, 4), userFd);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(10, 4), userAid);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(14, 4), accountId);
            buffer[18] = 0;
            await _stream.WriteAsync(buffer, cancellationToken);
            return;
        }

        var bufferSuccess = new byte[146];
        BinaryPrimitives.WriteInt16LittleEndian(bufferSuccess.AsSpan(0, 2), PacketConstants.LcAccountInfoResponse);
        BinaryPrimitives.WriteUInt32LittleEndian(bufferSuccess.AsSpan(2, 4), mapFd);
        BinaryPrimitives.WriteUInt32LittleEndian(bufferSuccess.AsSpan(6, 4), userFd);
        BinaryPrimitives.WriteUInt32LittleEndian(bufferSuccess.AsSpan(10, 4), userAid);
        BinaryPrimitives.WriteUInt32LittleEndian(bufferSuccess.AsSpan(14, 4), accountId);
        bufferSuccess[18] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(bufferSuccess.AsSpan(19, 4), (uint)account.GroupId);
        BinaryPrimitives.WriteUInt32LittleEndian(bufferSuccess.AsSpan(23, 4), (uint)Math.Max(0, account.LoginCount));
        BinaryPrimitives.WriteUInt32LittleEndian(bufferSuccess.AsSpan(27, 4), account.State);
        WriteFixedString(bufferSuccess, 31, 40, email);
        WriteFixedString(bufferSuccess, 71, 16, account.LastIp);
        WriteFixedString(bufferSuccess, 87, 24, account.LastLogin?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty);
        WriteFixedString(bufferSuccess, 111, 11, account.Birthdate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty);
        WriteFixedString(bufferSuccess, 122, PacketConstants.NameLength, userName);

        await _stream.WriteAsync(bufferSuccess, cancellationToken);
    }

    private async Task SendAccountDataAsync(Db.Identity.AthenaGameAccount account, string email, CancellationToken cancellationToken)
    {
        var buffer = new byte[75];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.LcAccountDataResponse);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2, 4), account.RagnarokAccountId);

        WriteFixedString(buffer, 6, 40, email);

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(46, 4), account.ExpirationTime);
        buffer[50] = (byte)Math.Clamp(account.GroupId, 0, byte.MaxValue);

        var charSlots = Config.CharPerAccount;
        if (account.CharacterSlots > 0)
        {
            charSlots = account.CharacterSlots;
        }

        var isVip = false;
        var vipIncrease = Config.VipCharIncrease;
        if (account.VipTime > 0 && account.VipTime > ToUnixTime(DateTime.UtcNow))
        {
            isVip = true;
            charSlots += vipIncrease;
        }

        buffer[51] = (byte)Math.Clamp(charSlots, 0, byte.MaxValue);

        if (account.Birthdate.HasValue)
        {
            var birthdate = account.Birthdate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            WriteFixedString(buffer, 52, 11, birthdate);
        }

        WriteFixedString(buffer, 63, 5, account.Pincode);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(68, 4), account.PincodeChange);

        buffer[72] = isVip ? (byte)1 : (byte)0;
        buffer[73] = (byte)Math.Clamp(vipIncrease, 0, byte.MaxValue);
        buffer[74] = (byte)Math.Clamp(Config.MaxCharBilling, 0, byte.MaxValue);

        await _stream.WriteAsync(buffer, cancellationToken);
    }

    private async Task SendAcceptLoginAsync(AuthResult result, IPAddress? clientAddress, CancellationToken cancellationToken)
    {
        var servers = _charServers.Servers;
        var serverCount = servers.Count;
        var serverEntrySize = 32;
        var length = 2 + 2 + 4 + 4 + 4 + 4 + 26 + 1 + PacketConstants.WebAuthTokenLength + serverCount * serverEntrySize;
        var buffer = new byte[length];

        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.AcAcceptLogin);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(2, 2), (short)length);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), result.LoginId1);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8, 4), result.AccountId);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12, 4), result.LoginId2);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16, 4), 0);
        buffer[46] = result.Sex;

        if (Config.UseWebAuthToken && !string.IsNullOrEmpty(result.WebAuthToken))
        {
            var tokenBytes = Encoding.ASCII.GetBytes(result.WebAuthToken);
            Buffer.BlockCopy(tokenBytes, 0, buffer, 47, Math.Min(PacketConstants.WebAuthTokenLength, tokenBytes.Length));
        }

        var offset = 47 + PacketConstants.WebAuthTokenLength;
        foreach (var server in servers)
        {
            var ip = Config.IroAdvertisedCharIp;

            LoginLogger.Info($"[iRO DEBUG] Server entry: name='{server.Name}' ip={ip} port={Config.IroAdvertisedCharPort} users={server.Users} type={server.Type} new={server.IsNew}");

            var ipBytes = ip.GetAddressBytes();
            Buffer.BlockCopy(ipBytes, 0, buffer, offset, 4);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 4, 2), (ushort)Config.IroAdvertisedCharPort);
            WriteFixedString(buffer, offset + 6, 20, "Chaos");
            // Athena.NET/current-iRO compatibility choice, deliberately NOT pinned rAthena behavior:
            // the current iRO client's server-select screen is expected to show the LITERAL online
            // player count (0/1/2/.../10/...), not a population-level category. server.Users is
            // already the real aggregated count MapServer -> CharServer -> LoginServer reports (see
            // ai/char-server.md's "Online player count" section) - it goes straight onto the wire
            // here, unchanged, never through MapUserCount. MapUserCount itself is left in place,
            // reproducing pinned rAthena's own login_get_usercount exactly (see its own doc comment
            // and ClientSessionWireCharacterizationTests' parity tests) - retained as a characterized,
            // tested helper for any future non-iRO/legacy-compatible path, just no longer wired into
            // this one.
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 26, 2), server.Users);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 28, 2), server.Type);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 30, 2), server.IsNew);
            offset += serverEntrySize;
        }

        LoginLogger.Info($"[iRO DEBUG] AC_ACCEPT_LOGIN packet=0x{PacketConstants.AcAcceptLogin:X4} len={buffer.Length} servers={serverCount}");

        await _stream.WriteAsync(buffer, cancellationToken);
    }

    private async Task SendKickRequestAsync(uint accountId, CancellationToken cancellationToken)
    {
        var buffer = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.LcKickRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2, 4), accountId);
        await _charServers.SendToAllAsync(buffer);
    }

    private IPAddress ResolveServerIp(IPAddress serverIp, IPAddress? clientAddress)
    {
        if (clientAddress == null)
        {
            return serverIp;
        }

        return _subnetConfig.TryGetCharIp(clientAddress, out var charIp) ? charIp : serverIp;
    }

    private ushort MapUserCount(int users)
    {
        if (Config.UsercountDisable)
        {
            return 4;
        }

        if (users <= Config.UsercountLow)
        {
            return 0;
        }

        if (users <= Config.UsercountMedium)
        {
            return 1;
        }

        if (users <= Config.UsercountHigh)
        {
            return 2;
        }

        return 3;
    }

    private LoginRequest ParsePlainLogin(byte[] packet)
    {
        var user = ReadFixedString(packet, 6, PacketConstants.NameLength);
        var pass = ReadFixedString(packet, 6 + PacketConstants.NameLength, PacketConstants.NameLength);
        var clientType = packet[^1];
        return new LoginRequest(user, pass, clientType);
    }

    private LoginRequest ParseSsoLogin(byte[] packet)
    {
        var user = ReadFixedString(packet, 9, PacketConstants.NameLength);
        var tokenOffset = 9 + PacketConstants.NameLength + 27 + 17 + 15;
        var tokenLength = packet.Length - tokenOffset;
        var token = tokenLength > 0 ? Encoding.ASCII.GetString(packet, tokenOffset, tokenLength).TrimEnd('\0') : string.Empty;
        var clientType = packet[8];
        return new LoginRequest(user, token, clientType);
    }

    private static string ReadFixedString(byte[] buffer, int offset, int length)
    {
        var span = buffer.AsSpan(offset, length);

        var nullIndex = span.IndexOf((byte)0);

        if (nullIndex >= 0)
        {
            span = span[..nullIndex];
        }

        return Encoding.ASCII.GetString(span);
    }

    private static void WriteFixedString(byte[] buffer, int offset, int length, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        Buffer.BlockCopy(bytes, 0, buffer, offset, Math.Min(length, bytes.Length));
    }

    private static uint ToUnixTime(DateTime time)
    {
        return (uint)new DateTimeOffset(time).ToUnixTimeSeconds();
    }

    private static DateTime FromUnixTime(uint value)
    {
        return DateTimeOffset.FromUnixTimeSeconds(value).LocalDateTime;
    }

    private string FormatDate(DateTime time)
    {
        var format = ConvertDateFormat(Config.DateFormat);
        return time.ToString(format, CultureInfo.InvariantCulture);
    }

    private static string ConvertDateFormat(string format)
    {
        return format
            .Replace("%Y", "yyyy", StringComparison.Ordinal)
            .Replace("%m", "MM", StringComparison.Ordinal)
            .Replace("%d", "dd", StringComparison.Ordinal)
            .Replace("%H", "HH", StringComparison.Ordinal)
            .Replace("%M", "mm", StringComparison.Ordinal)
            .Replace("%S", "ss", StringComparison.Ordinal);
    }

    private static bool IsValidEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        var at = email.IndexOf('@');
        if (at <= 0 || at >= email.Length - 3)
        {
            return false;
        }

        var dot = email.IndexOf('.', at + 1);
        return dot > at + 1 && dot < email.Length - 1 && !email.Equals("a@a.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseIpv4(string ip, out byte[] octets)
    {
        octets = Array.Empty<byte>();
        if (!IPAddress.TryParse(ip, out var address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return false;
        }

        octets = bytes;
        return true;
    }

    private async Task<bool> IsDnsblListedAsync(string ip, CancellationToken cancellationToken)
    {
        if (!TryParseIpv4(ip, out var octets))
        {
            return false;
        }

        var reversed = $"{octets[3]}.{octets[2]}.{octets[1]}.{octets[0]}";
        var servers = Config.DnsblServers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var server in servers)
        {
            var query = $"{reversed}.{server}";
            try
            {
                var entry = await Dns.GetHostEntryAsync(query);
                if (entry.AddressList.Length > 0)
                {
                    LoginLogger.Warning("DNSBL match detected.");
                    return true;
                }
            }
            catch (Exception)
            {
                // Treat lookup errors as not listed.
            }
        }

        return false;
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

    private readonly record struct LoginRequest(string UserId, string Password, byte ClientType);

    private readonly record struct AuthResult(
        bool Success,
        uint ErrorCode,
        string UnblockTime,
        uint AccountId,
        uint LoginId1,
        uint LoginId2,
        byte Sex,
        int GroupId,
        string WebAuthToken,
        uint Ip)
    {
        public static AuthResult Fail(uint error, string unblockTime = "")
        {
            return new AuthResult(false, error, unblockTime, 0, 0, 0, 0, 0, string.Empty, 0);
        }

        public static AuthResult FromGameAccount(AuthenticatedGameAccount account, uint loginId1, uint loginId2, string ip)
        {
            var sex = MapSex(account.Sex);
            var parsedIp = ParseIp(ip);
            return new AuthResult(true, 0, string.Empty, account.RagnarokAccountId, loginId1, loginId2, sex, account.GroupId, account.WebAuthToken, parsedIp);
        }

        private static byte MapSex(string sex) =>
            (byte)(sex.Equals("F", StringComparison.OrdinalIgnoreCase) ? 0 :
                sex.Equals("M", StringComparison.OrdinalIgnoreCase) ? 1 : 2);
    }

    private static uint ParseIp(string ip)
    {
        if (!IPAddress.TryParse(ip, out var address))
        {
            return 0;
        }

        var bytes = address.GetAddressBytes();
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    private async Task ScheduleDisableWebAuthTokenAsync(uint accountId)
    {
        if (!Config.UseWebAuthToken)
        {
            return;
        }

        var delay = Config.DisableWebTokenDelayMs;
        if (delay > 0)
        {
            await Task.Delay(delay);
        }

        if (_state.TryGetOnlineUser(accountId, out _))
        {
            return;
        }

        await DisableWebAuthTokenAsync(accountId);
    }

    /// <summary>
    /// Player WebAuthToken state lives on AthenaGameAccount (Identity schema), not
    /// the legacy LoginDbContext.Accounts table service accounts use - service
    /// accounts never have web auth tokens. Looked up by RagnarokAccountId, the
    /// only account identifier this legacy wire-protocol path (LcSetAccountOffline)
    /// ever carries.
    /// </summary>
    private async Task DisableWebAuthTokenAsync(uint accountId)
    {
        var db = _identityDbFactory();
        if (db == null)
        {
            return;
        }

        await using (db)
        {
            var account = await db.GameAccounts.FirstOrDefaultAsync(a => a.RagnarokAccountId == accountId);
            if (account == null)
            {
                return;
            }

            account.WebAuthTokenEnabled = false;
            await db.SaveChangesAsync();
        }
    }
}
