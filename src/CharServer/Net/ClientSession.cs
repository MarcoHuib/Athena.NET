using System.Buffers.Binary;
using System.Collections.Concurrent;
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

public sealed class ClientSession : IDisposable, ISession
{
    internal const int CharacterInfoSize = 175;
    private const int CharDelEmail = 1;
    private const int CharDelBirthdate = 2;
    private const int CharDelRestrictParty = 1;
    private const int CharDelRestrictGuild = 2;
    private const int JobSummoner = 4218;
    private const int JobBabySummoner = 4220;
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
        [PacketConstants.ChReqIsValidCharName] = 34,
        [PacketConstants.ChReqChangeCharName] = 30,
        [PacketConstants.ChReqChangeCharacterSlot] = 8,
        [PacketConstants.ChAvailableSecondPassword] = 6,
        [PacketConstants.ChSecondPasswordAck] = 10,
        [PacketConstants.ChMakeSecondPassword] = 10,
        [PacketConstants.ChEditSecondPassword] = 14,
    };

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly CharConfigStore _configStore;
    private readonly LoginServerConnector _loginConnector;
    private readonly Func<CharDbContext?> _dbFactory;
    private readonly int _startStatusPoints;
    private readonly MapServerRegistry _mapRegistry;
    private readonly MapAuthManager _mapAuthManager;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private byte[]? _prefetchedHeader;
    private static readonly ConcurrentDictionary<uint, bool> PincodePassed = new();
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
    private string _pincode = string.Empty;
    private uint _pincodeChange;
    private int _pincodeTry;
    private uint _pincodeSeed;
    private bool _pincodeCorrect;
    private string _pendingRenameName = string.Empty;
    private IroCharacterListSyncState? _iroCharacterListSync;
    private int _announcedSyncCount;
    private bool _pincodePending;

    public ClientSession(int sessionId, TcpClient client, CharConfigStore configStore, LoginServerConnector loginConnector, Func<CharDbContext?> dbFactory, int startStatusPoints, MapServerRegistry mapRegistry, MapAuthManager mapAuthManager, byte[]? prefetchedHeader = null)
    {
        SessionId = sessionId;
        _client = client;
        _configStore = configStore;
        _loginConnector = loginConnector;
        _dbFactory = dbFactory;
        _startStatusPoints = startStatusPoints;
        _mapRegistry = mapRegistry;
        _mapAuthManager = mapAuthManager;
        _stream = client.GetStream();
        _charSlots = _configStore.Current.MinChars;
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
            CharLogger.Debug($"[iRO DEBUG] Char client packet=0x{packetType:X4} len={packet.Length}");

            await HandlePacketAsync(packetType, packet, cancellationToken);
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        _writeLock.Dispose();
        if (_accountId != 0)
        {
            PincodePassed.TryRemove(_accountId, out _);
        }
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

    public async Task HandleAccountDataAsync(uint accountId, byte charSlots, bool isVip, byte vipSlots, byte billingSlots, string email, string birthdate, string pincode, uint pincodeChange)
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
        _pincode = pincode;
        _pincodeChange = pincodeChange;
        _pincodeTry = 0;
        _pincodeCorrect = false;

        _pincodePending = true;
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
            case PacketConstants.ChSelectChar:
                await HandleSelectCharAsync(packet, cancellationToken);
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
                await HandleAccountCheckAsync(packet, cancellationToken);
                break;
            case PacketConstants.ChAvailableSecondPassword:
                await HandlePincodeWindowAsync(packet, cancellationToken);
                break;
            case PacketConstants.ChSecondPasswordAck:
                await HandlePincodeCheckAsync(packet, cancellationToken);
                break;
            case PacketConstants.ChEditSecondPassword:
                await HandlePincodeChangeAsync(packet, cancellationToken);
                break;
            case PacketConstants.ChMakeSecondPassword:
                await HandlePincodeSetAsync(packet, cancellationToken);
                break;
            case PacketConstants.ChReqIsValidCharName:
                await HandleRenameCheckAsync(packet, cancellationToken);
                break;
            case PacketConstants.ChReqChangeCharName:
                await HandleRenameApplyAsync(packet, cancellationToken);
                break;
            case PacketConstants.ChReqChangeCharacterSlot:
                await HandleMoveCharSlotAsync(packet, cancellationToken);
                break;
            default:
                CharLogger.Warning($"Unknown char packet 0x{packetType:X4}, disconnecting.");
                _client.Close();
                break;
        }
    }

    private async Task HandleConnectAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (packet.Length < 17)
        {
            return;
        }

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

    private async Task HandleAccountCheckAsync(byte[] packet, CancellationToken cancellationToken)
    {
        var accountId = ParseAccountCheck(packet);
        CharLogger.Debug("[iRO DEBUG] Received 0x0187 account check");
        if (accountId != _accountId)
        {
            CharLogger.Warning("Char keep-alive account mismatch.");
            _client.Close();
            return;
        }

        if (_configStore.Current.IroRenewalCompatibility)
        {
            var echo = BuildAccountCheckEcho(accountId);
            CharLogger.Debug("[iRO DEBUG] Sending 0x0187 account check echo");
            await WriteAsync(echo, cancellationToken);
        }
    }

    internal static uint ParseAccountCheck(ReadOnlySpan<byte> packet)
    {
        if (packet.Length != 6 || BinaryPrimitives.ReadInt16LittleEndian(packet[..2]) != PacketConstants.ChPing)
        {
            throw new ArgumentException("Char account check must be packet 0x0187 with length 6.", nameof(packet));
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(2, 4));
    }

    internal static byte[] BuildAccountCheckEcho(uint accountId)
    {
        var packet = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ChPing);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2, 4), accountId);
        return packet;
    }

    private async Task HandleSelectCharAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!_authenticated)
        {
            return;
        }
        if (packet.Length < 3)
        {
            return;
        }


        var config = _configStore.Current;
        if (config.PincodeEnabled && !string.IsNullOrEmpty(_pincode) && !_pincodeCorrect && !PincodePassed.ContainsKey(_accountId))
        {
            await SendRefuseEnterAsync(0, cancellationToken);
            return;
        }

        if (!_mapRegistry.TryGetAny(out var mapServer))
        {
            CharLogger.Warning("No map server available for character selection.");
            await SendRefuseEnterAsync(0, cancellationToken);
            return;
        }

        var slot = ParseCharacterSelect(packet);
        var db = _dbFactory();
        if (db == null)
        {
            await SendRefuseEnterAsync(0, cancellationToken);
            return;
        }

        var character = await db.Characters.FirstOrDefaultAsync(c => c.AccountId == _accountId && c.CharNum == slot && c.DeleteDate == 0, cancellationToken);
        if (character == null)
        {
            await SendRefuseEnterAsync(0, cancellationToken);
            return;
        }

        CharLogger.Debug($"[iRO DEBUG] Character select slot={slot} charId={character.CharId}");

        var mapName = string.IsNullOrWhiteSpace(character.LastMap) ? character.SaveMap : character.LastMap;
        if (string.IsNullOrWhiteSpace(mapName))
        {
            mapName = "prontera";
        }

        var node = new MapAuthNode(
            _accountId,
            character.CharId,
            _loginId1,
            _loginId2,
            _sex,
            mapName,
            character.LastX,
            character.LastY,
            character.BodyDirection,
            character.Font,
            0,
            0,
            false);

        _mapAuthManager.Add(node);
        await SendZoneServerAsync(character.CharId, mapName, mapServer, cancellationToken);
    }

    internal static byte ParseCharacterSelect(ReadOnlySpan<byte> packet)
    {
        if (packet.Length != 3 || BinaryPrimitives.ReadInt16LittleEndian(packet[..2]) != PacketConstants.ChSelectChar)
        {
            throw new ArgumentException("CH_SELECT_CHAR must be packet 0x0066 with length 3.", nameof(packet));
        }

        return packet[2];
    }

    private Task SendZoneServerAsync(uint charId, string mapName, MapServerInfo mapServer, CancellationToken cancellationToken)
    {
        if (_configStore.Current.IroRenewalCompatibility)
        {
            var config = _configStore.Current;
            var iroBuffer = BuildIroZoneServerPacket(
                charId, mapName, config.IroAdvertisedMapIp, config.IroAdvertisedMapPort);
            CharLogger.Debug(
                $"[iRO DEBUG] Sending 0x0071 map='{mapName}' " +
                $"advertisedEndpoint={config.IroAdvertisedMapIp}:{config.IroAdvertisedMapPort} packetLength={iroBuffer.Length}");
            return WriteAsync(iroBuffer, cancellationToken);
        }

        var length = 2 + 4 + PacketConstants.MapNameLength + 4 + 2 + PacketConstants.DomainLength;
        var buffer = new byte[length];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.HcNotifyZoneServer);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2, 4), charId);
        WriteFixedString(buffer.AsSpan(6, PacketConstants.MapNameLength), mapName);
        var ipBytes = mapServer.Ip.MapToIPv4().GetAddressBytes();
        ipBytes.CopyTo(buffer.AsSpan(22, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(26, 2), (ushort)mapServer.Port);
        return WriteAsync(buffer, cancellationToken);
    }

    internal static byte[] BuildIroZoneServerPacket(uint charId, string mapName, IPAddress ip, int port)
    {
        var buffer = new byte[28];
        BinaryPrimitives.WriteInt16LittleEndian(buffer, PacketConstants.IroHcNotifyZoneServer);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2, 4), charId);
        WriteFixedString(buffer.AsSpan(6, PacketConstants.MapNameLength), mapName);
        ip.MapToIPv4().GetAddressBytes().CopyTo(buffer.AsSpan(22, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(26, 2), (ushort)port);
        return buffer;
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
        _iroCharacterListSync = config.IroRenewalCompatibility
            ? new IroCharacterListSyncState(characters)
            : null;
        if (config.IroRenewalCompatibility)
        {
            CharLogger.Debug(
                $"[iRO DEBUG] Loaded characters count={characters.Count} " +
                $"slots=[{string.Join(',', characters.Select(character => character.CharNum))}]");
            foreach (var character in characters)
            {
                CharLogger.Debug($"[iRO DEBUG] Loaded character slot={character.CharNum}");
            }
        }
        await SendAcceptEnter2Async(config, cancellationToken);
        if (config.IroRenewalCompatibility)
        {
            CharLogger.Debug("[iRO DEBUG] Skipping legacy 0x006B for iRO");
        }
        else
        {
            await SendAcceptEnterAsync(config, characters, cancellationToken);
        }

        _announcedSyncCount = config.IroRenewalCompatibility
            ? PacketConstants.IroCharSyncCount
            : Math.Max((_charSlots + 2) / 3, 1);
        await SendCharListNotifyAsync(_announcedSyncCount, cancellationToken);
    }

    private async Task SendCharListPageAsync(CancellationToken cancellationToken)
    {
        if (!_authenticated)
        {
            return;
        }

        IReadOnlyList<CharCharacter> characters;
        if (_configStore.Current.IroRenewalCompatibility)
        {
            var sync = _iroCharacterListSync;
            if (sync == null)
            {
                return;
            }

            var responses = sync.HandleRequest();
            if (sync.RequestsReceived > _announcedSyncCount)
            {
                CharLogger.Warning(
                    $"[iRO DEBUG] Ignoring unexpected 0x09A1 after {_announcedSyncCount} sync requests");
                return;
            }

            if (responses.Count == 0)
            {
                CharLogger.Debug(
                    $"[iRO DEBUG] Received 0x09A1 sync request {sync.RequestsReceived}/{_announcedSyncCount}; " +
                    "character list already complete, ignoring");
                return;
            }

            CharLogger.Debug(
                $"[iRO DEBUG] Received 0x09A1 sync request {sync.RequestsReceived}/{_announcedSyncCount}");
            foreach (var response in responses)
            {
                var characterCount = (response.Length - 4) / CharacterInfoSize;
                var slots = Enumerable.Range(0, characterCount)
                    .Select(index => response[4 + (index * CharacterInfoSize) + CharacterInfoSlotOffset]);
                CharLogger.Debug(
                    $"[iRO DEBUG] Sending 0x{PacketConstants.HcAckCharInfoPerPage:X4} " +
                    $"characters={characterCount} characterInfoSize={CharacterInfoSize} " +
                    $"packetLength={response.Length} slots=[{string.Join(',', slots)}]");
                await WriteAsync(response, cancellationToken);
            }

            CharLogger.Debug("[iRO DEBUG] Character list data complete");
            if (_pincodePending)
            {
                _pincodePending = false;
                await SendPincodeStartAsync(cancellationToken);
            }
            return;
        }
        else
        {
            var db = _dbFactory();
            if (db == null)
            {
                return;
            }

            await using (db)
            {
                characters = await db.Characters
                    .AsNoTracking()
                    .Where(c => c.AccountId == _accountId)
                    .OrderBy(c => c.CharNum)
                    .ToListAsync(cancellationToken);
            }
        }

        var iroCompatibility = _configStore.Current.IroRenewalCompatibility;
        var payload = BuildCharacterInfoPayload(characters, iroCompatibility);
        var buffer = BuildCharacterPagePacket(payload);
        if (iroCompatibility)
        {
            for (var index = 0; index < characters.Count; index++)
            {
                var character = characters[index];
                var packetOffset = 4 + (index * CharacterInfoSize) + CharacterInfoSlotOffset;
                CharLogger.Debug(
                    $"[iRO DEBUG] 0x0B72 charId={character.CharId} slot={character.CharNum} " +
                    $"packetOffset={packetOffset} value={buffer[packetOffset]}");
            }
        }
        LogCharacterList(PacketConstants.HcAckCharInfoPerPage, characters.Count, buffer.Length);
        await WriteAsync(buffer, cancellationToken);

    }

    internal static IReadOnlyList<byte[]> BuildIroCharacterListResponses(
        IReadOnlyList<CharCharacter> characters)
    {
        var orderedCharacters = characters.OrderBy(character => character.CharNum).ToArray();
        var payload = BuildCharacterInfoPayload(orderedCharacters);
        var responses = new List<byte[]> { BuildCharacterPagePacket(payload) };

        // Current rAthena mirrors Gravity's special finalization behavior: when the
        // data response contains exactly three characters, an empty response follows
        // immediately so the client executes its character-list finalization path.
        if (orderedCharacters.Length == 3)
        {
            responses.Add(BuildCharacterPagePacket(ReadOnlySpan<byte>.Empty));
        }

        return responses;
    }

    internal static byte[] BuildCharacterPagePacket(ReadOnlySpan<byte> characterInfo)
    {
        var buffer = new byte[4 + characterInfo.Length];
        BinaryPrimitives.WriteInt16LittleEndian(buffer, PacketConstants.HcAckCharInfoPerPage);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(2, 2), (short)buffer.Length);
        characterInfo.CopyTo(buffer.AsSpan(4));
        return buffer;
    }

    private async Task SendAcceptEnter2Async(CharConfig config, CancellationToken cancellationToken)
    {
        var buffer = BuildAcceptEnter2Packet(config, _charsVip, _charsBilling, _charSlots);

        CharLogger.Debug(
            $"[iRO DEBUG] Sending 0x{PacketConstants.HcAcceptEnter2:X4} normal={buffer[4]} " +
            $"premium={buffer[5]} billing={buffer[6]} producible={buffer[7]} valid={buffer[8]}");
        await WriteAsync(buffer, cancellationToken);
    }

    internal static byte[] BuildAcceptEnter2Packet(CharConfig config, byte vipSlots, byte billingSlots, int charSlots)
    {
        var buffer = new byte[29];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.HcAcceptEnter2);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(2, 2), 29);
        if (config.IroRenewalCompatibility)
        {
            buffer[4] = 9;
            buffer[5] = 9;
            buffer[6] = 0;
            buffer[7] = 9;
            buffer[8] = 9;
        }
        else
        {
            buffer[4] = (byte)Math.Clamp(config.MinChars, 0, byte.MaxValue);
            buffer[5] = vipSlots;
            buffer[6] = billingSlots;
            buffer[7] = (byte)Math.Clamp(charSlots, 0, byte.MaxValue);
            buffer[8] = (byte)Math.Clamp(config.MaxChars, 0, byte.MaxValue);
        }
        return buffer;
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

        LogCharacterList(PacketConstants.HcAcceptEnter, characters.Count, length);
        await WriteAsync(buffer, cancellationToken);
    }

    private async Task SendCharListNotifyAsync(int syncCount, CancellationToken cancellationToken)
    {
        var buffer = BuildCharListNotifyPacket(syncCount);
        CharLogger.Debug(
            $"[iRO DEBUG] Sending 0x{PacketConstants.HcCharListNotify:X4} syncCount={syncCount} packetLength={buffer.Length}");
        await WriteAsync(buffer, cancellationToken);
    }

    internal static byte[] BuildCharListNotifyPacket(int syncCount)
    {
        var buffer = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.HcCharListNotify);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2, 4), (uint)Math.Max(syncCount, 1));
        return buffer;
    }

    private static void LogCharacterList(short packetType, int characterCount, int packetLength)
    {
        CharLogger.Debug(
            $"[iRO DEBUG] Sending character list packet=0x{packetType:X4} " +
            $"characters={characterCount} characterInfoSize={CharacterInfoSize} packetLength={packetLength}");
    }

    private async Task HandleMakeCharAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!_authenticated)
        {
            await RejectMakeCharAsync(CharacterCreateFailure.Denied, "session is not authenticated", cancellationToken);
            return;
        }

        if (packet.Length < 36)
        {
            await RejectMakeCharAsync(
                CharacterCreateFailure.InvalidInput,
                $"invalid packet length len={packet.Length}",
                cancellationToken);
            return;
        }

        var config = _configStore.Current;
        if (!config.CharNew)
        {
            await RejectMakeCharAsync(CharacterCreateFailure.Denied, "character creation is disabled", cancellationToken);
            return;
        }

        var request = ParseIroCharacterCreate(packet);
        var name = NormalizeName(request.Name);
        var slot = request.Slot;
        var hairColor = request.HairColor;
        var hairStyle = request.HairStyle;
        var job = request.Job;
        var sex = request.Sex;

        CharLogger.Debug(
            $"[iRO DEBUG] Character create request packet=0x{PacketConstants.ChMakeChar:X4} len={packet.Length}");
        CharLogger.Debug(
            $"[iRO DEBUG] Character create name='{name}' slot={slot} hairColor={hairColor} " +
            $"hairStyle={hairStyle} job={job} sex={sex}");
        CharLogger.Debug($"[iRO DEBUG] Character create bytes={Convert.ToHexString(packet)}");

        if (string.IsNullOrWhiteSpace(name))
        {
            await RejectMakeCharAsync(CharacterCreateFailure.InvalidInput, "invalid name", cancellationToken);
            return;
        }

        if (slot >= _charSlots)
        {
            await RejectMakeCharAsync(CharacterCreateFailure.InvalidSlot, $"invalid slot slot={slot}", cancellationToken);
            return;
        }

        if (sex is not 0 and not 1)
        {
            await RejectMakeCharAsync(CharacterCreateFailure.InvalidInput, $"invalid sex sex={sex}", cancellationToken);
            return;
        }

        if (job != 0 && job != JobSummoner && job != JobBabySummoner)
        {
            await RejectMakeCharAsync(CharacterCreateFailure.InvalidInput, $"invalid job job={job}", cancellationToken);
            return;
        }

        var nameValidation = await ValidateCharNameAsync(name, cancellationToken);
        CharLogger.Debug(
            $"[iRO DEBUG] Name lookup '{name}': exists={(nameValidation == NameValidationResult.Exists).ToString().ToLowerInvariant()}");
        if (nameValidation != NameValidationResult.Ok)
        {
            var failure = nameValidation switch
            {
                NameValidationResult.Exists => CharacterCreateFailure.NameTaken,
                NameValidationResult.DatabaseError => CharacterCreateFailure.DatabaseError,
                _ => CharacterCreateFailure.InvalidInput,
            };
            var detail = nameValidation switch
            {
                NameValidationResult.Exists => "name already exists",
                NameValidationResult.DatabaseError => "name lookup database unavailable",
                _ => "invalid name",
            };
            await RejectMakeCharAsync(failure, detail, cancellationToken);
            return;
        }

        var db = _dbFactory();
        if (db == null)
        {
            await RejectMakeCharAsync(CharacterCreateFailure.DatabaseError, "database unavailable", cancellationToken);
            return;
        }

        await using (db)
        {
            var existingCharacters = await db.Characters
                .AsNoTracking()
                .Where(c => c.AccountId == _accountId)
                .Select(c => new CharCharacter
                {
                    CharId = c.CharId,
                    Name = c.Name,
                    CharNum = c.CharNum,
                })
                .ToListAsync(cancellationToken);

            var slotCharacter = existingCharacters.FirstOrDefault(c => c.CharNum == slot);
            CharLogger.Debug(
                $"[iRO DEBUG] Slot lookup slot={slot}: occupied={(slotCharacter != null).ToString().ToLowerInvariant()}" +
                (slotCharacter == null ? string.Empty : $" charId={slotCharacter.CharId}"));

            var validationFailure = DetermineCharacterCreateFailure(
                nameValidation, existingCharacters, slot, _charSlots);
            if (validationFailure == CharacterCreateFailure.AccountLimitReached)
            {
                await RejectMakeCharAsync(
                    validationFailure.Value,
                    $"account character limit reached count={existingCharacters.Count} limit={_charSlots}",
                    cancellationToken);
                return;
            }

            if (validationFailure == CharacterCreateFailure.SlotOccupied)
            {
                await RejectMakeCharAsync(
                    validationFailure.Value,
                    $"slot occupied slot={slot} charId={slotCharacter!.CharId}",
                    cancellationToken);
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
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception)
            {
                CharLogger.Error($"Character create database constraint failure: {exception.GetType().Name}");
                await RejectMakeCharAsync(
                    CharacterCreateFailure.DatabaseError,
                    $"database constraint {exception.GetType().Name}",
                    cancellationToken);
                return;
            }
            CharLogger.Debug($"[iRO DEBUG] Character created charId={character.CharId}");

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

    internal static CharacterCreateRequest ParseIroCharacterCreate(ReadOnlySpan<byte> packet)
    {
        if (packet.Length != 36 || BinaryPrimitives.ReadInt16LittleEndian(packet[..2]) != PacketConstants.ChMakeChar)
        {
            throw new ArgumentException("iRO CH_MAKE_CHAR must be packet 0x0A39 with length 36.", nameof(packet));
        }

        return new CharacterCreateRequest(
            ReadFixedString(packet.Slice(2, 24)),
            packet[26],
            BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(27, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(29, 2)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(31, 4)),
            packet[35]);
    }

    internal static CharacterCreateFailure? DetermineCharacterCreateFailure(
        NameValidationResult nameValidation,
        IReadOnlyList<CharCharacter> existingCharacters,
        byte slot,
        int availableSlots)
    {
        if (nameValidation == NameValidationResult.Exists)
        {
            return CharacterCreateFailure.NameTaken;
        }

        if (nameValidation != NameValidationResult.Ok)
        {
            return CharacterCreateFailure.InvalidInput;
        }

        if (slot >= availableSlots)
        {
            return CharacterCreateFailure.InvalidSlot;
        }

        if (existingCharacters.Count >= availableSlots)
        {
            return CharacterCreateFailure.AccountLimitReached;
        }

        return existingCharacters.Any(character => character.CharNum == slot)
            ? CharacterCreateFailure.SlotOccupied
            : null;
    }

    internal static bool IsCharacterNameTaken(
        IEnumerable<string> existingNames,
        string requestedName,
        bool nameIgnoringCase)
    {
        var comparison = nameIgnoringCase
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        return existingNames.Any(existingName =>
            string.Equals(existingName, requestedName, comparison));
    }

    private async Task HandleDeleteCharAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!_authenticated)
        {
            await SendRefuseDeleteCharAsync(cancellationToken);
            return;
        }

        if (packet.Length < 56)
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

        if (packet.Length < 6)
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

        if (packet.Length < 12)
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

    private async Task HandlePincodeWindowAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!_authenticated)
        {
            return;
        }

        if (packet.Length < 6)
        {
            return;
        }

        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        if (accountId != _accountId)
        {
            return;
        }

        await SendPincodeStartAsync(cancellationToken);
    }

    private async Task HandlePincodeCheckAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!_authenticated)
        {
            return;
        }

        if (packet.Length < 10)
        {
            _client.Close();
            return;
        }

        var config = _configStore.Current;
        if (!config.PincodeEnabled)
        {
            _client.Close();
            return;
        }

        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        if (accountId != _accountId)
        {
            _client.Close();
            return;
        }

        var pin = ReadFixedString(packet.AsSpan(6, 4));
        var decrypted = DecryptPincode(_pincodeSeed, pin);
        if (decrypted == null)
        {
            _client.Close();
            return;
        }

        if (string.Equals(_pincode, decrypted, StringComparison.Ordinal))
        {
            _pincodeTry = 0;
            _pincodeCorrect = true;
            PincodePassed[_accountId] = true;
            await SendPincodeStateAsync(PincodeState.Passed, cancellationToken);
            return;
        }

        _pincodeTry += 1;
        await SendPincodeStateAsync(PincodeState.Wrong, cancellationToken);

        if (config.PincodeMaxTry > 0 && _pincodeTry >= config.PincodeMaxTry)
        {
            _loginConnector.TrySendPincodeAuthFail(_accountId);
        }
    }

    private async Task HandlePincodeChangeAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!_authenticated)
        {
            return;
        }

        if (packet.Length < 14)
        {
            _client.Close();
            return;
        }

        var config = _configStore.Current;
        if (!config.PincodeEnabled)
        {
            _client.Close();
            return;
        }

        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        if (accountId != _accountId)
        {
            _client.Close();
            return;
        }

        var oldPin = ReadFixedString(packet.AsSpan(6, 4));
        var newPin = ReadFixedString(packet.AsSpan(10, 4));
        var decryptedOld = DecryptPincode(_pincodeSeed, oldPin);
        var decryptedNew = DecryptPincode(_pincodeSeed, newPin);
        if (decryptedOld == null || decryptedNew == null)
        {
            _client.Close();
            return;
        }

        if (!string.Equals(_pincode, decryptedOld, StringComparison.Ordinal))
        {
            _pincodeTry += 1;
            await SendPincodeStateAsync(PincodeState.Wrong, cancellationToken);

            if (config.PincodeMaxTry > 0 && _pincodeTry >= config.PincodeMaxTry)
            {
                _loginConnector.TrySendPincodeAuthFail(_accountId);
            }
            return;
        }

        if (!IsPincodeAllowed(config, decryptedNew))
        {
            await SendPincodeStateAsync(PincodeState.Illegal, cancellationToken);
            return;
        }

        _loginConnector.TrySendPincodeUpdate(_accountId, decryptedNew);
        _pincode = decryptedNew;
        _pincodeCorrect = true;
        PincodePassed[_accountId] = true;
        _pincodeChange = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _pincodeTry = 0;
        await SendPincodeStateAsync(PincodeState.Passed, cancellationToken);
    }

    private async Task HandlePincodeSetAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!_authenticated)
        {
            return;
        }

        if (packet.Length < 10)
        {
            _client.Close();
            return;
        }

        var config = _configStore.Current;
        if (!config.PincodeEnabled)
        {
            _client.Close();
            return;
        }

        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        if (accountId != _accountId)
        {
            _client.Close();
            return;
        }

        var newPin = ReadFixedString(packet.AsSpan(6, 4));
        var decryptedNew = DecryptPincode(_pincodeSeed, newPin);
        if (decryptedNew == null)
        {
            _client.Close();
            return;
        }

        if (!IsPincodeAllowed(config, decryptedNew))
        {
            await SendPincodeStateAsync(PincodeState.Illegal, cancellationToken);
            return;
        }

        _loginConnector.TrySendPincodeUpdate(_accountId, decryptedNew);
        _pincode = decryptedNew;
        _pincodeCorrect = true;
        PincodePassed[_accountId] = true;
        _pincodeChange = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _pincodeTry = 0;
        await SendPincodeStateAsync(PincodeState.Passed, cancellationToken);
    }

    private async Task HandleRenameCheckAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!_authenticated)
        {
            return;
        }

        if (packet.Length < 34)
        {
            await SendRenameCheckAsync(false, cancellationToken);
            return;
        }

        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        if (accountId != _accountId)
        {
            return;
        }

        var charId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(6, 4));
        var name = ReadFixedString(packet.AsSpan(10, 24));
        var normalized = NormalizeName(name);
        var db = _dbFactory();
        if (db == null)
        {
            await SendRenameCheckAsync(false, cancellationToken);
            return;
        }

        await using (db)
        {
            var exists = await db.Characters
                .AnyAsync(c => c.CharId == charId && c.AccountId == _accountId, cancellationToken);
            if (!exists)
            {
                await SendRenameCheckAsync(false, cancellationToken);
                return;
            }
        }

        var validation = await ValidateCharNameAsync(normalized, cancellationToken);
        if (validation == NameValidationResult.Ok)
        {
            _pendingRenameName = normalized;
            await SendRenameCheckAsync(true, cancellationToken);
            return;
        }

        await SendRenameCheckAsync(false, cancellationToken);
    }

    private async Task HandleRenameApplyAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!_authenticated)
        {
            return;
        }

        if (packet.Length < 30)
        {
            await SendRenameResultAsync(2, cancellationToken);
            return;
        }

        var charId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        var newName = ReadFixedString(packet.AsSpan(6, 24));
        var normalized = NormalizeName(newName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = _pendingRenameName;
        }

        _pendingRenameName = string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            await SendRenameResultAsync(2, cancellationToken);
            return;
        }

        var db = _dbFactory();
        if (db == null)
        {
            await SendRenameResultAsync(3, cancellationToken);
            return;
        }

        var config = _configStore.Current;
        await using (db)
        {
            var character = await db.Characters
                .FirstOrDefaultAsync(c => c.CharId == charId && c.AccountId == _accountId, cancellationToken);
            if (character == null)
            {
                await SendRenameResultAsync(2, cancellationToken);
                return;
            }

            if (!string.IsNullOrEmpty(normalized) && string.Equals(character.Name, normalized, StringComparison.Ordinal))
            {
                await SendRenameResultAsync(0, cancellationToken);
                return;
            }

            if (character.Rename == 0)
            {
                await SendRenameResultAsync(1, cancellationToken);
                return;
            }

            if (!config.CharRenameParty && character.PartyId != 0)
            {
                await SendRenameResultAsync(6, cancellationToken);
                return;
            }

            if (!config.CharRenameGuild && character.GuildId != 0)
            {
                await SendRenameResultAsync(5, cancellationToken);
                return;
            }

            var validation = await ValidateCharNameAsync(normalized, cancellationToken);
            if (validation == NameValidationResult.Exists)
            {
                await SendRenameResultAsync(4, cancellationToken);
                return;
            }

            if (validation != NameValidationResult.Ok)
            {
                await SendRenameResultAsync(8, cancellationToken);
                return;
            }

            character.Name = normalized;
            character.Rename = (ushort)Math.Max(0, character.Rename - 1);
            await db.SaveChangesAsync(cancellationToken);
        }

        await SendRenameResultAsync(0, cancellationToken);
        await SendCharListAsync(cancellationToken);
    }

    private async Task HandleMoveCharSlotAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!_authenticated)
        {
            return;
        }

        if (packet.Length < 6)
        {
            await SendMoveCharSlotAckAsync(1, 0, cancellationToken);
            return;
        }

        var fromSlot = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2, 2));
        var toSlot = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(4, 2));
        var config = _configStore.Current;

        if (fromSlot >= config.MaxChars)
        {
            await SendMoveCharSlotAckAsync(1, 0, cancellationToken);
            return;
        }

        if (!config.CharMoveEnabled)
        {
            await SendMoveCharSlotAckAsync(1, 0, cancellationToken);
            return;
        }

        if (toSlot >= _charSlots)
        {
            await SendMoveCharSlotAckAsync(1, 0, cancellationToken);
            return;
        }

        var db = _dbFactory();
        if (db == null)
        {
            await SendMoveCharSlotAckAsync(1, 0, cancellationToken);
            return;
        }

        await using (db)
        {
            var characters = await db.Characters
                .Where(c => c.AccountId == _accountId)
                .ToListAsync(cancellationToken);

            var fromChar = characters.FirstOrDefault(c => c.CharNum == fromSlot);
            if (fromChar == null)
            {
                await SendMoveCharSlotAckAsync(1, 0, cancellationToken);
                return;
            }

            var remainingMoves = (ushort)Math.Min(fromChar.Moves, ushort.MaxValue);
            if (!config.CharMovesUnlimited && remainingMoves == 0)
            {
                await SendMoveCharSlotAckAsync(1, remainingMoves, cancellationToken);
                return;
            }

            var toChar = characters.FirstOrDefault(c => c.CharNum == toSlot);
            if (toChar != null)
            {
                if (!config.CharMoveToUsed)
                {
                    await SendMoveCharSlotAckAsync(1, remainingMoves, cancellationToken);
                    return;
                }

                var temp = fromChar.CharNum;
                fromChar.CharNum = toChar.CharNum;
                toChar.CharNum = temp;
            }
            else
            {
                fromChar.CharNum = (byte)Math.Clamp((int)toSlot, 0, byte.MaxValue);
            }

            if (!config.CharMovesUnlimited && fromChar.Moves > 0)
            {
                fromChar.Moves -= 1;
            }

            await db.SaveChangesAsync(cancellationToken);

            remainingMoves = (ushort)Math.Min(fromChar.Moves, ushort.MaxValue);
            await SendMoveCharSlotAckAsync(0, remainingMoves, cancellationToken);
        }

        await SendCharListAsync(cancellationToken);
    }

    private Task RejectMakeCharAsync(
        CharacterCreateFailure failure,
        string detail,
        CancellationToken cancellationToken)
    {
        CharLogger.Debug($"[iRO DEBUG] Character create rejected: {detail}");
        return SendRefuseMakeCharAsync(failure, cancellationToken);
    }

    private Task SendRefuseMakeCharAsync(
        CharacterCreateFailure failure,
        CancellationToken cancellationToken)
    {
        var reason = GetCharacterCreateFailureWireReason(failure);
        var buffer = BuildRefuseMakeCharPacket(reason);
        CharLogger.Debug(
            $"[iRO DEBUG] Sending character creation failure packet=0x{PacketConstants.HcRefuseMakeChar:X4} " +
            $"reason={buffer[2]} packetLength={buffer.Length}");
        return WriteAsync(buffer, cancellationToken);
    }

    internal static byte GetCharacterCreateFailureWireReason(CharacterCreateFailure failure)
    {
        return failure switch
        {
            CharacterCreateFailure.NameTaken => 0x00,
            CharacterCreateFailure.InvalidSlot => 0x03,
            _ => 0xff,
        };
    }

    internal static byte[] BuildRefuseMakeCharPacket(byte reason)
    {
        var buffer = new byte[3];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.HcRefuseMakeChar);
        buffer[2] = reason;
        return buffer;
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
        var payload = BuildCharacterInfoPayload(
            new[] { character }, _configStore.Current.IroRenewalCompatibility);
        var buffer = BuildAcceptMakeCharPacket(payload);
        CharLogger.Debug(
            $"[iRO DEBUG] Sending character creation success packet=0x{PacketConstants.HcAcceptMakeChar:X4} " +
            $"characterInfoSize={CharacterInfoSize} packetLength={buffer.Length}");
        return WriteAsync(buffer, cancellationToken);
    }

    internal static byte[] BuildAcceptMakeCharPacket(ReadOnlySpan<byte> characterInfo)
    {
        var buffer = new byte[2 + characterInfo.Length];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.HcAcceptMakeChar);
        characterInfo.CopyTo(buffer.AsSpan(2));
        return buffer;
    }

    private Task SendRenameCheckAsync(bool isValid, CancellationToken cancellationToken)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.HcAckIsValidCharName);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(2, 2), isValid ? (ushort)1 : (ushort)0);
        return WriteAsync(buffer, cancellationToken);
    }

    private Task SendRenameResultAsync(int result, CancellationToken cancellationToken)
    {
        var buffer = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.HcAckChangeCharName);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2, 4), (uint)Math.Max(0, result));
        return WriteAsync(buffer, cancellationToken);
    }

    private Task SendMoveCharSlotAckAsync(ushort reason, ushort moves, CancellationToken cancellationToken)
    {
        var buffer = new byte[8];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.HcAckChangeCharacterSlot);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(2, 2), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4, 2), reason);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6, 2), moves);
        return WriteAsync(buffer, cancellationToken);
    }

    private async Task SendPincodeStartAsync(CancellationToken cancellationToken)
    {
        var config = _configStore.Current;
        CharLogger.Debug(
            $"[iRO DEBUG] PIN start effective enabled={config.PincodeEnabled.ToString().ToLowerInvariant()} " +
            $"force={config.PincodeForce.ToString().ToLowerInvariant()} hasPin={!string.IsNullOrEmpty(_pincode)} " +
            $"pinCorrect={_pincodeCorrect}");

        var state = DeterminePincodeStartState(
            config,
            _pincode,
            _pincodeChange,
            _pincodeCorrect,
            PincodePassed.ContainsKey(_accountId),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (state == null)
        {
            CharLogger.Debug("[iRO DEBUG] PIN disabled; skipping 0x08B9");
            return;
        }

        await SendPincodeStateAsync(state.Value, cancellationToken);
    }

    internal static PincodeState? DeterminePincodeStartState(
        CharConfig config,
        string pincode,
        uint pincodeChange,
        bool pincodeCorrect,
        bool pincodePassed,
        long now)
    {
        if (!config.PincodeEnabled)
        {
            return config.IroRenewalCompatibility ? null : PincodeState.Ok;
        }

        if (string.IsNullOrEmpty(pincode))
        {
            return config.PincodeForce ? PincodeState.New : PincodeState.Passed;
        }

        if (config.PincodeChangeTimeSeconds > 0 && pincodeChange > 0 &&
            pincodeChange + config.PincodeChangeTimeSeconds <= now)
        {
            return PincodeState.Expired;
        }

        if (pincodeCorrect || pincodePassed)
        {
            return PincodeState.Passed;
        }

        return PincodeState.Ask;
    }

    private Task SendPincodeStateAsync(PincodeState state, CancellationToken cancellationToken)
    {
        _pincodeSeed = (uint)Random.Shared.Next(0, 0x10000);
        var buffer = BuildPincodeStatePacket(state, _pincodeSeed, _accountId);
        CharLogger.Debug(
            $"[iRO DEBUG] Sending 0x{PacketConstants.HcSecondPasswordLogin:X4} state={(ushort)state} " +
            $"packetLength={buffer.Length}");
        return WriteAsync(buffer, cancellationToken);
    }

    internal static byte[] BuildPincodeStatePacket(PincodeState state, uint seed, uint accountId)
    {
        var buffer = new byte[12];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.HcSecondPasswordLogin);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2, 4), seed);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(6, 4), accountId);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10, 2), (ushort)state);
        return buffer;
    }

    internal const int CharacterInfoSlotOffset = 138;

    private static byte[] BuildCharacterInfoPayload(
        IReadOnlyList<CharCharacter> characters,
        bool iroDebug = false)
    {
        if (characters.Count == 0)
        {
            return Array.Empty<byte>();
        }

        var payload = new byte[characters.Count * CharacterInfoSize];
        var offset = 0;
        foreach (var character in characters)
        {
            var characterInfo = payload.AsSpan(offset, CharacterInfoSize);
            WriteCharacterInfo(characterInfo, character);
            if (iroDebug)
            {
                CharLogger.Debug(
                    $"[iRO DEBUG] CHARACTER_INFO charId={character.CharId} name='{character.Name}' " +
                    $"dbSlot={character.CharNum} modelSlot={character.CharNum} " +
                    $"serializedSlot={characterInfo[CharacterInfoSlotOffset]} slotOffset={CharacterInfoSlotOffset}");
                CharLogger.Debug(
                    $"[iRO DEBUG] CHARACTER_INFO tail132_145=" +
                    Convert.ToHexString(characterInfo.Slice(132, 14)));
            }
            offset += CharacterInfoSize;
        }

        return payload;
    }

    internal static void WriteCharacterInfo(Span<byte> buffer, CharCharacter character)
    {
        if (buffer.Length != CharacterInfoSize)
        {
            throw new ArgumentException($"iRO CHARACTER_INFO2 must be exactly {CharacterInfoSize} bytes.", nameof(buffer));
        }

        buffer.Clear();

        var weaponValue =
            (character.Option & WeaponHiddenOptionMask) != 0
                ? (ushort)0
                : character.Weapon;

        // 0..3 - Character ID
        BinaryPrimitives.WriteUInt32LittleEndian(
            buffer.Slice(0, 4),
            character.CharId);

        // 4..11 - Base EXP (64-bit)
        BinaryPrimitives.WriteUInt64LittleEndian(
            buffer.Slice(4, 8),
            character.BaseExp);

        // 12..15 - Zeny
        BinaryPrimitives.WriteUInt32LittleEndian(
            buffer.Slice(12, 4),
            character.Zeny);

        // 16..23 - Job EXP (64-bit)
        BinaryPrimitives.WriteUInt64LittleEndian(
            buffer.Slice(16, 8),
            character.JobExp);

        // 24..27 - Job level
        BinaryPrimitives.WriteInt32LittleEndian(
            buffer.Slice(24, 4),
            character.JobLevel);

        // 28..31 - body state
        BinaryPrimitives.WriteInt32LittleEndian(
            buffer.Slice(28, 4),
            0);

        // 32..35 - health state
        BinaryPrimitives.WriteInt32LittleEndian(
            buffer.Slice(32, 4),
            0);

        // 36..39 - option/effect state
        BinaryPrimitives.WriteInt32LittleEndian(
            buffer.Slice(36, 4),
            (int)character.Option);

        // 40..43 - karma
        BinaryPrimitives.WriteInt32LittleEndian(
            buffer.Slice(40, 4),
            character.Karma);

        // 44..47 - manner
        BinaryPrimitives.WriteInt32LittleEndian(
            buffer.Slice(44, 4),
            character.Manner);

        // 48..49 - status points
        BinaryPrimitives.WriteInt16LittleEndian(
            buffer.Slice(48, 2),
            (short)Math.Min(character.StatusPoint, short.MaxValue));

        // 50..57 - HP
        BinaryPrimitives.WriteUInt64LittleEndian(
            buffer.Slice(50, 8),
            character.Hp);

        // 58..65 - Max HP
        BinaryPrimitives.WriteUInt64LittleEndian(
            buffer.Slice(58, 8),
            character.MaxHp);

        // 66..73 - SP
        BinaryPrimitives.WriteUInt64LittleEndian(
            buffer.Slice(66, 8),
            character.Sp);

        // 74..81 - Max SP
        BinaryPrimitives.WriteUInt64LittleEndian(
            buffer.Slice(74, 8),
            character.MaxSp);

        // 82..83 - Walk speed
        BinaryPrimitives.WriteInt16LittleEndian(
            buffer.Slice(82, 2),
            150);

        // 84..85 - Job/class
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.Slice(84, 2),
            character.Class);

        // 86..87 - Hair style
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.Slice(86, 2),
            character.Hair);

        // 88..89 - Body style. Modern (2023-12-20+) CHARACTER_INFO carries the value directly.
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(88, 2), character.Body);

        // 90..91 - Weapon
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.Slice(90, 2),
            weaponValue);

        // 92..93 - Base level
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.Slice(92, 2),
            character.BaseLevel);

        // 94..95 - Skill points
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.Slice(94, 2),
            (ushort)Math.Min(character.SkillPoint, ushort.MaxValue));

        // 96..107 - Equipment and palettes
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.Slice(96, 2),
            character.HeadBottom);
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.Slice(98, 2),
            character.Shield);
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.Slice(100, 2),
            character.HeadTop);
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.Slice(102, 2),
            character.HeadMid);
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.Slice(104, 2),
            character.HairColor);
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.Slice(106, 2),
            character.ClothesColor);

        // 108..131 - Character name
        WriteFixedString(
            buffer.Slice(108, 24),
            character.Name);

        // 132..137 - Stats
        buffer[132] = (byte)Math.Min(character.Str, byte.MaxValue);
        buffer[133] = (byte)Math.Min(character.Agi, byte.MaxValue);
        buffer[134] = (byte)Math.Min(character.Vit, byte.MaxValue);
        buffer[135] = (byte)Math.Min(character.Int, byte.MaxValue);
        buffer[136] = (byte)Math.Min(character.Dex, byte.MaxValue);
        buffer[137] = (byte)Math.Min(character.Luk, byte.MaxValue);

        // 138 - Character slot
        buffer[CharacterInfoSlotOffset] = character.CharNum;

        // 139 - Hair color byte
        buffer[139] =
            (byte)Math.Min(character.HairColor, byte.MaxValue);

        // Rename flag
        var renameFlag = character.Rename > 0 ? 0 : 1;

        BinaryPrimitives.WriteInt16LittleEndian(
            buffer.Slice(140, 2),
            (short)renameFlag);

        // 142..157 - Last map
        WriteFixedString(
            buffer.Slice(142, 16),
            character.LastMap);

        // Delete time
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var deleteRemaining =
            character.DeleteDate > now
                ? (int)Math.Min(
                    character.DeleteDate - now,
                    int.MaxValue)
                : 0;

        BinaryPrimitives.WriteInt32LittleEndian(
            buffer.Slice(158, 4),
            deleteRemaining);

        // Robe
        BinaryPrimitives.WriteInt32LittleEndian(
            buffer.Slice(162, 4),
            character.Robe);

        // Character slot moves
        BinaryPrimitives.WriteInt32LittleEndian(
            buffer.Slice(166, 4),
            (int)Math.Min(character.Moves, int.MaxValue));

        // Rename count
        var nameChangeCount =
            character.Rename > 0 ? 1 : 0;

        BinaryPrimitives.WriteInt32LittleEndian(
            buffer.Slice(170, 4),
            nameChangeCount);

        // 174 - Sex
        buffer[174] =
            character.Sex.Equals(
                "M",
                StringComparison.OrdinalIgnoreCase)
                ? (byte)1
                : (byte)0;
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

    private static string NormalizeName(string name)
    {
        return (name ?? string.Empty).Trim();
    }

    private async Task<NameValidationResult> ValidateCharNameAsync(string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return NameValidationResult.Invalid;
        }

        var config = _configStore.Current;
        if (name.Length < config.CharNameMinLength)
        {
            return NameValidationResult.Invalid;
        }

        if (ContainsControlChars(name))
        {
            return NameValidationResult.Invalid;
        }

        if (!string.IsNullOrEmpty(config.WispServerName) &&
            string.Equals(name, config.WispServerName, StringComparison.OrdinalIgnoreCase))
        {
            return NameValidationResult.Exists;
        }

        if (name.StartsWith("#", StringComparison.Ordinal))
        {
            return NameValidationResult.Invalid;
        }

        if (!IsNameAllowed(config, name))
        {
            return NameValidationResult.Invalid;
        }

        var db = _dbFactory();
        if (db == null)
        {
            return NameValidationResult.DatabaseError;
        }

        await using (db)
        {
            if (config.NameIgnoringCase)
            {
                var normalizedName = name.ToLowerInvariant();
                var matches = await db.Characters
                    .Where(c => c.Name.ToLower() == normalizedName)
                    .Select(c => c.Name)
                    .ToListAsync(cancellationToken);
                return matches.Any(match => string.Equals(match, name, StringComparison.Ordinal))
                    ? NameValidationResult.Exists
                    : NameValidationResult.Ok;
            }

            var normalizedLookup = name.ToLowerInvariant();
            var existsInsensitive = await db.Characters
                .AnyAsync(c => c.Name.ToLower() == normalizedLookup, cancellationToken);
            return existsInsensitive ? NameValidationResult.Exists : NameValidationResult.Ok;
        }
    }

    private static bool ContainsControlChars(string name)
    {
        foreach (var ch in name)
        {
            if (char.IsControl(ch))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNameAllowed(CharConfig config, string name)
    {
        if (config.CharNameOption == 1)
        {
            foreach (var ch in name)
            {
                if (!config.CharNameLetters.Contains(ch))
                {
                    return false;
                }
            }

            return true;
        }

        if (config.CharNameOption == 2)
        {
            foreach (var ch in name)
            {
                if (config.CharNameLetters.Contains(ch))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsPincodeAllowed(CharConfig config, string pincode)
    {
        if (pincode.Length != 4)
        {
            return false;
        }

        foreach (var ch in pincode)
        {
            if (ch < '0' || ch > '9')
            {
                return false;
            }
        }

        if (!config.PincodeAllowRepeated)
        {
            if (pincode[0] == pincode[1] &&
                pincode[0] == pincode[2] &&
                pincode[0] == pincode[3])
            {
                return false;
            }
        }

        if (!config.PincodeAllowSequential)
        {
            if (IsSequential(pincode, 1) || IsSequential(pincode, -1))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSequential(string pin, int step)
    {
        var current = pin[0];
        for (var i = 1; i < pin.Length; i++)
        {
            var next = (char)(current + step);
            if (next > '9')
            {
                next = (char)('0' + (next - '9' - 1));
            }
            else if (next < '0')
            {
                next = (char)('9' - ('0' - next) + 1);
            }

            if (pin[i] != next)
            {
                return false;
            }

            current = next;
        }

        return true;
    }

    private static string? DecryptPincode(uint seed, string pin)
    {
        if (pin.Length != 4)
        {
            return null;
        }

        foreach (var ch in pin)
        {
            if (ch < '0' || ch > '9')
            {
                return null;
            }
        }

        var tab = new int[10];
        for (var i = 0; i < tab.Length; i++)
        {
            tab[i] = i;
        }

        for (var i = 1; i < 10; i++)
        {
            const uint multiplier = 0x3498;
            const uint baseSeed = 0x881234;
            seed = baseSeed + seed * multiplier;
            var pos = (int)(seed % (i + 1));
            if (i != pos)
            {
                (tab[i], tab[pos]) = (tab[pos], tab[i]);
            }
        }

        var output = new char[4];
        for (var i = 0; i < 4; i++)
        {
            var idx = pin[i] - '0';
            output[i] = (char)('0' + tab[idx]);
        }

        return new string(output);
    }

    internal enum NameValidationResult
    {
        Ok = 0,
        Invalid = 1,
        Exists = 2,
        DatabaseError = 3,
    }

    internal enum CharacterCreateFailure
    {
        NameTaken,
        SlotOccupied,
        InvalidSlot,
        InvalidInput,
        AccountLimitReached,
        DatabaseError,
        Denied,
    }

    internal enum PincodeState : ushort
    {
        Ok = 0,
        Ask = 1,
        NotSet = 2,
        Expired = 3,
        New = 4,
        Illegal = 5,
        Passed = 0,
        Wrong = 8,
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

internal readonly record struct CharacterCreateRequest(
    string Name,
    byte Slot,
    ushort HairColor,
    ushort HairStyle,
    uint Job,
    byte Sex);
