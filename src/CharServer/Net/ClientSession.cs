using System.Buffers.Binary;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Athena.Net.CharServer.Config;
using Athena.Net.CharServer.Db;
using Athena.Net.CharServer.Db.Entities;
using Athena.Net.CharServer.Logging;
using Microsoft.EntityFrameworkCore;

namespace Athena.Net.CharServer.Net;

public sealed class ClientSession : IDisposable
{
    private const int CharacterInfoSize = 175;
    private const int CharDelEmail = 1;
    private const int CharDelBirthdate = 2;
    private const int CharDelRestrictParty = 1;
    private const int CharDelRestrictGuild = 2;
    private const int JobSummoner = 4218;
    private const int JobBabySummoner = 4220;
    private const ushort JobSecondJobStart = 4331;
    private const ushort JobSecondJobEnd = 4350;
    private const uint WeaponHiddenOptionMask = 0x20
        | 0x80000
        | 0x100000
        | 0x200000
        | 0x400000
        | 0x800000
        | 0x1000000
        | 0x2000000
        | 0x4000000
        | 0x8000000;

    private static readonly Dictionary<short, int> PacketLengths = new()
    {
        [PacketConstants.ChReqConnect] = 17,
        [PacketConstants.ChSelectChar] = 3,
        [PacketConstants.ChMakeChar] = 36,
        [PacketConstants.ChDeleteChar] = 56,
        [PacketConstants.ChDeleteChar3Reserved] = 6,
        [PacketConstants.ChDeleteChar3] = 12,
        [PacketConstants.ChDeleteChar3Cancel] = 6,
        [PacketConstants.ChCharListReq] = 2,
        [PacketConstants.ChPing] = 6,
    };

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly CharConfigStore _configStore;
    private readonly LoginServerConnector _loginConnector;
    private readonly Func<CharDbContext?> _dbFactory;
    private readonly int _startStatusPoints;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private uint _accountId;
    private uint _loginId1;
    private uint _loginId2;
    private byte _sex;
    private bool _authenticated;
    private int _charSlots;
    private byte _charsVip;
    private byte _charsBilling;
    private string _email = string.Empty;
    private string _birthdate = string.Empty;

    public ClientSession(int sessionId, TcpClient client, CharConfigStore configStore, LoginServerConnector loginConnector, Func<CharDbContext?> dbFactory, int startStatusPoints)
    {
        SessionId = sessionId;
        _client = client;
        _configStore = configStore;
        _loginConnector = loginConnector;
        _dbFactory = dbFactory;
        _startStatusPoints = startStatusPoints;
        _stream = client.GetStream();
        _charSlots = _configStore.Current.MinChars;
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

    public void Dispose()
    {
        _stream.Dispose();
        _writeLock.Dispose();
    }

    public async Task HandleAuthResponseAsync(uint accountId, uint loginId1, uint loginId2, byte sex, byte result, byte clientType)
    {
        if (_accountId != accountId || _loginId1 != loginId1 || _loginId2 != loginId2 || _sex != sex)
        {
            return;
        }

        if (result != 0)
        {
            await SendRefuseEnterAsync(0, CancellationToken.None);
            return;
        }

        _authenticated = true;
        if (!_loginConnector.TrySendAccountDataRequest(this, _accountId))
        {
            await SendCharListAsync(CancellationToken.None);
        }
    }

    public async Task HandleAccountDataAsync(uint accountId, byte charSlots, bool isVip, byte vipSlots, byte billingSlots, string email, string birthdate)
    {
        if (_accountId != accountId)
        {
            return;
        }

        var config = _configStore.Current;
        var slots = charSlots == 0 ? config.MinChars : charSlots;
        _charSlots = Math.Clamp(slots, config.MinChars, config.MaxChars);
        _charsVip = isVip ? vipSlots : (byte)0;
        _charsBilling = billingSlots;
        _email = email;
        _birthdate = birthdate;

        await SendCharListAsync(CancellationToken.None);
    }

    public Task SendRefuseEnterAsync(byte errorCode, CancellationToken cancellationToken)
    {
        var buffer = new byte[3];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.HcRefuseEnter);
        buffer[2] = errorCode;
        return WriteAsync(buffer, cancellationToken);
    }

