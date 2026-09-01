using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Logging;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Net;

public sealed class CharServerConnector : ICharacterPositionPersistence, ICharacterQuestPersistence, ICharacterGameplayStatePersistence, ICharacterInventoryPersistence, ICharacterInventoryListPersistence, ICharacterSkillPersistence
{
    private static readonly Dictionary<short, int> PacketLengths = new()
    {
        [PacketConstants.MapLoginAck] = 3,
        [PacketConstants.MapAuthFail] = 19,
        [PacketConstants.MapQuestStateResponse] = MapQuestStateProtocol.ResponseLength,
        [PacketConstants.MapSavePointResponse] = MapSavePointProtocol.ResponseLength,
        [PacketConstants.MapGameplayStateGetResponse] = MapCharacterGameplayStateProtocol.ResponseLength,
        [PacketConstants.MapGameplayStateUpdateResponse] = MapCharacterGameplayStateProtocol.ResponseLength,
        [PacketConstants.MapInventoryAddResponse] = MapInventoryAddProtocol.ResponseLength,
        [PacketConstants.MapInventoryEquipUpdateResponse] = MapInventoryEquipUpdateProtocol.ResponseLength,
        [PacketConstants.MapInventoryConsumeResponse] = MapInventoryConsumeProtocol.ResponseLength,
        [PacketConstants.MapSkillLearnResponse] = MapSkillLearnProtocol.ResponseLength,
        // MapInventoryListGetResponse and MapSkillListGetResponse are variable-length - see
        // VariableLengthMinLength below.
    };

    private readonly MapConfigStore _configStore;
    private readonly TimeSpan _initialRetryDelay = TimeSpan.FromSeconds(1);
    private readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(10);
    private readonly TaskCompletionSource<bool> _initialReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<uint, PendingAuthRequest> _pendingAuth = new();
    private readonly ConcurrentDictionary<(uint CharId, uint QuestId), TaskCompletionSource<CharacterQuestStatus?>> _pendingQuestStates = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<bool>> _pendingSavePoints = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<CharacterGameplayState?>> _pendingGameplayReads = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<CharacterGameplayState?>> _pendingGameplayUpdates = new();
    private readonly ConcurrentDictionary<(uint CharId, int ItemId), TaskCompletionSource<InventoryAddPersistenceResult>> _pendingInventoryAdds = new();
    private readonly ConcurrentDictionary<(uint CharId, uint DurableId), TaskCompletionSource<InventoryConsumePersistenceResult>> _pendingInventoryConsumes = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<CharacterInventoryReadResult>> _pendingInventoryReads = new();
    private readonly ConcurrentDictionary<(uint CharId, uint DurableId), TaskCompletionSource<bool>> _pendingEquipUpdates = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<CharacterSkillReadResult>> _pendingSkillReads = new();
    private readonly ConcurrentDictionary<uint, (TaskCompletionSource<CharacterSkillLearnResult?> Pending, ushort SkillId)> _pendingSkillLearns = new();
    private CharServerConnectionState? _connection;

    public CharServerConnector(MapConfigStore configStore)
    {
        _configStore = configStore;
    }

    public bool IsConnected => _connection != null;

