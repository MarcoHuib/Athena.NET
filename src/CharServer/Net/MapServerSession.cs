using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Athena.Net.CharServer.Config;
using Athena.Net.CharServer.Db;
using Athena.Net.CharServer.Logging;
using Microsoft.EntityFrameworkCore;

namespace Athena.Net.CharServer.Net;

public sealed class MapServerSession : IDisposable, ISession
{
    private static readonly Dictionary<short, int> PacketLengths = new()
    {
        [PacketConstants.MapLogin] = 60,
        [PacketConstants.MapAuthRequest] = 20,
        [PacketConstants.MapSavePosition] = 30,
        [PacketConstants.MapQuestStateRequest] = MapQuestStateProtocol.RequestLength,
        [PacketConstants.MapSavePointRequest] = MapSavePointProtocol.RequestLength,
        [PacketConstants.MapGameplayStateGetRequest] = MapCharacterGameplayStateProtocol.GetRequestLength,
        [PacketConstants.MapGameplayStateUpdateRequest] = MapCharacterGameplayStateProtocol.UpdateRequestLength,
        [PacketConstants.MapInventoryAddRequest] = MapInventoryAddProtocol.RequestLength,
        [PacketConstants.MapInventoryListGetRequest] = MapInventoryListProtocol.GetRequestLength,
        [PacketConstants.MapInventoryEquipUpdateRequest] = MapInventoryEquipUpdateProtocol.RequestLength,
    };

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly CharConfigStore _configStore;
    private readonly MapServerRegistry _registry;
    private readonly MapAuthManager _authManager;
    private readonly Func<CharDbContext?> _dbFactory;
    private readonly HashSet<(uint AccountId, uint CharId)> _ownedCharacters = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _authenticated;
    private IPAddress _mapIp = IPAddress.Loopback;
    private int _mapPort;
    private byte[]? _prefetchedHeader;

    public MapServerSession(
        int sessionId,
        TcpClient client,
        CharConfigStore configStore,
        MapServerRegistry registry,
        MapAuthManager authManager,
        Func<CharDbContext?> dbFactory,
        byte[]? prefetchedHeader = null)
    {
        SessionId = sessionId;
        _client = client;
        _stream = client.GetStream();
        _configStore = configStore;
        _registry = registry;
        _authManager = authManager;
        _dbFactory = dbFactory;
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
            case PacketConstants.MapSavePosition:
                await HandleSavePositionAsync(packet, cancellationToken);
                break;
            case PacketConstants.MapQuestStateRequest:
                await HandleQuestStateRequestAsync(packet, cancellationToken);
                break;
            case PacketConstants.MapSavePointRequest:
                await HandleSavePointAsync(packet, cancellationToken);
                break;
            case PacketConstants.MapGameplayStateGetRequest:
                await HandleGameplayStateGetAsync(packet, cancellationToken);
                break;
            case PacketConstants.MapGameplayStateUpdateRequest:
                await HandleGameplayStateUpdateAsync(packet, cancellationToken);
                break;
            case PacketConstants.MapInventoryAddRequest:
                await HandleInventoryAddRequestAsync(packet, cancellationToken);
                break;
            case PacketConstants.MapInventoryListGetRequest:
                await HandleInventoryListGetAsync(packet, cancellationToken);
                break;
            case PacketConstants.MapInventoryEquipUpdateRequest:
                await HandleInventoryEquipUpdateAsync(packet, cancellationToken);
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
        var iroAuth = packet[19] == 1;

        if (_authManager.TryConsume(
                accountId,
                charId,
                loginId1,
                iroAuth ? null : sex,
                out var node))
        {
            _ownedCharacters.Add((node.AccountId, node.CharId));
            await SendAuthOkAsync(node, cancellationToken);
            return;
        }

        await SendAuthFailAsync(accountId, charId, loginId1, sex, clientIp, cancellationToken);
    }

