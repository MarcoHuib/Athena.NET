using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Db;
using Athena.Net.LoginServer.Db.Entities;
using Athena.Net.LoginServer.Logging;

namespace Athena.Net.LoginServer.Net;

public sealed class ClientSession : IDisposable
{
    private const int PasswordEncMode = 3;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly LoginConfigStore _configStore;
    private readonly LoginMessageStore _messageStore;
    private readonly Func<LoginDbContext?> _dbFactory;
    private readonly CharServerRegistry _charServers;
    private readonly LoginState _state;
    private readonly Config.SubnetConfig _subnetConfig;
    private byte[]? _md5Key;
    private byte[]? _clientHash;
    private int? _charServerId;
    private LoginConfig Config => _configStore.Current;
    private bool IsCaseSensitive => _configStore.LoginCaseSensitive;

    private static readonly Dictionary<short, int> PacketLengths = new()
    {
        [PacketConstants.CaLogin] = 2 + 4 + PacketConstants.NameLength + PacketConstants.NameLength + 1,
        [PacketConstants.CaLogin2] = 2 + 4 + PacketConstants.NameLength + 16 + 1,
        [PacketConstants.CaLogin3] = 2 + 4 + PacketConstants.NameLength + 16 + 1 + 1,
        [PacketConstants.CaConnectInfoChanged] = 2 + PacketConstants.NameLength,
        [PacketConstants.CaExeHashCheck] = 2 + 16,
        [PacketConstants.CaReqHash] = 2,
        [PacketConstants.CaLoginPcBang] = 2 + 4 + PacketConstants.NameLength + PacketConstants.NameLength + 1 + 16 + 13,
        [PacketConstants.CaLogin4] = 2 + 4 + PacketConstants.NameLength + 16 + 1 + 13,
        [PacketConstants.CaLoginChannel] = 2 + 4 + PacketConstants.NameLength + PacketConstants.NameLength + 1 + 16 + 13 + 1,
        [PacketConstants.LcCharServerLogin] = 86,
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
    {
        _client = client;
        _configStore = configStore;
        _messageStore = messageStore;
        _dbFactory = dbFactory;
        _charServers = charServers;
        _state = state;
        _subnetConfig = subnetConfig;
        _stream = client.GetStream();
    }

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

    private async Task HandlePacketAsync(short packetType, byte[] packet, CancellationToken cancellationToken)
    {
        switch (packetType)
        {
            case PacketConstants.CaReqHash:
                await SendAckHashAsync(cancellationToken);
                break;
            case PacketConstants.CaConnectInfoChanged:
                break;
            case PacketConstants.CaExeHashCheck:
                _clientHash = packet.AsSpan(2, 16).ToArray();
                break;
            case PacketConstants.CaLogin:
            await HandleLoginAsync(ParsePlainLogin(packet), false, cancellationToken);
            break;
            case PacketConstants.CaLogin2:
            await HandleLoginAsync(ParseMd5Login(packet), false, cancellationToken);
            break;
            case PacketConstants.CaLogin3:
            await HandleLoginAsync(ParseMd5Login(packet), false, cancellationToken);
            break;
            case PacketConstants.CaLogin4:
            await HandleLoginAsync(ParseMd5Login(packet), false, cancellationToken);
            break;
            case PacketConstants.CaLoginPcBang:
            await HandleLoginAsync(ParsePlainLogin(packet), false, cancellationToken);
            break;
            case PacketConstants.CaLoginChannel:
            await HandleLoginAsync(ParsePlainLogin(packet), false, cancellationToken);
            break;
            case PacketConstants.CaSsoLoginReq:
            await HandleLoginAsync(ParseSsoLogin(packet), false, cancellationToken);
            break;
            case PacketConstants.LcCharServerLogin:
                await HandleCharServerLoginAsync(packet, cancellationToken);
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

    private async Task HandleLoginAsync(LoginRequest request, bool isServer, CancellationToken cancellationToken)
    {
        if (Config.UseMd5Passwords && request.PasswordEnc != 0)
        {
            await SendRefuseLoginAsync(3, string.Empty, cancellationToken);
            return;
        }

        if (Config.NewAccountFlag && request.AutoRegisterBaseId != null)
        {
            request = request with { UserId = request.AutoRegisterBaseId };
        }

        var remoteIp = (_client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "0.0.0.0";
        var result = await AuthenticateAsync(request, remoteIp, isServer, cancellationToken);

        if (!result.Success)
        {
            if (isServer)
            {
                await SendCharServerAckAsync(3, cancellationToken);
            }
            else
            {
                await SendRefuseLoginAsync(result.ErrorCode, result.UnblockTime, cancellationToken);
                if (result.ErrorCode is 0 or 1)
                {
                    await ApplyDynamicIpBanAsync(remoteIp, cancellationToken);
                }
            }

            return;
        }

        if (isServer)
        {
            if (result.Sex != 2 || result.AccountId >= 5)
            {
                await SendCharServerAckAsync(3, cancellationToken);
                return;
            }

            RegisterCharServer(result, request, cancellationToken);
            await SendCharServerAckAsync(0, cancellationToken);
            return;
        }

        if (_charServers.Servers.Count == 0)
        {
            await SendNotifyBanAsync(1, cancellationToken);
            return;
        }

        if (_state.TryGetOnlineUser(result.AccountId, out var existing))
        {
            if (existing.CharServerId >= 0)
            {
                await SendKickRequestAsync(result.AccountId, cancellationToken);
                _state.ScheduleWaitingDisconnect(result.AccountId);
                await SendNotifyBanAsync(8, cancellationToken);
                return;
            }

            if (existing.CharServerId == -1)
            {
                _state.RemoveAuthNode(result.AccountId);
                _state.RemoveOnlineUser(result.AccountId);
            }
        }

        _state.AddAuthNode(new AuthNode
        {
            AccountId = result.AccountId,
            LoginId1 = result.LoginId1,
            LoginId2 = result.LoginId2,
            Sex = result.Sex,
            ClientType = request.ClientType,
            Ip = result.Ip
        });

        _state.AddOnlineUser(-1, result.AccountId);
        _state.ScheduleWaitingDisconnect(result.AccountId);

        var remoteAddress = (_client.Client.RemoteEndPoint as IPEndPoint)?.Address;
        await SendAcceptLoginAsync(result, remoteAddress, cancellationToken);
    }

    private async Task HandleCharServerLoginAsync(byte[] packet, CancellationToken cancellationToken)
    {
        var user = ReadFixedString(packet, 2, PacketConstants.NameLength);
        var pass = ReadFixedString(packet, 26, PacketConstants.NameLength);

        if (Config.UseMd5Passwords)
        {
            pass = Md5Hex(Encoding.ASCII.GetBytes(pass));
        }

        var loginRequest = new LoginRequest(user, pass, 0, 0, null, null) { RawPacket = packet };
        await HandleLoginAsync(loginRequest, true, cancellationToken);
    }

    private async Task HandleAuthRequestAsync(byte[] packet, CancellationToken cancellationToken)
    {
        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        var loginId1 = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(6, 4));
        var loginId2 = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(10, 4));
        var sex = packet[14];
        var requestId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(19, 4));

        byte result = 1;
        byte clientType = 0;

        if (_state.TryGetAuthNode(accountId, out var node))
        {
            if (node.AccountId == accountId && node.LoginId1 == loginId1 && node.LoginId2 == loginId2 && node.Sex == sex)
            {
                result = 0;
                clientType = node.ClientType;
                _state.RemoveAuthNode(accountId);
            }
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
        var db = _dbFactory();
        if (db == null)
        {
            return;
        }

        await using (db)
        {
            var account = await db.Accounts
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.AccountId == accountId, cancellationToken);

            if (account == null)
            {
                return;
            }

            await SendAccountDataAsync(account, cancellationToken);
        }
    }

    private async Task HandleChangeEmailAsync(byte[] packet, CancellationToken cancellationToken)
    {
        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        var actualEmail = ReadFixedString(packet, 6, 40);
        var newEmail = ReadFixedString(packet, 46, 40);

        if (!IsValidEmail(actualEmail) || !IsValidEmail(newEmail))
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
            var account = await db.Accounts.FirstOrDefaultAsync(a => a.AccountId == accountId, cancellationToken);
            if (account == null)
            {
                return;
            }

            if (!string.Equals(account.Email, actualEmail, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            account.Email = newEmail;
            db.Entry(account).Property(a => a.Email).IsModified = true;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task HandleUpdateAccountStateAsync(byte[] packet, CancellationToken cancellationToken)
    {
        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        var state = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(6, 4));

        var db = _dbFactory();
        if (db == null)
        {
            return;
        }

        await using (db)
        {
            var account = await db.Accounts.FirstOrDefaultAsync(a => a.AccountId == accountId, cancellationToken);
            if (account == null)
            {
                return;
            }

            account.State = state;
            db.Entry(account).Property(a => a.State).IsModified = true;
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

        var db = _dbFactory();
        if (db == null)
        {
            return;
        }

        await using (db)
        {
            var account = await db.Accounts.FirstOrDefaultAsync(a => a.AccountId == accountId, cancellationToken);
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
            db.Entry(account).Property(a => a.UnbanTime).IsModified = true;
            await db.SaveChangesAsync(cancellationToken);
            await BroadcastAccountStatusAsync(accountId, 1, newTime, cancellationToken);
        }
    }

    private async Task HandleChangeSexAsync(byte[] packet, CancellationToken cancellationToken)
    {
        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        var db = _dbFactory();
        if (db == null)
        {
            return;
        }

        await using (db)
        {
            var account = await db.Accounts.FirstOrDefaultAsync(a => a.AccountId == accountId, cancellationToken);
            if (account == null)
            {
                return;
            }

            if (string.Equals(account.Sex, "S", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            account.Sex = account.Sex.Equals("M", StringComparison.OrdinalIgnoreCase) ? "F" : "M";
            db.Entry(account).Property(a => a.Sex).IsModified = true;
            await db.SaveChangesAsync(cancellationToken);
            await BroadcastSexChangeAsync(accountId, account.Sex, cancellationToken);
        }
    }

    private async Task HandleUnbanAccountAsync(byte[] packet, CancellationToken cancellationToken)
    {
        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        var db = _dbFactory();
        if (db == null)
        {
            return;
        }

        await using (db)
        {
            var account = await db.Accounts.FirstOrDefaultAsync(a => a.AccountId == accountId, cancellationToken);
            if (account == null)
            {
                return;
            }

            if (account.UnbanTime == 0)
            {
                return;
            }

            account.UnbanTime = 0;
            db.Entry(account).Property(a => a.UnbanTime).IsModified = true;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task HandleVipRequestAsync(byte[] packet, CancellationToken cancellationToken)
    {
        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        var flag = packet[6];
        var timeDiff = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(7, 4));
        var mapFd = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(11, 4));

        var db = _dbFactory();
        if (db == null)
        {
            return;
        }

        await using (db)
        {
            var account = await db.Accounts.FirstOrDefaultAsync(a => a.AccountId == accountId, cancellationToken);
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
            db.Entry(account).Property(a => a.GroupId).IsModified = true;
            db.Entry(account).Property(a => a.OldGroup).IsModified = true;
            db.Entry(account).Property(a => a.CharacterSlots).IsModified = true;
            db.Entry(account).Property(a => a.VipTime).IsModified = true;
            await db.SaveChangesAsync(cancellationToken);

            if ((flag & 0x1) != 0)
            {
                var responseFlag = (byte)((isVip ? 0x1 : 0x0) | ((flag & 0x8) != 0 ? 0x4 : 0x0));
                await SendVipDataAsync(account, responseFlag, mapFd, cancellationToken);
            }

            await SendAccountDataAsync(account, cancellationToken);
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

        var db = _dbFactory();
        if (db == null)
        {
            return;
        }

        await using (db)
        {
            var account = await db.Accounts
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.AccountId == accountId, cancellationToken);

            if (account == null)
            {
                await SendAccountInfoResponseAsync(mapFd, userFd, userAid, accountId, null, cancellationToken);
                return;
            }

            await SendAccountInfoResponseAsync(mapFd, userFd, userAid, accountId, account, cancellationToken);
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

        _state.RemoveOnlineUsersByCharServer(_charServerId.Value);
    }

    private async Task HandlePincodeUpdateAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (packet.Length < 13)
        {
            return;
        }

        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4, 4));
        var pin = ReadFixedString(packet, 8, 5);
        var db = _dbFactory();
        if (db == null)
        {
            return;
        }

        await using (db)
        {
            var account = await db.Accounts.FirstOrDefaultAsync(a => a.AccountId == accountId, cancellationToken);
            if (account == null)
            {
                return;
            }

            account.Pincode = pin;
            account.PincodeChange = ToUnixTime(DateTime.UtcNow);
            db.Entry(account).Property(a => a.Pincode).IsModified = true;
            db.Entry(account).Property(a => a.PincodeChange).IsModified = true;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task HandlePincodeAuthFailAsync(byte[] packet, CancellationToken cancellationToken)
    {
        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        var db = _dbFactory();
        if (db == null)
        {
            return;
        }

        await using (db)
        {
            var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.AccountId == accountId, cancellationToken);
            if (account == null)
            {
                return;
            }

            await LogLoginAsync(db, account.UserId, account.LastIp, 100, "PIN Code check failed", cancellationToken);
            _state.RemoveOnlineUser(accountId);
        }
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

    private async Task<AuthResult> AuthenticateAsync(LoginRequest request, string remoteIp, bool isServer, CancellationToken cancellationToken)
    {
        var db = _dbFactory();
        if (db == null)
        {
            return AuthResult.Fail(1);
        }

        await using (db)
        {
            if (!isServer && Config.NewAccountFlag)
            {
                var regResult = await TryAutoRegisterAsync(db, request, remoteIp, cancellationToken);
                if (regResult.HasValue && regResult.Value != -1)
                {
                    return AuthResult.Fail((uint)regResult.Value);
                }
            }

            if (!isServer && Config.IpBanEnabled)
            {
                if (await IsIpBannedAsync(db, remoteIp, cancellationToken))
                {
                    return AuthResult.Fail(3);
                }
            }

            if (!isServer && Config.UseDnsbl && Config.DnsblServers.Length > 0)
            {
                if (await IsDnsblListedAsync(remoteIp, cancellationToken))
                {
                    await LogLoginAsync(db, request.UserId, remoteIp, 3, "DNSBL", cancellationToken);
                    return AuthResult.Fail(3);
                }
            }

            var userId = request.AutoRegisterBaseId ?? request.UserId;

            LoginAccount? account;
            if (IsCaseSensitive)
            {
                account = await db.Accounts
                    .AsNoTracking()
                    .FirstOrDefaultAsync(a => a.UserId == userId, cancellationToken);
            }
            else
            {
                var normalizedUserId = userId.ToLowerInvariant();
                account = await db.Accounts
                    .AsNoTracking()
                    .FirstOrDefaultAsync(a => a.UserId.ToLower() == normalizedUserId, cancellationToken);
            }

            if (account == null)
            {
                await LogLoginAsync(db, userId, remoteIp, 0, string.Empty, cancellationToken);
                return AuthResult.Fail(0);
            }

            if (!isServer && string.Equals(account.Sex, "S", StringComparison.OrdinalIgnoreCase))
            {
                await LogLoginAsync(db, userId, remoteIp, 0, "Server account", cancellationToken);
                return AuthResult.Fail(0);
            }

            if (!CheckPassword(request, account))
            {
                await LogLoginAsync(db, userId, remoteIp, 1, string.Empty, cancellationToken);
                return AuthResult.Fail(1);
            }

            var now = DateTime.UtcNow;
            if (account.ExpirationTime != 0 && account.ExpirationTime < ToUnixTime(now))
            {
                await LogLoginAsync(db, userId, remoteIp, 2, string.Empty, cancellationToken);
                return AuthResult.Fail(2);
            }

            if (account.UnbanTime != 0 && account.UnbanTime > ToUnixTime(now))
            {
                var unblock = FormatDate(FromUnixTime(account.UnbanTime));
                await LogLoginAsync(db, userId, remoteIp, 6, string.Empty, cancellationToken);
                return AuthResult.Fail(6, unblock);
            }

            if (account.State != 0)
            {
                var error = (uint)Math.Max(0, (int)account.State - 1);
                await LogLoginAsync(db, userId, remoteIp, error, string.Empty, cancellationToken);
                return AuthResult.Fail(error);
            }

            if (!isServer && Config.ClientHashCheck)
            {
                if (!IsClientHashAllowed(account.GroupId))
                {
                    await LogLoginAsync(db, userId, remoteIp, 5, string.Empty, cancellationToken);
                    return AuthResult.Fail(5);
                }
            }

            if (!isServer)
            {
                if (Config.GroupIdToConnect >= 0 && account.GroupId != Config.GroupIdToConnect)
                {
                    await LogLoginAsync(db, userId, remoteIp, 1, "Group id restriction", cancellationToken);
                    return AuthResult.Fail(1);
                }

                if (Config.MinGroupIdToConnect >= 0 && Config.GroupIdToConnect == -1 && account.GroupId < Config.MinGroupIdToConnect)
                {
                    await LogLoginAsync(db, userId, remoteIp, 1, "Min group id restriction", cancellationToken);
                    return AuthResult.Fail(1);
                }
            }

            await UpdateAccountLoginAsync(db, account, remoteIp, cancellationToken);
            await LogLoginAsync(db, userId, remoteIp, 100, "login ok", cancellationToken);

            var loginId1 = RandomNumberGenerator.GetInt32(1, int.MaxValue);
            var loginId2 = RandomNumberGenerator.GetInt32(1, int.MaxValue);

            return AuthResult.FromAccount(account, loginId1, loginId2, remoteIp);
        }
    }

    private void RegisterCharServer(AuthResult result, LoginRequest request, CancellationToken cancellationToken)
    {
        var packet = request.RawPacket;
        if (packet == null || packet.Length < 86)
        {
            return;
        }

        var ip = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(54, 4));
        var port = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(58, 2));
        var name = ReadFixedString(packet, 60, 20);
        var type = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(82, 2));
        var isNew = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(84, 2));

        var info = new CharServerInfo
        {
            Name = name,
            Ip = new IPAddress(ip),
            Port = port,
            Type = type,
            IsNew = isNew,
            Users = 0,
            Connection = new CharServerConnection(_stream),
        };

        _charServerId = (int)result.AccountId;
        _charServers.Register(_charServerId.Value, info);
    }

    private bool CheckPassword(LoginRequest request, LoginAccount account)
    {
        if (request.PasswordEnc == 0)
        {
            return string.Equals(request.Password, account.UserPass, StringComparison.Ordinal);
        }

        if (_md5Key == null || _md5Key.Length == 0)
        {
            return false;
        }

        if ((request.PasswordEnc & 0x01) != 0)
        {
            var hash = Md5Hex(Concat(_md5Key, Encoding.ASCII.GetBytes(account.UserPass)));
            if (string.Equals(request.Password, hash, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if ((request.PasswordEnc & 0x02) != 0)
        {
            var hash = Md5Hex(Concat(Encoding.ASCII.GetBytes(account.UserPass), _md5Key));
            if (string.Equals(request.Password, hash, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task UpdateAccountLoginAsync(LoginDbContext db, LoginAccount account, string remoteIp, CancellationToken cancellationToken)
    {
        account.LastLogin = DateTime.UtcNow;
        account.LastIp = remoteIp;
        account.UnbanTime = 0;
        account.LoginCount += 1;

        db.Attach(account);
        db.Entry(account).Property(a => a.LastLogin).IsModified = true;
        db.Entry(account).Property(a => a.LastIp).IsModified = true;
        db.Entry(account).Property(a => a.UnbanTime).IsModified = true;
        db.Entry(account).Property(a => a.LoginCount).IsModified = true;

        if (Config.UseWebAuthToken && !string.Equals(account.Sex, "S", StringComparison.OrdinalIgnoreCase))
        {
            account.WebAuthToken = GenerateWebAuthToken();
            account.WebAuthTokenEnabled = true;
            db.Entry(account).Property(a => a.WebAuthToken).IsModified = true;
            db.Entry(account).Property(a => a.WebAuthTokenEnabled).IsModified = true;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task LogLoginAsync(LoginDbContext db, string userId, string ip, uint resultCode, string message, CancellationToken cancellationToken)
    {
        if (!Config.LogLogin)
        {
            return;
        }

        var entry = new LoginLogEntry
        {
            Time = DateTime.UtcNow,
            Ip = ip,
            User = userId,
            ResultCode = (byte)Math.Min(byte.MaxValue, resultCode),
            Log = ResolveLoginMessage(resultCode, message),
        };

        await db.LoginLogs.AddAsync(entry, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private string ResolveLoginMessage(uint resultCode, string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            return message;
        }

        if (_messageStore.Current.TryGet(resultCode, out var resolved))
        {
            return resolved;
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
            LoginLogger.Info($"IPBan added for {list} ({Config.DynamicPassFailureBanDurationMinutes}m).");
        }
    }

    private async Task SendAckHashAsync(CancellationToken cancellationToken)
    {
        _md5Key ??= RandomNumberGenerator.GetBytes(PacketConstants.Md5KeyLength);
        var length = 4 + _md5Key.Length;
        var buffer = new byte[length];

        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.AcAckHash);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(2, 2), (short)length);
        Buffer.BlockCopy(_md5Key, 0, buffer, 4, _md5Key.Length);

        await _stream.WriteAsync(buffer, cancellationToken);
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

    private async Task SendCharServerAckAsync(byte result, CancellationToken cancellationToken)
    {
        var buffer = new byte[3];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.LcCharServerLoginAck);
        buffer[2] = result;
        await _stream.WriteAsync(buffer, cancellationToken);
    }

    private async Task SendKeepAliveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.LcKeepAliveResponse);
        await _stream.WriteAsync(buffer, cancellationToken);
    }

    private async Task SendVipDataAsync(LoginAccount account, byte flag, uint mapFd, CancellationToken cancellationToken)
    {
        var buffer = new byte[19];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.LcVipResponse);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2, 4), account.AccountId);
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

    private async Task SendAccountInfoResponseAsync(uint mapFd, uint userFd, uint userAid, uint accountId, LoginAccount? account, CancellationToken cancellationToken)
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
        WriteFixedString(bufferSuccess, 31, 40, account.Email);
        WriteFixedString(bufferSuccess, 71, 16, account.LastIp);
        WriteFixedString(bufferSuccess, 87, 24, account.LastLogin?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty);
        WriteFixedString(bufferSuccess, 111, 11, account.Birthdate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty);
        WriteFixedString(bufferSuccess, 122, PacketConstants.NameLength, account.UserId);

        await _stream.WriteAsync(bufferSuccess, cancellationToken);
    }

    private async Task SendAccountDataAsync(LoginAccount account, CancellationToken cancellationToken)
    {
        var buffer = new byte[75];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.LcAccountDataResponse);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2, 4), account.AccountId);

        WriteFixedString(buffer, 6, 40, account.Email);

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
        var serverEntrySize = 4 + 2 + 20 + 2 + 2 + 2 + 128;
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
            var ip = ResolveServerIp(server.Ip, clientAddress);
            var ipBytes = ip.GetAddressBytes();
            Buffer.BlockCopy(ipBytes, 0, buffer, offset, 4);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 4, 2), server.Port);
            WriteFixedString(buffer, offset + 6, 20, server.Name);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 26, 2), MapUserCount(server.Users));
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 28, 2), server.Type);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 30, 2), server.IsNew);
            offset += serverEntrySize;
        }

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
        if (Config.UseMd5Passwords)
        {
            pass = Md5Hex(Encoding.ASCII.GetBytes(pass));
        }

        var clientType = packet[^1];
        var (baseId, sex) = ParseAutoRegister(user);
        return new LoginRequest(user, pass, 0, clientType, baseId, sex);
    }

    private LoginRequest ParseMd5Login(byte[] packet)
    {
        var user = ReadFixedString(packet, 6, PacketConstants.NameLength);
        var md5Bytes = packet.AsSpan(6 + PacketConstants.NameLength, 16).ToArray();
        var pass = BytesToHex(md5Bytes);
        var clientType = packet[^1];
        var (baseId, sex) = ParseAutoRegister(user);
        return new LoginRequest(user, pass, PasswordEncMode, clientType, baseId, sex);
    }

    private LoginRequest ParseSsoLogin(byte[] packet)
    {
        var user = ReadFixedString(packet, 9, PacketConstants.NameLength);
        var tokenOffset = 9 + PacketConstants.NameLength + 27 + 17 + 15;
        var tokenLength = packet.Length - tokenOffset;
        var token = tokenLength > 0 ? Encoding.ASCII.GetString(packet, tokenOffset, tokenLength).TrimEnd('\0') : string.Empty;
        if (Config.UseMd5Passwords)
        {
            token = Md5Hex(Encoding.ASCII.GetBytes(token));
        }

        var clientType = packet[8];
        var (baseId, sex) = ParseAutoRegister(user);
        return new LoginRequest(user, token, 0, clientType, baseId, sex);
    }

    private static string ReadFixedString(byte[] buffer, int offset, int length)
    {
        return Encoding.ASCII.GetString(buffer, offset, length).TrimEnd('\0');
    }

    private static void WriteFixedString(byte[] buffer, int offset, int length, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        Buffer.BlockCopy(bytes, 0, buffer, offset, Math.Min(length, bytes.Length));
    }

    private static byte[] Concat(byte[] first, byte[] second)
    {
        var buffer = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, buffer, 0, first.Length);
        Buffer.BlockCopy(second, 0, buffer, first.Length, second.Length);
        return buffer;
    }

    private static string Md5Hex(byte[] data)
    {
        using var md5 = MD5.Create();
        return BytesToHex(md5.ComputeHash(data));
    }

    private static string BytesToHex(byte[] data)
    {
        var sb = new StringBuilder(data.Length * 2);
        foreach (var b in data)
        {
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    private static uint ToUnixTime(DateTime time)
    {
        return (uint)new DateTimeOffset(time).ToUnixTimeSeconds();
    }

    private static DateTime FromUnixTime(uint value)
    {
        return DateTimeOffset.FromUnixTimeSeconds(value).UtcDateTime;
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
                    LoginLogger.Warning($"DNSBL match: {query}");
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

    private readonly record struct LoginRequest(string UserId, string Password, int PasswordEnc, byte ClientType, string? AutoRegisterBaseId, char? AutoRegisterSex)
    {
        public byte[]? RawPacket { get; init; }
    }

    private readonly record struct AuthResult(
        bool Success,
        uint ErrorCode,
        string UnblockTime,
        uint AccountId,
        uint LoginId1,
        uint LoginId2,
        byte Sex,
        string WebAuthToken,
        uint Ip)
    {
        public static AuthResult Fail(uint error, string unblockTime = "")
        {
            return new AuthResult(false, error, unblockTime, 0, 0, 0, 0, string.Empty, 0);
        }

        public static AuthResult FromAccount(LoginAccount account, int loginId1, int loginId2, string ip)
        {
            var sex = (byte)(account.Sex.Equals("F", StringComparison.OrdinalIgnoreCase) ? 0 :
                account.Sex.Equals("M", StringComparison.OrdinalIgnoreCase) ? 1 : 2);
            var parsedIp = ParseIp(ip);
            return new AuthResult(true, 0, string.Empty, account.AccountId, (uint)loginId1, (uint)loginId2, sex, account.WebAuthToken ?? string.Empty, parsedIp);
        }
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

    private static (string? baseId, char? sex) ParseAutoRegister(string userId)
    {
        if (userId.Length <= 2)
        {
            return (null, null);
        }

        var suffix = userId[^2..];
        if (!suffix.StartsWith("_", StringComparison.Ordinal))
        {
            return (null, null);
        }

        var sexChar = suffix[1];
        if (sexChar is 'M' or 'm')
        {
            return (userId[..^2], 'M');
        }

        if (sexChar is 'F' or 'f')
        {
            return (userId[..^2], 'F');
        }

        return (null, null);
    }

    private async Task<int?> TryAutoRegisterAsync(LoginDbContext db, LoginRequest request, string remoteIp, CancellationToken cancellationToken)
    {
        if (request.AutoRegisterBaseId == null || request.AutoRegisterSex == null)
        {
            return null;
        }

        if (request.PasswordEnc != 0)
        {
            return 0;
        }

        if (!Config.NewAccountFlag)
        {
            return null;
        }

        if (!_state.IsRegistrationAllowed(Config.AllowedRegistrations, Config.RegistrationWindowSeconds))
        {
            return 3;
        }

        var baseId = request.AutoRegisterBaseId;
        if (baseId.Length < Config.AccountNameMinLength || request.Password.Length < Config.PasswordMinLength)
        {
            return 1;
        }

        var sex = request.AutoRegisterSex.Value;
        if (sex is not ('M' or 'F'))
        {
            return 0;
        }

        var exists = await ExistsUserIdAsync(db, baseId, cancellationToken);
        if (exists)
        {
            return 1;
        }

        var expiration = 0u;
        if (Config.StartLimitedTimeSeconds != -1)
        {
            expiration = ToUnixTime(DateTime.UtcNow.AddSeconds(Config.StartLimitedTimeSeconds));
        }

        var account = new LoginAccount
        {
            UserId = baseId,
            UserPass = request.Password,
            Sex = sex.ToString(),
            Email = "a@a.com",
            ExpirationTime = expiration,
            LastLogin = null,
            LastIp = remoteIp,
            Birthdate = null,
            Pincode = string.Empty,
            PincodeChange = 0,
            CharacterSlots = (byte)Config.CharPerAccount,
            VipTime = 0,
            OldGroup = 0,
            GroupId = 0,
            State = 0,
            LoginCount = 0,
            WebAuthToken = null,
            WebAuthTokenEnabled = false,
        };

        db.Accounts.Add(account);
        await db.SaveChangesAsync(cancellationToken);
        _state.RegisterSuccess(Config.RegistrationWindowSeconds);
        return -1;
    }

    private async Task<bool> ExistsUserIdAsync(LoginDbContext db, string userId, CancellationToken cancellationToken)
    {
        if (IsCaseSensitive)
        {
            return await db.Accounts.AsNoTracking().AnyAsync(a => a.UserId == userId, cancellationToken);
        }

        var normalizedUserId = userId.ToLowerInvariant();
        return await db.Accounts.AsNoTracking().AnyAsync(a => a.UserId.ToLower() == normalizedUserId, cancellationToken);
    }

    private bool IsClientHashAllowed(int accountGroupId)
    {
        var rules = Config.ClientHashRules;
        if (rules.Count == 0)
        {
            return false;
        }

        foreach (var rule in rules)
        {
            if (accountGroupId < rule.GroupId)
            {
                continue;
            }

            if (rule.AllowWithoutHash)
            {
                return true;
            }

            if (_clientHash != null && rule.Hash != null && _clientHash.SequenceEqual(rule.Hash))
            {
                return true;
            }
        }

        return false;
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

    private async Task DisableWebAuthTokenAsync(uint accountId)
    {
        var db = _dbFactory();
        if (db == null)
        {
            return;
        }

        await using (db)
        {
            var account = await db.Accounts.FirstOrDefaultAsync(a => a.AccountId == accountId);
            if (account == null)
            {
                return;
            }

            account.WebAuthTokenEnabled = false;
            db.Entry(account).Property(a => a.WebAuthTokenEnabled).IsModified = true;
            await db.SaveChangesAsync();
        }
    }

    private static string GenerateWebAuthToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(8);
        var sb = new StringBuilder(16);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
}