    public Task WaitUntilReadyAsync(CancellationToken cancellationToken)
        => _initialReady.Task.WaitAsync(cancellationToken);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var connected = await TryConnectAsync(cancellationToken);
            if (!connected && !cancellationToken.IsCancellationRequested)
            {
                var delay = _initialReady.Task.IsCompleted
                    ? _retryDelay
                    : _initialRetryDelay;
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    public bool TrySendAuthRequest(MapClientSession session, uint accountId, uint charId, uint loginId1, byte sex, IPAddress clientIp)
    {
        return TrySendAuthRequest(session, accountId, charId, loginId1, sex, clientIp, validateSex: true);
    }

    public bool TrySendIroAuthRequest(MapClientSession session, uint accountId, uint charId, uint loginId1, IPAddress clientIp)
    {
        return TrySendAuthRequest(session, accountId, charId, loginId1, 0, clientIp, validateSex: false);
    }

    public async Task<bool> SavePositionAsync(
        uint accountId,
        uint charId,
        string mapName,
        ushort x,
        ushort y,
        CancellationToken cancellationToken)
    {
        var connection = _connection;
        if (connection == null || string.IsNullOrWhiteSpace(mapName))
        {
            return false;
        }

        var mapBytes = Encoding.ASCII.GetBytes(mapName);
        if (mapBytes.Length > PacketConstants.MapNameLength)
        {
            return false;
        }

        var packet = new byte[30];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapSavePosition);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), accountId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(6), charId);
        mapBytes.CopyTo(packet.AsSpan(10, PacketConstants.MapNameLength));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(26), x);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(28), y);
        await connection.WriteAsync(packet, cancellationToken);
        return true;
    }

    public async Task<bool> SavePointAsync(uint accountId, uint charId, string mapName, ushort x, ushort y, CancellationToken cancellationToken)
    {
        var connection = _connection;
        var mapBytes = Encoding.ASCII.GetBytes(mapName);
        if (connection is null || mapBytes.Length is 0 or > 11) return false;
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingSavePoints.TryAdd(charId, pending)) return false;
        using var registration = cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken));
        var packet = MapSavePointProtocol.BuildRequest(accountId, charId, mapName, x, y);
        try { await connection.WriteAsync(packet, cancellationToken); return await pending.Task; }
        finally { _pendingSavePoints.TryRemove(charId, out _); }
    }

    public async Task<CharacterQuestStatus?> GetQuestStateAsync(uint accountId, uint charId, uint questId, CancellationToken cancellationToken)
        => await SendQuestStateRequestAsync(accountId, charId, questId, CharacterQuestStatus.Absent, cancellationToken);

    public async Task<bool> SetQuestStateAsync(uint accountId, uint charId, uint questId, CharacterQuestStatus state, CancellationToken cancellationToken)
        => await SendQuestStateRequestAsync(accountId, charId, questId, state, cancellationToken) is not null;

    public async Task<CharacterGameplayState?> GetAsync(uint accountId, uint characterId, CancellationToken cancellationToken)
    {
        var connection=_connection; if(connection is null)return null;
        var pending=new TaskCompletionSource<CharacterGameplayState?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if(!_pendingGameplayReads.TryAdd(characterId,pending))return null;
        using var registration=cancellationToken.Register(()=>pending.TrySetCanceled(cancellationToken));
        try { await connection.WriteAsync(MapCharacterGameplayStateProtocol.BuildGetRequest(accountId,characterId),cancellationToken); return await pending.Task; }
        finally { _pendingGameplayReads.TryRemove(characterId,out _); }
    }

    public async Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken)
    {
        var connection=_connection; if(connection is null)return null;
        var pending=new TaskCompletionSource<CharacterGameplayState?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if(!_pendingGameplayUpdates.TryAdd(expected.CharacterId,pending))return null;
        using var registration=cancellationToken.Register(()=>pending.TrySetCanceled(cancellationToken));
        try { await connection.WriteAsync(MapCharacterGameplayStateProtocol.BuildUpdateRequest(accountId,expected,updated),cancellationToken); return await pending.Task; }
        finally { _pendingGameplayUpdates.TryRemove(expected.CharacterId,out _); }
    }

    public async Task<CharacterInventoryReadResult> GetInventoryAsync(uint accountId, uint characterId, CancellationToken cancellationToken)
    {
        var connection = _connection; if (connection is null) return CharacterInventoryReadResult.Failed();
        var pending = new TaskCompletionSource<CharacterInventoryReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingInventoryReads.TryAdd(characterId, pending)) return CharacterInventoryReadResult.Failed();
        using var registration = cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken));
        try { await connection.WriteAsync(MapInventoryListProtocol.BuildGetRequest(accountId, characterId), cancellationToken); return await pending.Task; }
        finally { _pendingInventoryReads.TryRemove(characterId, out _); }
    }

    public async Task<CharacterSkillReadResult> GetSkillsAsync(uint accountId, uint characterId, CancellationToken cancellationToken)
    {
        var connection = _connection; if (connection is null) return CharacterSkillReadResult.Failed();
        var pending = new TaskCompletionSource<CharacterSkillReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingSkillReads.TryAdd(characterId, pending)) return CharacterSkillReadResult.Failed();
        using var registration = cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken));
        try { await connection.WriteAsync(MapSkillListProtocol.BuildGetRequest(accountId, characterId), cancellationToken); return await pending.Task; }
        finally { _pendingSkillReads.TryRemove(characterId, out _); }
    }

    public async Task<CharacterSkillLearnResult?> LearnSkillAsync(uint accountId, CharacterGameplayState expectedGameplayState, ushort skillId, byte expectedCurrentLevel, CancellationToken cancellationToken)
    {
        var connection = _connection; if (connection is null) return null;
        var pending = new TaskCompletionSource<CharacterSkillLearnResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingSkillLearns.TryAdd(expectedGameplayState.CharacterId, (pending, skillId))) return null;
        using var registration = cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken));
        try { await connection.WriteAsync(MapSkillLearnProtocol.BuildRequest(accountId, expectedGameplayState, skillId, expectedCurrentLevel), cancellationToken); return await pending.Task; }
        finally { _pendingSkillLearns.TryRemove(expectedGameplayState.CharacterId, out _); }
    }

    public async Task<bool> SetItemEquipAsync(uint accountId, uint characterId, uint durableId, uint equip, CancellationToken cancellationToken)
    {
        var connection = _connection; if (connection is null) return false;
        var key = (characterId, durableId);
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingEquipUpdates.TryAdd(key, pending)) return false;
        using var registration = cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken));
        try { await connection.WriteAsync(MapInventoryEquipUpdateProtocol.BuildRequest(accountId, characterId, durableId, equip), cancellationToken); return await pending.Task; }
        finally { _pendingEquipUpdates.TryRemove(key, out _); }
    }

    public async Task<InventoryAddPersistenceResult> AddStackableItemAsync(uint accountId, uint charId, int itemId, uint amount, CancellationToken cancellationToken)
    {
        var connection = _connection;
        if (connection is null || itemId <= 0 || amount == 0) return InventoryAddPersistenceResult.Failed();
        var key = (charId, itemId);
        var pending = new TaskCompletionSource<InventoryAddPersistenceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingInventoryAdds.TryAdd(key, pending)) return InventoryAddPersistenceResult.Failed();
        using var registration = cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken));
        try
        {
            await connection.WriteAsync(MapInventoryAddProtocol.BuildRequest(accountId, charId, itemId, amount), cancellationToken);
            return await pending.Task;
        }
        finally { _pendingInventoryAdds.TryRemove(key, out _); }
    }

    public async Task<InventoryConsumePersistenceResult> ConsumeItemAsync(uint accountId, uint charId, uint durableId, uint amount, CancellationToken cancellationToken)
    {
        var connection = _connection;
        if (connection is null || amount == 0) return InventoryConsumePersistenceResult.Failed();
        var key = (charId, durableId);
        var pending = new TaskCompletionSource<InventoryConsumePersistenceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingInventoryConsumes.TryAdd(key, pending)) return InventoryConsumePersistenceResult.Failed();
        using var registration = cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken));
        try
        {
            await connection.WriteAsync(MapInventoryConsumeProtocol.BuildRequest(accountId, charId, durableId, amount), cancellationToken);
            return await pending.Task;
        }
        finally { _pendingInventoryConsumes.TryRemove(key, out _); }
    }

    private async Task<CharacterQuestStatus?> SendQuestStateRequestAsync(uint accountId, uint charId, uint questId, CharacterQuestStatus operation, CancellationToken cancellationToken)
    {
        var connection = _connection;
        if (connection is null || questId == 0) return null;
        var key = (charId, questId);
        var pending = new TaskCompletionSource<CharacterQuestStatus?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingQuestStates.TryAdd(key, pending)) return null;
        using var registration = cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken));
        var packet = MapQuestStateProtocol.BuildRequest(accountId, charId, questId, operation);
        // Section 14's own terminology fix: operation==Absent(0) is the CharServer wire protocol's
        // READ opcode (GetQuestStateAsync's own call site below) - only operations 1 (Active) or 2
        // (Completed) actually WRITE the quest row. The live log previously said "Persisting quest
        // ... state=Absent" for every ordinary read, which reads as if an active quest were being
        // repeatedly reset to Absent - it never was; that was always just the read request's own
        // (overloaded) opcode value, never a write. See CharacterQuestStateTests for the regression.
        MapLogger.Info(operation == CharacterQuestStatus.Absent
            ? $"Reading quest state charId={charId} questId={questId}."
            : $"Persisting quest charId={charId} questId={questId} state={operation}.");
        try { await connection.WriteAsync(packet, cancellationToken); return await pending.Task; }
        finally { _pendingQuestStates.TryRemove(key, out _); }
    }

    private bool TrySendAuthRequest(MapClientSession session, uint accountId, uint charId, uint loginId1, byte sex, IPAddress clientIp, bool validateSex)
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
        buffer[19] = validateSex ? (byte)0 : (byte)1;

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
            FailPendingQuestStates();
            FailPendingSavePoints();
            FailPendingGameplayStates();
            FailPendingInventoryAdds();
            FailPendingInventoryReads();
            FailPendingEquipUpdates();
            FailPendingInventoryConsumes();
            FailPendingSkillReads();
            FailPendingSkillLearns();

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
            case PacketConstants.MapQuestStateResponse:
                return HandleQuestStateResponse(packet);
            case PacketConstants.MapSavePointResponse:
                return HandleSavePointResponse(packet);
            case PacketConstants.MapGameplayStateGetResponse:
                return HandleGameplayStateResponse(packet, packetType, _pendingGameplayReads);
            case PacketConstants.MapGameplayStateUpdateResponse:
                return HandleGameplayStateResponse(packet, packetType, _pendingGameplayUpdates);
            case PacketConstants.MapInventoryAddResponse:
                return HandleInventoryAddResponse(packet);
            case PacketConstants.MapInventoryListGetResponse:
                return HandleInventoryListGetResponse(packet);
            case PacketConstants.MapInventoryEquipUpdateResponse:
                return HandleInventoryEquipUpdateResponse(packet);
            case PacketConstants.MapInventoryConsumeResponse:
                return HandleInventoryConsumeResponse(packet);
            case PacketConstants.MapSkillListGetResponse:
                return HandleSkillListGetResponse(packet);
            case PacketConstants.MapSkillLearnResponse:
                return HandleSkillLearnResponse(packet);
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
        _initialReady.TrySetResult(true);
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

    private bool HandleInventoryAddResponse(byte[] packet)
    {
        if (!MapInventoryAddProtocol.TryParseResponse(
                packet, out var charId, out var itemId, out var newAmount, out var durableId,
                out var equip, out var identified, out var refine, out var favorite, out var bound, out var isNewRow, out var success))
            return false;
        if (_pendingInventoryAdds.TryRemove((charId, itemId), out var pending))
            pending.TrySetResult(new InventoryAddPersistenceResult(success, newAmount, durableId, equip, identified, refine, favorite, bound, isNewRow));
        if (success) MapLogger.Info($"Inventory-add succeeded charId={charId} itemId={itemId} newAmount={newAmount} durableId={durableId} isNewRow={isNewRow}.");
        else MapLogger.Warning($"Inventory-add failed charId={charId} itemId={itemId}.");
        return true;
    }

    private bool HandleQuestStateResponse(byte[] packet)
    {
        if (!MapQuestStateProtocol.TryParseResponse(packet, out var charId, out var questId, out var state)) return false;
        if (_pendingQuestStates.TryRemove((charId, questId), out var pending))
            pending.TrySetResult(state);
        // This response handles BOTH read (GetQuestStateAsync) and write (SetQuestStateAsync)
        // requests, keyed only by (charId, questId) - the operation itself is not tracked here, so
        // "state" below is simply the resulting/current row value CharServer returned, never
        // implying a write occurred (see SendQuestStateRequestAsync's own request-side log for the
        // operation-aware "Reading" vs "Persisting" distinction).
        if (state is not null) MapLogger.Info($"Quest state response received charId={charId} questId={questId} state={state}.");
        else MapLogger.Warning($"Quest state request failed charId={charId} questId={questId}.");
        return true;
    }

    private bool HandleSavePointResponse(byte[] packet)
    {
        if (!MapSavePointProtocol.TryParseResponse(packet, out var charId, out var success)) return false;
        if (_pendingSavePoints.TryRemove(charId, out var pending)) pending.TrySetResult(success);
        return true;
    }

    private bool HandleInventoryListGetResponse(byte[] packet)
    {
        if (!MapInventoryListProtocol.TryParseResponse(packet, out _, out var charId, out var inventory)) return false;
        if (_pendingInventoryReads.TryRemove(charId, out var pending)) pending.TrySetResult(inventory);
        return true;
    }

    private bool HandleInventoryEquipUpdateResponse(byte[] packet)
    {
        if (!MapInventoryEquipUpdateProtocol.TryParseResponse(packet, out var success, out var charId, out var durableId)) return false;
        if (_pendingEquipUpdates.TryRemove((charId, durableId), out var pending)) pending.TrySetResult(success);
        return true;
    }

    private bool HandleInventoryConsumeResponse(byte[] packet)
    {
        if (!MapInventoryConsumeProtocol.TryParseResponse(packet, out var success, out var charId, out var durableId, out var newAmount, out var rowDeleted)) return false;
        if (_pendingInventoryConsumes.TryRemove((charId, durableId), out var pending))
            pending.TrySetResult(new InventoryConsumePersistenceResult(success, newAmount, rowDeleted));
        if (success) MapLogger.Info($"Inventory-consume succeeded charId={charId} durableId={durableId} newAmount={newAmount} rowDeleted={rowDeleted}.");
        else MapLogger.Warning($"Inventory-consume failed charId={charId} durableId={durableId}.");
        return true;
    }

    private bool HandleSkillListGetResponse(byte[] packet)
    {
        if (!MapSkillListProtocol.TryParseResponse(packet, out _, out var charId, out var skills)) return false;
        if (_pendingSkillReads.TryRemove(charId, out var pending)) pending.TrySetResult(skills);
        return true;
    }

    // The response echoes charId but not the requested skillId (see MapSkillLearnProtocol's own
    // doc comment) - the pending-request table tracks skillId alongside the TaskCompletionSource
    // so TryParseResponse can attach it to a successful CharacterSkillLearnResult.
    private bool HandleSkillLearnResponse(byte[] packet)
    {
        if (packet.Length < MapSkillLearnProtocol.ResponseHeaderLength) return false;
        var charId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(3));
        if (!_pendingSkillLearns.TryRemove(charId, out var pendingEntry)) return true;
        if (!MapSkillLearnProtocol.TryParseResponse(packet, out _, out _, out var learnResult, pendingEntry.SkillId)) { pendingEntry.Pending.TrySetResult(null); return false; }
        pendingEntry.Pending.TrySetResult(learnResult);
        return true;
    }

    private static bool HandleGameplayStateResponse(byte[] packet, short type, ConcurrentDictionary<uint, TaskCompletionSource<CharacterGameplayState?>> pendingRequests)
    {
        if(!MapCharacterGameplayStateProtocol.TryParseResponse(packet,type,out _,out var charId,out var state))return false;
        if(pendingRequests.TryRemove(charId,out var pending))pending.TrySetResult(state);
        return true;
    }

    internal static bool TryParseAuthOk(byte[] packet, out MapAuthOkData authOk)
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
        var characterName = ReadFixedString(packet.AsSpan(53, PacketConstants.NameLength));
        var hairStyle = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(77));
        var hairColor = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(79));
        var clothesColor = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(81));
        var bodyStyle = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(83));
        var weaponAppearance = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(85));
        var shieldAppearance = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(89));
        var headBottomAppearance = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(93));
        var headTopAppearance = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(95));
        var headMidAppearance = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(97));
        var robeAppearance = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(99));
        var option = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(101));
        var karma = packet[105];
        var manner = BinaryPrimitives.ReadInt16LittleEndian(packet.AsSpan(106));

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
            sex,
            characterName,
            hairStyle,
            hairColor,
            clothesColor,
            bodyStyle,
            weaponAppearance,
            shieldAppearance,
            headBottomAppearance,
            headTopAppearance,
            headMidAppearance,
            robeAppearance,
            option,
            karma,
            manner);

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

    private void FailPendingQuestStates()
    {
        foreach (var pending in _pendingQuestStates.Values) pending.TrySetResult(null);
        _pendingQuestStates.Clear();
    }

    private void FailPendingSavePoints()
    {
        foreach (var pending in _pendingSavePoints.Values) pending.TrySetResult(false);
        _pendingSavePoints.Clear();
    }

    private void FailPendingGameplayStates()
    {
        foreach(var pending in _pendingGameplayReads.Values) pending.TrySetResult(null);
        foreach(var pending in _pendingGameplayUpdates.Values) pending.TrySetResult(null);
        _pendingGameplayReads.Clear(); _pendingGameplayUpdates.Clear();
    }

    private void FailPendingInventoryAdds()
    {
        foreach (var pending in _pendingInventoryAdds.Values) pending.TrySetResult(InventoryAddPersistenceResult.Failed());
        _pendingInventoryAdds.Clear();
    }

    private void FailPendingInventoryReads()
    {
        foreach (var pending in _pendingInventoryReads.Values) pending.TrySetResult(CharacterInventoryReadResult.Failed());
        _pendingInventoryReads.Clear();
    }

    private void FailPendingEquipUpdates()
    {
        foreach (var pending in _pendingEquipUpdates.Values) pending.TrySetResult(false);
        _pendingEquipUpdates.Clear();
    }

    private void FailPendingInventoryConsumes()
    {
        foreach (var pending in _pendingInventoryConsumes.Values) pending.TrySetResult(InventoryConsumePersistenceResult.Failed());
        _pendingInventoryConsumes.Clear();
    }

    private void FailPendingSkillReads()
    {
        foreach (var pending in _pendingSkillReads.Values) pending.TrySetResult(CharacterSkillReadResult.Failed());
        _pendingSkillReads.Clear();
    }

    private void FailPendingSkillLearns()
    {
        foreach (var entry in _pendingSkillLearns.Values) entry.Pending.TrySetResult(null);
        _pendingSkillLearns.Clear();
    }

    // Opcodes framed as [opcode.W][length.W][payload], where `length` is the TOTAL packet
    // length (matching pinned rAthena's own variable-length packet convention) - i.e. payload
    // is (length - 4) bytes. Contrast with PacketLengths, whose opcodes have a single fixed
    // total length known upfront. Both MapAuthOk (a pinned-shaped internal packet carrying the
    // map name string) and MapInventoryListGetResponse (an unbounded-count CharInventory row
    // list - see ICharacterInventoryListPersistence) need this; VariableLengthMinLength is the
    // smallest legal total length for each (below which the packet is malformed).
    private static readonly Dictionary<short, int> VariableLengthMinLength = new()
    {
        [PacketConstants.MapAuthOk] = 4,
        [PacketConstants.MapInventoryListGetResponse] = MapInventoryListProtocol.ResponseHeaderLength,
        [PacketConstants.MapSkillListGetResponse] = MapSkillListProtocol.ResponseHeaderLength,
    };

    // The length field is a uint16 (BinaryPrimitives.ReadUInt16LittleEndian below), so its own
    // max value is already the real ceiling - no separate cap needed.

    private static async Task<byte[]> ReadPacketAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = await ReadExactAsync(stream, 2, cancellationToken);
        if (header.Length == 0)
        {
            return Array.Empty<byte>();
        }

        var packetType = BinaryPrimitives.ReadInt16LittleEndian(header.AsSpan(0, 2));
        if (VariableLengthMinLength.TryGetValue(packetType, out var minLength))
        {
            var lengthBytes = await ReadExactAsync(stream, 2, cancellationToken);
            if (lengthBytes.Length == 0)
            {
                return Array.Empty<byte>();
            }

            var packetLength = BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes.AsSpan(0, 2));
            if (packetLength < minLength)
            {
                MapLogger.Warning($"Malformed variable-length char server packet 0x{packetType:X4} length={packetLength}, disconnecting.");
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