    private async Task HandlePacketAsync(short packetType, byte[] packet, CancellationToken cancellationToken)
    {
        switch (packetType)
        {
            case PacketConstants.ChReqConnect:
                await HandleConnectAsync(packet, cancellationToken);
                break;
            case PacketConstants.ChCharListReq:
                await SendCharListPageAsync(cancellationToken);
                break;
            case PacketConstants.ChMakeChar:
                await HandleMakeCharAsync(packet, cancellationToken);
                break;
            case PacketConstants.ChDeleteChar:
                await HandleDeleteCharAsync(packet, cancellationToken);
                break;
            case PacketConstants.ChDeleteChar3Reserved:
                await HandleDeleteChar3ReserveAsync(packet, cancellationToken);
                break;
            case PacketConstants.ChDeleteChar3:
                await HandleDeleteChar3AcceptAsync(packet, cancellationToken);
                break;
            case PacketConstants.ChDeleteChar3Cancel:
                await HandleDeleteChar3CancelAsync(packet, cancellationToken);
                break;
            case PacketConstants.ChPing:
                break;
            default:
                CharLogger.Warning($"Unknown char packet 0x{packetType:X4}, disconnecting.");
                _client.Close();
                break;
        }
    }

    private async Task HandleConnectAsync(byte[] packet, CancellationToken cancellationToken)
    {
        _accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        _loginId1 = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(6, 4));
        _loginId2 = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(10, 4));
        _sex = packet[16];

        var buffer = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), _accountId);
        await WriteAsync(buffer, cancellationToken);

        var remoteIp = (_client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.Loopback;
        if (!_loginConnector.TrySendAuthRequest(this, _accountId, _loginId1, _loginId2, _sex, remoteIp))
        {
            await SendRefuseEnterAsync(0, cancellationToken);
        }
    }

    private async Task SendCharListAsync(CancellationToken cancellationToken)
    {
        if (!_authenticated)
        {
            return;
        }

        var db = _dbFactory();
        if (db == null)
        {
            await SendRefuseEnterAsync(0, cancellationToken);
            return;
        }

        List<CharCharacter> characters;
        await using (db)
        {
            characters = await db.Characters
                .AsNoTracking()
                .Where(c => c.AccountId == _accountId)
                .OrderBy(c => c.CharNum)
                .ToListAsync(cancellationToken);
        }

        var config = _configStore.Current;
        await SendAcceptEnter2Async(config, cancellationToken);
        await SendAcceptEnterAsync(config, characters, cancellationToken);
        await SendCharListNotifyAsync(cancellationToken);
    }

    private async Task SendCharListPageAsync(CancellationToken cancellationToken)
    {
        if (!_authenticated)
        {
            return;
        }

        var db = _dbFactory();
        if (db == null)
        {
            return;
        }

        List<CharCharacter> characters;
        await using (db)
        {
            characters = await db.Characters
                .AsNoTracking()
                .Where(c => c.AccountId == _accountId)
                .OrderBy(c => c.CharNum)
                .ToListAsync(cancellationToken);
        }

        var payload = BuildCharacterInfoPayload(characters);
        var length = (short)(4 + payload.Length);
        var buffer = new byte[length];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.HcAckCharInfoPerPage);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(2, 2), length);
        payload.CopyTo(buffer.AsSpan(4));
        await WriteAsync(buffer, cancellationToken);
    }

    private async Task SendAcceptEnter2Async(CharConfig config, CancellationToken cancellationToken)
    {
        var buffer = new byte[29];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.HcAcceptEnter2);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(2, 2), 29);
        buffer[4] = (byte)Math.Clamp(config.MinChars, 0, byte.MaxValue);
        buffer[5] = _charsVip;
        buffer[6] = _charsBilling;
        buffer[7] = (byte)Math.Clamp(_charSlots, 0, byte.MaxValue);
        buffer[8] = (byte)Math.Clamp(config.MaxChars, 0, byte.MaxValue);
        buffer.AsSpan(9, 20).Clear();

        await WriteAsync(buffer, cancellationToken);
    }

    private async Task SendAcceptEnterAsync(CharConfig config, IReadOnlyList<CharCharacter> characters, CancellationToken cancellationToken)
    {
        var payload = BuildCharacterInfoPayload(characters);
        var length = 27 + payload.Length;
        var buffer = new byte[length];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.HcAcceptEnter);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(2, 2), (short)length);
        buffer[4] = (byte)Math.Clamp(config.MaxChars, 0, byte.MaxValue);
        buffer[5] = (byte)Math.Clamp(config.MinChars, 0, byte.MaxValue);
        var premiumEnd = Math.Min(config.MinChars + _charsVip, config.MaxChars);
        buffer[6] = (byte)Math.Clamp(premiumEnd, 0, byte.MaxValue);
        buffer.AsSpan(7, 20).Clear();
        if (payload.Length > 0)
        {
            payload.CopyTo(buffer.AsSpan(27));
        }

        await WriteAsync(buffer, cancellationToken);
    }

    private async Task SendCharListNotifyAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.HcCharListNotify);
        var pages = Math.Max((_charSlots + 2) / 3, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2, 4), (uint)pages);
        await WriteAsync(buffer, cancellationToken);
    }

    private async Task HandleMakeCharAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!_authenticated)
        {
            await SendRefuseMakeCharAsync(cancellationToken);
            return;
        }

        var config = _configStore.Current;
        if (!config.CharNew)
        {
            await SendRefuseMakeCharAsync(cancellationToken);
            return;
        }

        var name = ReadFixedString(packet.AsSpan(2, 24));
        var slot = packet[26];
        var hairColor = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(27, 2));
        var hairStyle = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(29, 2));
        var job = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(31, 4));
        var sex = packet[35];

        if (string.IsNullOrWhiteSpace(name) || slot >= _charSlots)
        {
            await SendRefuseMakeCharAsync(cancellationToken);
            return;
        }

        if (job != 0 && job != JobSummoner && job != JobBabySummoner)
        {
            await SendRefuseMakeCharAsync(cancellationToken);
            return;
        }

        var db = _dbFactory();
        if (db == null)
        {
            await SendRefuseMakeCharAsync(cancellationToken);
            return;
        }

        await using (db)
        {
            var accountCount = await db.Characters.CountAsync(c => c.AccountId == _accountId, cancellationToken);
            if (accountCount >= _charSlots)
            {
                await SendRefuseMakeCharAsync(cancellationToken);
                return;
            }

            var nameTaken = await db.Characters.AnyAsync(c => c.Name == name, cancellationToken);
            if (nameTaken)
            {
                await SendRefuseMakeCharAsync(cancellationToken);
                return;
            }

            var slotTaken = await db.Characters.AnyAsync(c => c.AccountId == _accountId && c.CharNum == slot, cancellationToken);
            if (slotTaken)
            {
                await SendRefuseMakeCharAsync(cancellationToken);
                return;
            }

            var startPoint = SelectStartPoint(config, job);
            var vit = 1;
            var intStat = 1;
            var maxHp = (uint)(40 * (100 + vit) / 100);
            var maxSp = (uint)(11 * (100 + intStat) / 100);
            var character = new CharCharacter
            {
                AccountId = _accountId,
                CharNum = slot,
                Name = name,
                Class = (ushort)Math.Clamp((int)job, 0, ushort.MaxValue),
                BaseLevel = 1,
                JobLevel = 1,
                BaseExp = 0,
                JobExp = 0,
                Zeny = (uint)Math.Clamp(config.StartZeny, 0, int.MaxValue),
                Str = 1,
                Agi = 1,
                Vit = (ushort)vit,
                Int = (ushort)intStat,
                Dex = 1,
                Luk = 1,
                Pow = 0,
                Sta = 0,
                Wis = 0,
                Spl = 0,
                Con = 0,
                Crt = 0,
                MaxHp = maxHp,
                Hp = maxHp,
                MaxSp = maxSp,
                Sp = maxSp,
                MaxAp = 0,
                Ap = 0,
                StatusPoint = (uint)Math.Max(0, _startStatusPoints),
                SkillPoint = 0,
                TraitPoint = 0,
                Option = 0,
                Karma = 0,
                Manner = 0,
                PartyId = 0,
                GuildId = 0,
                PetId = 0,
                HomunId = 0,
                ElementalId = 0,
                Hair = (byte)Math.Clamp((int)hairStyle, 0, byte.MaxValue),
                HairColor = hairColor,
                ClothesColor = 0,
                Body = 0,
                Weapon = 0,
                Shield = 0,
                HeadTop = 0,
                HeadMid = 0,
                HeadBottom = 0,
                Robe = 0,
                LastMap = startPoint.Map,
                LastX = startPoint.X,
                LastY = startPoint.Y,
                LastInstanceId = 0,
                SaveMap = startPoint.Map,
                SaveX = startPoint.X,
                SaveY = startPoint.Y,
                PartnerId = 0,
                Online = 0,
                Father = 0,
                Mother = 0,
                Child = 0,
                Fame = 0,
                Rename = 0,
                DeleteDate = 0,
                Moves = 0,
                UnbanTime = 0,
                Font = 0,
                UniqueItemCounter = 0,
                Sex = (sex == 0 || sex == 1) ? (sex == 0 ? "F" : "M") : (_sex == 0 ? "F" : "M"),
                HotkeyRowShift = 0,
                HotkeyRowShift2 = 0,
                ClanId = 0,
                LastLogin = null,
                TitleId = 0,
                ShowEquip = 0,
                InventorySlots = 100,
                BodyDirection = 0,
                DisableCall = 0,
                DisablePartyInvite = 0,
                DisableShowCostumes = 0,
            };

            db.Characters.Add(character);
            await db.SaveChangesAsync(cancellationToken);

            var items = SelectStartItems(config, job)
                .Select(item => new CharInventory
                {
                    CharId = character.CharId,
                    NameId = item.ItemId,
                    Amount = item.Amount,
                    Equip = item.EquipPosition,
                    Identify = 1,
                    Refine = 0,
                    Attribute = 0,
                    Card0 = 0,
                    Card1 = 0,
                    Card2 = 0,
                    Card3 = 0,
                    OptionId0 = 0,
                    OptionVal0 = 0,
                    OptionParm0 = 0,
                    OptionId1 = 0,
                    OptionVal1 = 0,
                    OptionParm1 = 0,
                    OptionId2 = 0,
                    OptionVal2 = 0,
                    OptionParm2 = 0,
                    OptionId3 = 0,
                    OptionVal3 = 0,
                    OptionParm3 = 0,
                    OptionId4 = 0,
                    OptionVal4 = 0,
                    OptionParm4 = 0,
                    ExpireTime = 0,
                    Favorite = 0,
                    Bound = 0,
                    UniqueId = 0,
                    EquipSwitch = 0,
                    EnchantGrade = 0,
                })
                .ToList();

            if (items.Count > 0)
            {
                db.Inventory.AddRange(items);
                await db.SaveChangesAsync(cancellationToken);
            }

            await SendAcceptMakeCharAsync(character, cancellationToken);
        }
    }

    private async Task HandleDeleteCharAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!_authenticated)
        {
            await SendRefuseDeleteCharAsync(cancellationToken);
            return;
        }

        var charId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        var code = ReadFixedString(packet.AsSpan(6, 50));
        var config = _configStore.Current;

        if (!IsDeleteCodeValid(code, config.CharDeleteOption))
        {
            await SendRefuseDeleteCharAsync(cancellationToken);
            return;
        }

        var db = _dbFactory();
        if (db == null)
        {
            await SendRefuseDeleteCharAsync(cancellationToken);
            return;
        }

        await using (db)
        {
            var character = await db.Characters
                .FirstOrDefaultAsync(c => c.CharId == charId && c.AccountId == _accountId, cancellationToken);
            if (character == null || IsDeleteRestricted(config, character))
            {
                await SendRefuseDeleteCharAsync(cancellationToken);
                return;
            }

            var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (config.CharDeleteDelaySeconds > 0 && (character.DeleteDate == 0 || character.DeleteDate > now))
            {
                await SendRefuseDeleteCharAsync(cancellationToken);
                return;
            }

            db.Characters.Remove(character);
            await db.SaveChangesAsync(cancellationToken);
        }

        var buffer = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.HcAcceptDeleteChar);
        await WriteAsync(buffer, cancellationToken);
    }

    private async Task HandleDeleteChar3ReserveAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!_authenticated)
        {
            await SendDeleteChar3ReservedAsync(0, 3, 0, cancellationToken);
            return;
        }

        var charId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        var config = _configStore.Current;
        var db = _dbFactory();
        if (db == null)
        {
            await SendDeleteChar3ReservedAsync(charId, 3, 0, cancellationToken);
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await using (db)
        {
            var character = await db.Characters
                .FirstOrDefaultAsync(c => c.CharId == charId && c.AccountId == _accountId, cancellationToken);
            if (character == null)
            {
                await SendDeleteChar3ReservedAsync(charId, 3, 0, cancellationToken);
                return;
            }

            if ((config.CharDeleteRestriction & CharDelRestrictGuild) != 0 && character.GuildId != 0)
            {
                await SendDeleteChar3ReservedAsync(charId, 4, 0, cancellationToken);
                return;
            }

            if ((config.CharDeleteRestriction & CharDelRestrictParty) != 0 && character.PartyId != 0)
            {
                await SendDeleteChar3ReservedAsync(charId, 5, 0, cancellationToken);
                return;
            }

            if (IsDeleteLevelBlocked(config.CharDeleteLevel, character.BaseLevel))
            {
                await SendDeleteChar3ReservedAsync(charId, 0, 0, cancellationToken);
                return;
            }

            var deleteDate = (uint)DateTimeOffset.UtcNow.AddSeconds(config.CharDeleteDelaySeconds).ToUnixTimeSeconds();
            character.DeleteDate = deleteDate;
            await db.SaveChangesAsync(cancellationToken);

            var remaining = deleteDate > now ? (uint)(deleteDate - now) : 0u;
            await SendDeleteChar3ReservedAsync(charId, 1, remaining, cancellationToken);
        }
    }

    private async Task HandleDeleteChar3AcceptAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!_authenticated)
        {
            await SendDeleteChar3ResultAsync(0, 3, cancellationToken);
            return;
        }

        var charId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        var birthdate = ConvertBirthdate(packet.AsSpan(6, 6));
        if (!IsDeleteBirthdateValid(birthdate))
        {
            await SendDeleteChar3ResultAsync(charId, 5, cancellationToken);
            return;
        }

        var config = _configStore.Current;
        var db = _dbFactory();
        if (db == null)
        {
            await SendDeleteChar3ResultAsync(charId, 3, cancellationToken);
            return;
        }

        var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await using (db)
        {
            var character = await db.Characters
                .FirstOrDefaultAsync(c => c.CharId == charId && c.AccountId == _accountId, cancellationToken);
            if (character == null)
            {
                await SendDeleteChar3ResultAsync(charId, 3, cancellationToken);
                return;
            }

            if (IsDeleteRestricted(config, character))
            {
                await SendDeleteChar3ResultAsync(charId, 2, cancellationToken);
                return;
            }

            if (config.CharDeleteDelaySeconds > 0 && (character.DeleteDate == 0 || character.DeleteDate > now))
            {
                await SendDeleteChar3ResultAsync(charId, 4, cancellationToken);
                return;
            }

            db.Characters.Remove(character);
            await db.SaveChangesAsync(cancellationToken);
        }

        await SendDeleteChar3ResultAsync(charId, 1, cancellationToken);
    }

    private async Task HandleDeleteChar3CancelAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!_authenticated)
        {
            await SendDeleteChar3CancelAsync(0, 2, cancellationToken);
            return;
        }

        var charId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        var db = _dbFactory();
        if (db == null)
        {
            await SendDeleteChar3CancelAsync(charId, 2, cancellationToken);
            return;
        }

        await using (db)
        {
            var character = await db.Characters
                .FirstOrDefaultAsync(c => c.CharId == charId && c.AccountId == _accountId, cancellationToken);
            if (character == null || character.DeleteDate == 0)
            {
                await SendDeleteChar3CancelAsync(charId, 2, cancellationToken);
                return;
            }

            character.DeleteDate = 0;
            await db.SaveChangesAsync(cancellationToken);
        }

        await SendDeleteChar3CancelAsync(charId, 1, cancellationToken);
    }

    private Task SendRefuseMakeCharAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[3];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.HcRefuseMakeChar);
        buffer[2] = 0;
        return WriteAsync(buffer, cancellationToken);
    }

    private Task SendRefuseDeleteCharAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[3];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.HcRefuseDeleteChar);
        buffer[2] = 0;
        return WriteAsync(buffer, cancellationToken);
    }

    private Task SendDeleteChar3ReservedAsync(uint charId, int result, uint date, CancellationToken cancellationToken)
    {
        var buffer = new byte[14];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.HcDeleteChar3Reserved);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2, 4), charId);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(6, 4), result);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(10, 4), date);
        return WriteAsync(buffer, cancellationToken);
    }

    private Task SendDeleteChar3ResultAsync(uint charId, int result, CancellationToken cancellationToken)
    {
        var buffer = new byte[10];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.HcDeleteChar3);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2, 4), charId);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(6, 4), result);
        return WriteAsync(buffer, cancellationToken);
    }

    private Task SendDeleteChar3CancelAsync(uint charId, int result, CancellationToken cancellationToken)
    {
        var buffer = new byte[10];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.HcDeleteChar3Cancel);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2, 4), charId);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(6, 4), result);
        return WriteAsync(buffer, cancellationToken);
    }

    private Task SendAcceptMakeCharAsync(CharCharacter character, CancellationToken cancellationToken)
    {
        var payload = BuildCharacterInfoPayload(new[] { character });
        var buffer = new byte[2 + payload.Length];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.HcAcceptMakeChar);
        payload.CopyTo(buffer.AsSpan(2));
        return WriteAsync(buffer, cancellationToken);
    }

    private static byte[] BuildCharacterInfoPayload(IReadOnlyList<CharCharacter> characters)
    {
        if (characters.Count == 0)
        {
            return Array.Empty<byte>();
        }

        var payload = new byte[characters.Count * CharacterInfoSize];
        var offset = 0;
        foreach (var character in characters)
        {
            WriteCharacterInfo(payload.AsSpan(offset, CharacterInfoSize), character);
            offset += CharacterInfoSize;
        }

        return payload;
    }

    private static void WriteCharacterInfo(Span<byte> buffer, CharCharacter character)
    {
        buffer.Clear();
        var bodyValue = character.Body > JobSecondJobStart && character.Body < JobSecondJobEnd ? (ushort)1 : (ushort)0;
        var weaponValue = (character.Option & WeaponHiddenOptionMask) != 0 ? (ushort)0 : character.Weapon;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(0, 4), character.CharId);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(4, 8), (long)character.BaseExp);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(12, 4), (int)Math.Min(character.Zeny, int.MaxValue));
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(16, 8), (long)character.JobExp);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(24, 4), character.JobLevel);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(28, 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(32, 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(36, 4), (int)character.Option);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(40, 4), character.Karma);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(44, 4), character.Manner);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(48, 2), (short)Math.Min(character.StatusPoint, short.MaxValue));
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(50, 8), character.Hp);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(58, 8), character.MaxHp);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(66, 8), character.Sp);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(74, 8), character.MaxSp);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(82, 2), 150);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(84, 2), (short)Math.Min(character.Class, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(86, 2), (short)Math.Min(character.Hair, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(88, 2), (short)Math.Min(bodyValue, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(90, 2), (short)Math.Min(weaponValue, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(92, 2), (short)Math.Min(character.BaseLevel, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(94, 2), (short)Math.Min(character.SkillPoint, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(96, 2), (short)Math.Min(character.HeadBottom, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(98, 2), (short)Math.Min(character.Shield, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(100, 2), (short)Math.Min(character.HeadTop, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(102, 2), (short)Math.Min(character.HeadMid, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(104, 2), (short)Math.Min(character.HairColor, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(106, 2), (short)Math.Min(character.ClothesColor, short.MaxValue));
        WriteFixedString(buffer.Slice(108, 24), character.Name);
        buffer[132] = (byte)Math.Min(character.Str, byte.MaxValue);
        buffer[133] = (byte)Math.Min(character.Agi, byte.MaxValue);
        buffer[134] = (byte)Math.Min(character.Vit, byte.MaxValue);
        buffer[135] = (byte)Math.Min(character.Int, byte.MaxValue);
        buffer[136] = (byte)Math.Min(character.Dex, byte.MaxValue);
        buffer[137] = (byte)Math.Min(character.Luk, byte.MaxValue);
        buffer[138] = character.CharNum;
        buffer[139] = (byte)Math.Min(character.HairColor, byte.MaxValue);
        var renameFlag = character.Rename > 0 ? 0 : 1;
        BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(140, 2), (short)renameFlag);
        WriteFixedString(buffer.Slice(142, 16), character.LastMap);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var deleteRemaining = character.DeleteDate > now ? (int)Math.Min(character.DeleteDate - now, int.MaxValue) : 0;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(158, 4), deleteRemaining);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(162, 4), character.Robe);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(166, 4), (int)Math.Min(character.Moves, int.MaxValue));
        var nameChangeCount = character.Rename > 0 ? 1 : 0;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(170, 4), nameChangeCount);
        buffer[174] = character.Sex.Equals("M", StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)0;
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

    private static string ConvertBirthdate(ReadOnlySpan<byte> data)
    {
        if (data.Length < 6)
        {
            return string.Empty;
        }

        var chars = new char[8];
        chars[0] = (char)data[0];
        chars[1] = (char)data[1];
        chars[2] = '-';
        chars[3] = (char)data[2];
        chars[4] = (char)data[3];
        chars[5] = '-';
        chars[6] = (char)data[4];
        chars[7] = (char)data[5];
        return new string(chars);
    }

    private static StartPoint SelectStartPoint(CharConfig config, uint job)
    {
        var points = IsDoramJob(job) ? config.StartPointsDoram : config.StartPoints;
        if (!IsDoramJob(job) && config.UsePreRenewalStartPoints && config.StartPointsPre.Count > 0)
        {
            points = config.StartPointsPre;
        }
        if (points.Count == 0)
        {
            return new StartPoint("iz_int", 18, 26);
        }

        var index = Random.Shared.Next(points.Count);
        return points[index];
    }

    private static IReadOnlyList<StartItem> SelectStartItems(CharConfig config, uint job)
    {
        if (IsDoramJob(job))
        {
            return config.StartItemsDoram;
        }

        if (config.UsePreRenewalStartPoints && config.StartItemsPre.Count > 0)
        {
            return config.StartItemsPre;
        }

        return config.StartItems;
    }

    private static bool IsDoramJob(uint job)
    {
        return job == JobSummoner || job == JobBabySummoner;
    }

    private bool IsDeleteCodeValid(string code, int option)
    {
        var isEmailMatch = (option & CharDelEmail) != 0 &&
            ((!string.IsNullOrWhiteSpace(code) && string.Equals(code, _email, StringComparison.OrdinalIgnoreCase)) ||
             (string.Equals(_email, "a@a.com", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(code)));

        if (isEmailMatch)
        {
            return true;
        }

        if ((option & CharDelBirthdate) == 0)
        {
            return false;
        }

        var birthdate = _birthdate.Length >= 2 ? _birthdate[2..] : string.Empty;
        if (!string.IsNullOrEmpty(birthdate) && string.Equals(code, birthdate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.IsNullOrEmpty(_birthdate) && string.IsNullOrEmpty(code);
    }

    private bool IsDeleteBirthdateValid(string birthdate)
    {
        if (string.IsNullOrEmpty(_birthdate) && string.IsNullOrEmpty(birthdate))
        {
            return true;
        }

        if (_birthdate.Length < 2)
        {
            return false;
        }

        return string.Equals(_birthdate[2..], birthdate, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDeleteRestricted(CharConfig config, CharCharacter character)
    {
        if (IsDeleteLevelBlocked(config.CharDeleteLevel, character.BaseLevel))
        {
            return true;
        }

        if ((config.CharDeleteRestriction & CharDelRestrictGuild) != 0 && character.GuildId != 0)
        {
            return true;
        }

        if ((config.CharDeleteRestriction & CharDelRestrictParty) != 0 && character.PartyId != 0)
        {
            return true;
        }

        return false;
    }

    private static bool IsDeleteLevelBlocked(int limit, ushort baseLevel)
    {
        if (limit == 0)
        {
            return false;
        }

        if (limit > 0 && baseLevel >= limit)
        {
            return true;
        }

        return limit < 0 && baseLevel <= -limit;
    }

    private async Task<byte[]> ReadPacketAsync(short packetType, byte[] header, CancellationToken cancellationToken)
    {
        if (!PacketLengths.TryGetValue(packetType, out var length))
        {
            CharLogger.Warning($"Unknown char packet 0x{packetType:X4}, disconnecting.");
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
}