    private async Task HandleSavePositionAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!_authenticated)
        {
            return;
        }

        var accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        var charId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(6, 4));
        var mapName = ReadFixedString(packet.AsSpan(10, PacketConstants.MapNameLength));
        var x = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(26, 2));
        var y = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(28, 2));
        if (!IsPositionSaveAuthorized(_authenticated, _ownedCharacters, accountId, charId) ||
            string.IsNullOrWhiteSpace(mapName) ||
            mapName.Length > 11)
        {
            CharLogger.Warning(
                $"Rejected character position save accountId={accountId} charId={charId} for map server session {SessionId}.");
            return;
        }

        await using var db = _dbFactory();
        if (db == null)
        {
            return;
        }

        var character = await db.Characters.FirstOrDefaultAsync(
            candidate => candidate.AccountId == accountId && candidate.CharId == charId && candidate.DeleteDate == 0,
            cancellationToken);
        if (character == null)
        {
            CharLogger.Warning(
                $"Rejected character position save for missing character accountId={accountId} charId={charId}.");
            return;
        }

        character.LastMap = mapName;
        character.LastX = x;
        character.LastY = y;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task HandleSavePointAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!MapSavePointProtocol.TryParseRequest(packet, out var request)) return;
        var accountId = request.AccountId; var charId = request.CharId; var mapName = request.Map; var x = request.X; var y = request.Y;
        var success = false;
        try
        {
            if (_authenticated && IsPositionSaveAuthorized(_authenticated, _ownedCharacters, accountId, charId) && !string.IsNullOrWhiteSpace(mapName) && mapName.Length <= 11)
            {
                await using var db = _dbFactory();
                var character = db is null ? null : await db.Characters.FirstOrDefaultAsync(candidate => candidate.AccountId == accountId && candidate.CharId == charId && candidate.DeleteDate == 0, cancellationToken);
                if (character is not null)
                {
                    character.SaveMap = mapName; character.SaveX = x; character.SaveY = y;
                    await db!.SaveChangesAsync(cancellationToken); success = true;
                    CharLogger.Info($"SavePoint persistence succeeded charId={charId} map='{mapName}' x={x} y={y}.");
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            CharLogger.Warning($"SavePoint persistence failed charId={charId}: {exception.GetType().Name}: {exception.Message}");
        }
        await WriteAsync(MapSavePointProtocol.BuildResponse(charId, success), cancellationToken);
    }

    private async Task HandleQuestStateRequestAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!MapQuestStateProtocol.TryParseRequest(packet, out var request))
        {
            CharLogger.Warning($"Rejected malformed quest persistence request session={SessionId} length={packet.Length}.");
            return;
        }

        CharLogger.Info(
            $"Quest persistence request accountId={request.AccountId} charId={request.CharId} " +
            $"questId={request.QuestId} desiredState={request.Operation}.");
        byte state = 0;
        var success = false;
        if (!IsQuestStateRequestAuthorized(
                _authenticated, _ownedCharacters, request.AccountId, request.CharId, request.QuestId, request.Operation))
        {
            CharLogger.Warning(
                $"Quest persistence rejected reason=unauthorized-or-invalid accountId={request.AccountId} " +
                $"charId={request.CharId} questId={request.QuestId}.");
        }
        else
        {
            try
            {
                await using var db = _dbFactory();
                if (db is null)
                {
                    CharLogger.Warning("Quest persistence rejected reason=database-unavailable.");
                }
                else
                {
                    var quest = await db.Quests.FirstOrDefaultAsync(
                        q => q.CharId == request.CharId && q.QuestId == request.QuestId, cancellationToken);
                    if (request.Operation is 1 or 2)
                    {
                        if (quest is null)
                        {
                            quest = new() { CharId = request.CharId, QuestId = request.QuestId };
                            db.Quests.Add(quest);
                        }
                        quest.State = request.Operation.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        await db.SaveChangesAsync(cancellationToken);
                    }
                    success = quest is null ||
                        (byte.TryParse(quest.State, System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out state) && state <= 2);
                    if (success)
                    {
                        CharLogger.Info(
                            $"Quest persistence succeeded charId={request.CharId} questId={request.QuestId} state={state}.");
                    }
                    else
                    {
                        state = 0;
                        CharLogger.Warning(
                            $"Quest persistence rejected reason=invalid-stored-state charId={request.CharId} questId={request.QuestId}.");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                CharLogger.Warning(
                    $"Quest persistence rejected reason=database-error charId={request.CharId} " +
                    $"questId={request.QuestId} error={ex.GetType().Name}.");
            }
        }

        await WriteAsync(MapQuestStateProtocol.BuildResponse(request.CharId, request.QuestId, state, success), cancellationToken);
    }

    private async Task HandleInventoryAddRequestAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!MapInventoryAddProtocol.TryParseRequest(packet, out var request))
        {
            CharLogger.Warning($"Rejected malformed inventory-add request session={SessionId} length={packet.Length}.");
            return;
        }

        uint newAmount = 0;
        uint slotIndex = 0;
        uint equip = 0;
        var identified = false;
        byte refine = 0;
        byte favorite = 0;
        byte bound = 0;
        var success = false;
        if (!IsInventoryAddRequestAuthorized(_authenticated, _ownedCharacters, request.AccountId, request.CharId, request.ItemId, request.Amount))
        {
            CharLogger.Warning(
                $"Inventory-add rejected reason=unauthorized-or-invalid accountId={request.AccountId} " +
                $"charId={request.CharId} itemId={request.ItemId}.");
        }
        else
        {
            try
            {
                await using var db = _dbFactory();
                if (db is null)
                {
                    CharLogger.Warning("Inventory-add rejected reason=database-unavailable.");
                }
                else
                {
                    var row = await db.Inventory.FirstOrDefaultAsync(
                        item => item.CharId == request.CharId && item.NameId == (uint)request.ItemId && item.Equip == 0, cancellationToken);
                    if (row is null)
                    {
                        row = new() { CharId = request.CharId, NameId = (uint)request.ItemId, Amount = request.Amount, Identify = 1 };
                        db.Inventory.Add(row);
                    }
                    else
                    {
                        row.Amount += request.Amount;
                    }
                    await db.SaveChangesAsync(cancellationToken);
                    newAmount = row.Amount;
                    success = true;
                    equip = row.Equip;
                    identified = row.Identify != 0;
                    refine = row.Refine;
                    favorite = row.Favorite;
                    bound = row.Bound;
                    // SlotIndex is this row's position in the ONE authoritative stable ordering
                    // (CharInventoryOrdering.InStableSlotOrder) - equipped and unequipped rows
                    // share that same namespace. Must NOT reuse the item.Equip == 0 filter above
                    // (that filter exists only to find/avoid matching an EQUIPPED row when
                    // searching for a stackable item's existing stack - it has no bearing on slot
                    // position, which was the previously-diverging bug: counting only unequipped
                    // rows produced a different, incompatible namespace from the inventory-list
                    // read's/equip-update's full-row ordering).
                    slotIndex = (uint)await db.Inventory.InStableSlotOrder(request.CharId).CountAsync(item => item.Id < row.Id, cancellationToken);
                    CharLogger.Info(
                        $"Inventory-add succeeded charId={request.CharId} itemId={request.ItemId} newAmount={newAmount} slotIndex={slotIndex}.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                CharLogger.Warning(
                    $"Inventory-add rejected reason=database-error charId={request.CharId} " +
                    $"itemId={request.ItemId} error={ex.GetType().Name}.");
            }
        }

        await WriteAsync(
            MapInventoryAddProtocol.BuildResponse(request.CharId, request.ItemId, newAmount, slotIndex, equip, identified, refine, favorite, bound, success),
            cancellationToken);
    }

    private async Task HandleGameplayStateGetAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!MapCharacterGameplayStateProtocol.TryParseGet(packet, out var accountId, out var charId)) return;
        CharacterGameplayStateDto? state = null; byte result = 1;
        if (IsGameplayStateRequestAuthorized(_authenticated, _ownedCharacters, accountId, charId))
        {
            try
            {
                await using var db = _dbFactory();
                var character = db is null
                    ? null
                    : await db.Characters.AsNoTracking().SingleOrDefaultAsync(
                        c => c.AccountId == accountId && c.CharId == charId && c.DeleteDate == 0,
                        cancellationToken);
                if (character is not null) { state = CharacterGameplayStateDto.From(character); result = 0; }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                CharLogger.Warning(
                    $"Character gameplay state read rejected reason=database-error charId={charId} " +
                    $"error={ex.GetType().Name}.");
            }
        }
        await WriteAsync(MapCharacterGameplayStateProtocol.BuildResponse(PacketConstants.MapGameplayStateGetResponse, result, charId, state), cancellationToken);
    }

    private async Task HandleInventoryListGetAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!MapInventoryListProtocol.TryParseGet(packet, out var accountId, out var charId)) return;
        List<CharacterInventoryRowDto>? rows = null; byte result = 1;
        if (IsGameplayStateRequestAuthorized(_authenticated, _ownedCharacters, accountId, charId))
        {
            try
            {
                await using var db = _dbFactory();
                if (db is not null)
                {
                    // ONE authoritative stable server-side array order - see
                    // CharInventoryOrdering.InStableSlotOrder's own doc comment. Equipped and
                    // unequipped rows share this same namespace.
                    rows = await db.Inventory.AsNoTracking()
                        .InStableSlotOrder(charId)
                        .Select(i => new CharacterInventoryRowDto((int)i.NameId, i.Amount, i.Equip, i.Identify != 0, i.Refine, i.Favorite, i.Bound))
                        .ToListAsync(cancellationToken);
                    result = 0;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                CharLogger.Warning(
                    $"Character inventory read rejected reason=database-error charId={charId} " +
                    $"error={ex.GetType().Name}.");
            }
        }
        await WriteAsync(MapInventoryListProtocol.BuildResponse(result, charId, rows), cancellationToken);
    }

    private async Task HandleInventoryEquipUpdateAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!MapInventoryEquipUpdateProtocol.TryParseRequest(packet, out var accountId, out var charId, out var slotIndex, out var equip)) return;
        var success = false;
        if (IsGameplayStateRequestAuthorized(_authenticated, _ownedCharacters, accountId, charId))
        {
            try
            {
                await using var db = _dbFactory();
                if (db is not null)
                {
                    // ONE authoritative stable server-side array order - see
                    // CharInventoryOrdering.InStableSlotOrder's own doc comment.
                    var row = await db.Inventory
                        .InStableSlotOrder(charId)
                        .Skip((int)slotIndex)
                        .FirstOrDefaultAsync(cancellationToken);
                    if (row is not null)
                    {
                        row.Equip = equip;
                        await db.SaveChangesAsync(cancellationToken);
                        success = true;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                CharLogger.Warning(
                    $"Character inventory equip update rejected reason=database-error charId={charId} " +
                    $"slotIndex={slotIndex} error={ex.GetType().Name}.");
            }
        }
        await WriteAsync(MapInventoryEquipUpdateProtocol.BuildResponse(success, charId, slotIndex), cancellationToken);
    }

    private async Task HandleGameplayStateUpdateAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!MapCharacterGameplayStateProtocol.TryParseUpdate(packet, out var accountId, out var expected, out var updated)) return;
        CharacterGameplayStateDto? state = null; byte result = 1;
        if (IsValidGameplayStateUpdate(expected, updated) &&
            IsGameplayStateRequestAuthorized(_authenticated, _ownedCharacters, accountId, expected.CharacterId))
        {
            try
            {
                await using var db = _dbFactory();
                if (db is not null)
                {
                    var strategy = db.Database.CreateExecutionStrategy();
                    await strategy.ExecuteAsync(async () =>
                    {
                        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                        var character = await db.Characters.SingleOrDefaultAsync(
                            c => c.AccountId == accountId && c.CharId == expected.CharacterId && c.DeleteDate == 0,
                            cancellationToken);
                        if (character is null || !TryApplyGameplayState(character, expected, updated))
                        {
                            result = 2;
                            await transaction.RollbackAsync(cancellationToken);
                            return;
                        }

                        await db.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        state = CharacterGameplayStateDto.From(character);
                        result = 0;
                    });
                }
            }
            catch (DbUpdateConcurrencyException)
            {
                result = 2;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                CharLogger.Warning(
                    $"Character gameplay state update rejected reason=database-error charId={expected.CharacterId} " +
                    $"error={ex.GetType().Name}.");
            }
        }
        await WriteAsync(MapCharacterGameplayStateProtocol.BuildResponse(PacketConstants.MapGameplayStateUpdateResponse, result, expected.CharacterId, state), cancellationToken);
    }

    internal static bool IsValidGameplayStateUpdate(CharacterGameplayStateDto expected, CharacterGameplayStateDto updated)
        => expected.CharacterId == updated.CharacterId &&
           expected.CharacterId != 0 &&
           expected.JobClass == updated.JobClass &&
           updated.BaseLevel > 0 &&
           updated.JobLevel > 0 &&
           updated.MaxHp > 0 &&
           updated.MaxSp > 0 &&
           updated.CurrentHp <= updated.MaxHp &&
           updated.CurrentSp <= updated.MaxSp &&
           expected.Version < ulong.MaxValue;

    internal static bool TryApplyGameplayState(Athena.Net.CharServer.Db.Entities.CharCharacter character, CharacterGameplayStateDto expected, CharacterGameplayStateDto updated)
    {
        if(character.CharId!=expected.CharacterId||updated.CharacterId!=expected.CharacterId||character.GameplayStateVersion!=expected.Version)return false;
        character.BaseLevel=updated.BaseLevel; character.JobLevel=updated.JobLevel; character.BaseExp=updated.BaseExperience; character.JobExp=updated.JobExperience;
        character.Hp=updated.CurrentHp; character.Sp=updated.CurrentSp; character.MaxHp=updated.MaxHp; character.MaxSp=updated.MaxSp;
        character.StatusPoint=updated.StatPoints; character.SkillPoint=updated.SkillPoints; character.Str=updated.Strength; character.Agi=updated.Agility;
        character.Vit=updated.Vitality; character.Int=updated.Intelligence; character.Dex=updated.Dexterity; character.Luk=updated.Luck; character.GameplayStateVersion++;
        return true;
    }

    internal static bool IsPositionSaveAuthorized(
        bool authenticated,
        IReadOnlySet<(uint AccountId, uint CharId)> ownedCharacters,
        uint accountId,
        uint charId)
    {
        return authenticated && ownedCharacters.Contains((accountId, charId));
    }

    internal static bool IsGameplayStateRequestAuthorized(bool authenticated, IReadOnlySet<(uint AccountId,uint CharId)> ownedCharacters, uint accountId, uint charId)
        => authenticated && charId != 0 && ownedCharacters.Contains((accountId,charId));

    internal static bool IsInventoryAddRequestAuthorized(
        bool authenticated,
        IReadOnlySet<(uint AccountId, uint CharId)> ownedCharacters,
        uint accountId,
        uint charId,
        int itemId,
        uint amount)
    {
        return authenticated && itemId > 0 && amount > 0 && ownedCharacters.Contains((accountId, charId));
    }

    internal static bool IsQuestStateRequestAuthorized(
        bool authenticated,
        IReadOnlySet<(uint AccountId, uint CharId)> ownedCharacters,
        uint accountId,
        uint charId,
        uint questId,
        byte operation)
    {
        return authenticated && questId > 0 && operation <= 2 && ownedCharacters.Contains((accountId, charId));
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
