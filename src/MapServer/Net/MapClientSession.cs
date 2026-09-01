using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using Athena.Net.MapServer.Generated.GameData.Items;
using Athena.Net.MapServer.Generated.GameData.Quests;
using Athena.Net.MapServer.Gameplay.Rates;
using Athena.Net.MapServer.Logging;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.GeneratedScripts;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Net;

public sealed class MapClientSession : IAsyncDisposable, INpcScriptHost, IPlayerPresenceObserver
{
    private static readonly Dictionary<short, int> PacketLengths = new()
    {
        [PacketConstants.CzEnter] = 19,
        [PacketConstants.CzEnter2] = 19,
        // The stock-iRO capture carries one opaque trailing byte. Legacy references use 2 bytes.
        [PacketConstants.CzNotifyActorInit] = 3,
        [PacketConstants.CzClientVersion] = 6,
        [PacketConstants.CzPingLive] = 2,
        [PacketConstants.IroCzMapAuth] = PacketConstants.IroCzMapAuthLength,
        // Stock iRO appends one still-opaque byte to these otherwise familiar client packets.
        [PacketConstants.IroCzPostEnter0360] = PacketConstants.IroCzPostEnter0360Length,
        [PacketConstants.IroCzPostEnter08c9] = PacketConstants.IroCzPostEnter08c9Length,
        [PacketConstants.IroCzRequestMove] = PacketConstants.IroCzRequestMoveLength,
        [PacketConstants.IroCzActorInfoRequest] = PacketConstants.IroCzActorInfoRequestLength,
        [PacketConstants.IroCzChangeDirection] = PacketConstants.IroCzChangeDirectionLength,
        [PacketConstants.IroCzNpcInteraction] = PacketConstants.IroCzNpcInteractionLength,
        [PacketConstants.IroCzNpcNext] = PacketConstants.IroCzNpcNextLength,
        [PacketConstants.IroCzNpcClose] = PacketConstants.IroCzNpcCloseLength,
        [PacketConstants.IroCzNpcSelection] = PacketConstants.IroCzNpcSelectionLength,
        [PacketConstants.IroCzAttackRequest] = PacketConstants.IroCzAttackRequestLength,
        [PacketConstants.IroCzReqWearEquip] = PacketConstants.IroCzReqWearEquipLength,
        [PacketConstants.IroCzReqTakeoffEquip] = PacketConstants.IroCzReqTakeoffEquipLength,
        [PacketConstants.IroCzUseItem] = PacketConstants.IroCzUseItemLength,
        [PacketConstants.IroCzSkillLevelUp] = PacketConstants.IroCzSkillLevelUpLength,
        [PacketConstants.IroCzStatusUp] = PacketConstants.IroCzStatusUpLength,
    };

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly CharServerConnector _charConnector;
    private readonly ICharacterPositionPersistence _positionPersistence;
    private readonly ICharacterQuestPersistence _questPersistence;
    private readonly ICharacterGameplayStatePersistence _gameplayStatePersistence;
    private readonly ICharacterInventoryListPersistence _inventoryListPersistence;
    private readonly ICharacterInventoryPersistence _inventoryPersistence;
    private readonly ICharacterSkillPersistence _skillPersistence;
    private readonly WorldMapRegistry _worldMapRegistry;
    // Null when no MapServerWorld was supplied (test-facing constructor default).
    private readonly MonsterRegistry? _monsters;
    // Null alongside _monsters on the test-facing default path; both are populated together
    // by the production MapServerWorld-based constructor.
    private readonly MonsterCombatCoordinator? _combat;
    // Diagnostic-only for now (0x0368 actor-info click/hover logging) - see LogMonsterCellDiagnostics.
    // This actorId-correlated diagnostic is the ONE remaining live spatial diagnostic; the old bulk
    // per-spawn [MONSTER CELL] log (MobSpawnCellSelector, ~200 lines at startup) was removed once
    // this superseded it for live investigation - see this task's own report for that decision.
    // Null on the test-facing default path, same as _monsters/_combat. A small, reusable, read-only
    // spatial-inspection capability rather than threading IMapCollisionProvider into MonsterRegistry
    // merely for logging - see MonsterSpatialInspector's own doc comment for why it exists as its
    // own composed type.
    private readonly MonsterSpatialInspector? _spatialInspector;
    // Null on the test-facing default path, same as _monsters/_combat/_spatialInspector. Shared
    // across every session on this MapServer process (composed once in MapServerWorld.Build) -
    // ProcessTick is safe to call from multiple sessions' own periodic loops because
    // MonsterRegistry/MobInstance already own their own internal locking; this field is only a
    // reference to that ONE shared scheduler, never a per-session copy.
    private readonly MonsterRuntime? _monsterRuntime;
    private readonly IMovementPathProvider _movementPathProvider;
    // Same shared collision data every other collision-aware component uses (MonsterRuntime's own
    // idle-walk pathfinding, RathenaCompatibleMovementPathProvider) - never a second independently
    // loaded copy, never re-parsing map_cache.dat. Used by BasicAttackDistanceValidator's
    // battle_check_range line-of-attack check (see PerformDueRepeatAttackAsync). Defaults to
    // EmptyMapCollisionProvider.Instance on the test-facing path, matching this project's existing
    // "collision-less means no real map is loaded" convention (MapServerWorld.Build's own default).
    private readonly IMapCollisionProvider _collisionProvider;
    private readonly GameplayRateOptions _rates;
    private readonly PlayerPresenceRegistry _players;
    private readonly PlayerVisibilityCoordinator _playerVisibility;
    private readonly IWorldRuntime? _distributedWorld;
    private readonly WorldVisibilityOptions _visibilityOptions;
    // Owns authoritative per-cell walk timing (see CharacterMovementState's own doc comment for the
    // rAthena unit_walktoxy_timer trace this replaces). _x/_y/_mapName remain the fields every other
    // handler in this class reads; SyncPositionFromMovement() is the one place that reconciles them
    // against _movement's real elapsed-time state, called at the top of HandleIroMovementAsync so a
    // new movement request always retargets from the character's ACTUAL current cell rather than
    // wherever a previous request's destination happened to be.
    private CharacterMovementState? _movement;
    // Guards all reads/mutations of _movement, _pendingArrival, and the position fields they drive
    // (_mapName/_x/_y/_positionDirty) against a race between the movement loop (background task,
    // below) and a packet-handling call (HandleIroMovementAsync, TeleportTo) running concurrently -
    // this session's RunAsync packet loop and the movement loop are two independently-scheduled
    // tasks that both touch this state.
    private readonly SemaphoreSlim _movementGate = new(1, 1);
    // The warp/script-touch action attached to the CURRENT walk's destination cell, executed by the
    // movement loop only once that cell is actually reached (AdvanceTo reports it as newly crossed),
    // not immediately at click time - this is what makes OnTouch/warp execution match rAthena's
    // per-cell semantics (npc_touch_area_allnpc/npc_touch_areanpc2 inside unit_walktoxy_timer,
    // unit.cpp:684-699) instead of firing the instant a route is detected to intersect one.
    // Cleared by TeleportTo (a teleport/warp/map-change invalidates any old walk's pending action)
    // and by StartWalk with no intersection.
    private PendingMovementArrival? _pendingArrival;
    private abstract record PendingMovementArrival;
    private sealed record PendingWarpArrival(WarpDefinition Warp) : PendingMovementArrival;
    private sealed record PendingScriptTouchArrival(WorldEntityDefinition Entity, uint ActorId, ScriptBehaviorDefinition Script) : PendingMovementArrival;
    // NextAttackAt is the pinned ud->attackabletime equivalent - mutated in place (under
    // _attackGate) each time an attack actually executes, rather than replacing the record, so a
    // concurrent read of "is this still the active target" (TeleportTo/a replacing attack request)
    // keeps identity-comparing against the same instance.
    private sealed record RepeatAttackState(uint TargetActorId)
    {
        public DateTimeOffset NextAttackAt { get; set; }
    }
    // Single-slot wake signal (same semantics as _statusExpirationSignal above): a new/retargeted
    // walk may need the loop to wake earlier than its current sleep, or wake it from indefinite
    // waiting when it starts moving from a standstill.
    private readonly SemaphoreSlim _movementSignal = new(0, 1);
    private Task? _movementLoop;
    // Server-owned repeat-attack state (pinned unit_data.attacktimer/target/attackabletime,
    // unit.cpp:2902-2986/3187-3344). At most one active repeat target per session, matching
    // pinned unit_attack's own "just change target/type" comment (unit.cpp:2951-2953): a new
    // 0x0437 replaces this record outright rather than layering a second concurrent loop. Guarded
    // by _attackGate the same way _movement is guarded by _movementGate - RunRepeatAttackLoopAsync
    // (background task) and HandleIroAttackRequestAsync/TeleportTo (packet-handling calls) run on
    // independently-scheduled tasks that both touch this state.
    private RepeatAttackState? _repeatAttack;
    private readonly SemaphoreSlim _attackGate = new(1, 1);
    // Single-slot wake signal, same semantics as _movementSignal/_statusExpirationSignal: a new or
    // replaced repeat-attack target may need the loop to wake earlier than its current sleep, or
    // wake it from indefinite waiting when no repeat attack was previously active.
    private readonly SemaphoreSlim _attackSignal = new(0, 1);
    private Task? _attackLoop;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    // Diagnostic-only, read solely by RunAsync's own finally block to log the last packet
    // successfully written before this session's socket loop exits (task requirement: a
    // disconnect/crash investigation needs to know exactly what the client had already received
    // immediately beforehand). Set only AFTER the underlying stream write completes without
    // throwing - never before - so a write that itself fails/throws (e.g. because the client had
    // already disconnected) correctly leaves the PREVIOUS successful packet as "last written",
    // not the one that failed.
    private volatile string _lastPacketWrittenDescription = "<none>";
    private readonly CancellationTokenSource _sessionCancellation = new();
    private readonly VisibleActorTracker _visibleActorIds = new();
    private ScriptExecutionSession? _scriptExecutionSession;
    private Task? _generatedScriptTask;
    private string? _generatedScriptEntityId;
    private uint _generatedScriptActorId;
    private GeneratedContinuation? _generatedContinuation;
    private TaskCompletionSource _generatedSuspended = NewSignal();
    private uint _accountId;
    private uint _charId;
    private uint _loginId1;
    private string _characterName = string.Empty;
    private string _mapName = string.Empty;
    private ushort _x;
    private ushort _y;
    private byte _sex;
    private byte _direction;
    private byte _headDirection;
    private PlayerPresence? _presence;
    // Created once per logical world-visible lifecycle and retained across registration retries.
    // It is independent of Ragnarok packets, transport endpoints, and Orleans activation identity.
    private Guid? _presenceId;
    private string? _presenceMapId;
    private Guid? _pendingTransferId;
    private (string SourceMap, string DestinationMap, ushort X, ushort Y)? _pendingTransfer;
    private PlayerSessionLifecycle _playerLifecycle = PlayerSessionLifecycle.Unauthenticated;
    private readonly object _playerPresenceGate = new();
    private PlayerAuthAppearance _authAppearance = new();
    private sealed record PlayerAuthAppearance(
        ushort HairStyle = 0, ushort HairColor = 0, ushort ClothesColor = 0, ushort BodyStyle = 0,
        uint Weapon = 0, uint Shield = 0, ushort HeadBottom = 0, ushort HeadTop = 0,
        ushort HeadMid = 0, ushort Robe = 0, uint Option = 0, byte Karma = 0, short Manner = 0,
        ushort Font = 0);
    private bool _authRequested;
    private bool _iroAuthRequested;
    private bool _authenticated;
    private bool _positionDirty;
    // Guards EnsureRuntimeLoopsStarted so exactly one _statusExpirationLoop/_movementLoop pair is
    // ever created for this session, even if CompleteIroAuthenticationAsync and RunAsync's own
    // already-authenticated startup check both reach it (HandleAuthOk fires CompleteIroAuthentication-
    // SafelyAsync fire-and-forget, so it can race a RunAsync that started first on the already-
    // authenticated test constructor path). A plain `lock` is fine here: the guarded body only
    // reads/writes two Task? fields and starts two fire-and-forget async methods - it never awaits
    // while holding the lock.
    private readonly object _runtimeLoopStartGate = new();
    private bool _runtimeLoopsStarted;
    // Backs StopAsync/DisposeAsync's idempotent, shared shutdown: every caller (RunAsync's finally,
    // DisposeAsync, a direct StopAsync call) observes the SAME Task and therefore the same outcome,
    // instead of each caller racing its own teardown against the others' resource disposal.
    private readonly object _shutdownGate = new();
    private Task? _shutdownTask;
    private CharacterGameplayStateSession? _gameplayState;
    // The one authoritative CharInventory read (see CharacterInventorySnapshot's own doc
    // comment). Equipment is derived from this - never a second independent CharServer read.
    private CharacterInventorySnapshot? _inventory;
    private CharacterEquipmentSnapshot? _equipment;
    private readonly CharacterStatusEffectState _statusEffects;
    private readonly TimeProvider _timeProvider;
    // Single-slot wake signal (max count 1, not int.MaxValue): StartStatusAsync may Release()
    // several times before the scheduler loop gets a chance to consume a wake, and only one
    // extra wake-and-recheck is ever needed regardless of how many times the deadline moved
    // meanwhile - the loop always re-reads NextExpiration fresh on each iteration.
    private readonly SemaphoreSlim _statusExpirationSignal = new(0, 1);
    private Task? _statusExpirationLoop;

    // Production entry point used by MapTcpServer. Requires the composed MapServerWorld built once
    // at startup (MapServerApp.RunAsync -> MapServerWorld.Build()) rather than defaulting to
    // WorldMapRegistry.Tutorial: that static singleton builds its OWN private WorldActorIdAllocator,
    // so silently falling back to it here would reintroduce a second, independent actor-ID
    // namespace alongside the composed MonsterRegistry's shared one.
    public MapClientSession(int sessionId, TcpClient client, CharServerConnector charConnector, MapServerWorld world, IWorldRuntime worldRuntime)
        : this(sessionId, client, charConnector, world.Maps, monsters: world.Monsters, combat: world.Combat, spatialInspector: world.SpatialInspector,
               movementPathProvider: world.MovementPathProvider, monsterRuntime: world.MonsterRuntime, collisionProvider: world.Collision, rates: world.Rates,
               players: world.Players, playerVisibility: world.PlayerVisibility, visibilityOptions: world.Visibility, distributedWorld: worldRuntime)
    {
    }

    private MapClientSession(
        int sessionId,
        TcpClient client,
        CharServerConnector charConnector,
        WorldMapRegistry worldMapRegistry,
        ICharacterPositionPersistence? positionPersistence = null,
        ICharacterQuestPersistence? questPersistence = null,
        ICharacterGameplayStatePersistence? gameplayStatePersistence = null,
        TimeProvider? timeProvider = null,
        MonsterRegistry? monsters = null,
        IMovementPathProvider? movementPathProvider = null,
        MonsterCombatCoordinator? combat = null,
        ICharacterInventoryPersistence? inventoryPersistence = null,
        ICharacterInventoryListPersistence? inventoryListPersistence = null,
        ICharacterSkillPersistence? skillPersistence = null,
        MonsterSpatialInspector? spatialInspector = null,
        MonsterRuntime? monsterRuntime = null,
        IMapCollisionProvider? collisionProvider = null,
        GameplayRateOptions? rates = null,
        PlayerPresenceRegistry? players = null,
        PlayerVisibilityCoordinator? playerVisibility = null,
        WorldVisibilityOptions? visibilityOptions = null,
        IWorldRuntime? distributedWorld = null)
    {
        SessionId = sessionId;
        _client = client;
        _stream = client.GetStream();
        _charConnector = charConnector;
        _positionPersistence = positionPersistence ?? charConnector;
        _questPersistence = questPersistence ?? charConnector;
        _gameplayStatePersistence = gameplayStatePersistence ?? charConnector;
        _inventoryPersistence = inventoryPersistence ?? charConnector;
        _inventoryListPersistence = inventoryListPersistence ?? charConnector;
        _skillPersistence = skillPersistence ?? charConnector;
        _worldMapRegistry = worldMapRegistry;
        _monsters = monsters;
        _combat = combat;
        _spatialInspector = spatialInspector;
        _monsterRuntime = monsterRuntime;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _movementPathProvider = movementPathProvider ?? new UnverifiedGridLineMovementPathProvider();
        _collisionProvider = collisionProvider ?? EmptyMapCollisionProvider.Instance;
        _rates = rates ?? new GameplayRateOptions();
        _visibilityOptions = visibilityOptions ?? WorldVisibilityOptions.Default;
        _players = players ?? new PlayerPresenceRegistry(_visibilityOptions);
        _playerVisibility = playerVisibility ?? new PlayerVisibilityCoordinator(_players, _visibilityOptions);
        _distributedWorld = distributedWorld;
        _statusEffects = new CharacterStatusEffectState(_timeProvider);
    }

    // Test-facing constructor. Still defaults to WorldMapRegistry.Tutorial when no registry is
    // supplied - this default is fine for unit/integration tests that only exercise NPC/warp/
    // dialogue behavior and never touch monster state, but MUST NOT be reintroduced on the
    // production path above (see its doc comment).
    internal MapClientSession(
        int sessionId,
        TcpClient client,
        CharServerConnector charConnector,
        bool iroAuthenticated,
        string mapName = "",
        ushort x = 0,
        ushort y = 0,
        WorldMapRegistry? worldMapRegistry = null,
        ICharacterPositionPersistence? positionPersistence = null,
        ICharacterQuestPersistence? questPersistence = null,
        uint accountId = 0,
        uint charId = 0,
        ICharacterGameplayStatePersistence? gameplayStatePersistence = null,
        TimeProvider? timeProvider = null,
        MonsterRegistry? monsters = null,
        IMovementPathProvider? movementPathProvider = null,
        MonsterCombatCoordinator? combat = null,
        ICharacterInventoryPersistence? inventoryPersistence = null,
        ICharacterInventoryListPersistence? inventoryListPersistence = null,
        ICharacterSkillPersistence? skillPersistence = null,
        MonsterSpatialInspector? spatialInspector = null,
        MonsterRuntime? monsterRuntime = null,
        IMapCollisionProvider? collisionProvider = null,
        GameplayRateOptions? rates = null,
        PlayerPresenceRegistry? players = null,
        PlayerVisibilityCoordinator? playerVisibility = null,
        WorldVisibilityOptions? visibilityOptions = null,
        IWorldRuntime? distributedWorld = null)
        : this(
            sessionId,
            client,
            charConnector,
            worldMapRegistry ?? WorldMapRegistry.Tutorial,
            positionPersistence,
            questPersistence,
            gameplayStatePersistence,
            timeProvider,
            monsters,
            movementPathProvider,
            combat,
            inventoryPersistence,
            // Defaults to a successful "confirmed empty inventory" read, NOT the production
            // default (falling through to charConnector, a disconnected CharServerConnector in
            // tests, whose GetInventoryAsync always returns Failed() and would make
            // CompleteIroAuthenticationAsync fail auth for every test that doesn't care about
            // inventory/equipment). Tests that need to exercise specific inventory rows or a
            // failed read must pass inventoryListPersistence explicitly, same as
            // gameplayStatePersistence.
            inventoryListPersistence ?? AlwaysEmptyInventoryListPersistence.Instance,
            // Same reasoning as inventoryListPersistence above, for skills.
            skillPersistence ?? AlwaysEmptySkillPersistence.Instance,
            spatialInspector,
            monsterRuntime,
            collisionProvider,
            rates,
            players,
            playerVisibility,
            visibilityOptions,
            distributedWorld)
    {
        _iroAuthRequested = iroAuthenticated;
        _authRequested = iroAuthenticated;
        _mapName = mapName;
        _x = x;
        _y = y;
        _authenticated = iroAuthenticated;
        _accountId = accountId;
        _charId = charId;
        _playerLifecycle = iroAuthenticated
            ? PlayerSessionLifecycle.AuthenticatedButNotWorldVisible
            : PlayerSessionLifecycle.Unauthenticated;
    }

    public int SessionId { get; }

    // Read-only; never mutated after CompleteIroAuthenticationAsync sets it. Safe to read from any
    // thread (a plain uint field, assigned once during single-threaded auth completion before this
    // session is registered in MapTcpServer's _sessions - see that dictionary's own doc comment for
    // why a session is only ever discoverable there after authentication is complete).
    internal uint AccountId => _accountId;
    uint IPlayerPresenceObserver.ActorId => _accountId;
    internal string CurrentMapName => _mapName;
    // Syncs against real elapsed walking time on every read (no background timer - mirrors
    // CharacterStatusEffectState's lazy-on-read expiration model), so any caller (tests, a future
    // melee-range check, actor visibility) always observes the character's ACTUAL current cell
    // rather than a stale destination from the last movement packet.
    internal ushort CurrentX { get { SyncPositionToNow(); return _x; } }
    internal ushort CurrentY { get { SyncPositionToNow(); return _y; } }
    internal ScriptExecutionState? ActiveScriptState => _scriptExecutionSession?.State;
    internal string? ActiveGeneratedScriptEntityId => _generatedScriptEntityId;
    internal CharacterGameplayStateSession? GameplayState => _gameplayState;
    internal CharacterInventorySnapshot? Inventory => _inventory;
    internal CharacterEquipmentSnapshot? Equipment => _equipment;
    internal CharacterStatusEffectState StatusEffects => _statusEffects;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _sessionCancellation.Token);
        var sessionToken = linkedCancellation.Token;

        // Covers the already-authenticated test-facing constructor (iroAuthenticated: true), which
        // sets _authenticated directly and never goes through CompleteIroAuthenticationAsync - the
        // only other place that starts the runtime loops. Without this, such a session can accept a
        // movement packet (HandleIroMovementAsync) and register a deferred PendingMovementArrival
        // with no _movementLoop ever running to process it: the arrival is registered but never
        // fires, and any caller awaiting its effect (a warp/OnTouch packet) blocks forever. See
        // EnsureRuntimeLoopsStarted's own doc comment for why this must be idempotent/race-safe
        // against CompleteIroAuthenticationAsync's own call on the production path.
        if (_authenticated)
        {
            EnsureRuntimeLoopsStarted();
        }

        try
        {
            while (!sessionToken.IsCancellationRequested)
            {
                var packet = await ReadNextPacketAsync(_stream, sessionToken);
                if (packet.Length == 0)
                {
                    return;
                }

                var packetType = BinaryPrimitives.ReadInt16LittleEndian(packet);
                MapLogger.Info($"[iRO MAP DEBUG] Map client packet=0x{packetType:X4} len={packet.Length}");
                await HandlePacketAsync(packetType, packet, sessionToken);
            }
        }
        finally
        {
            // Disconnect/crash diagnostics (task requirement): whatever caused this loop to exit -
            // clean disconnect, cancellation, or an unhandled exception - the LAST packet this
            // session actually finished writing to the client is exactly what the client had
            // already received immediately beforehand. Logged unconditionally, before StopAsync's
            // own cleanup runs, so it is never lost if StopAsync itself throws.
            MapLogger.Info($"[iRO MAP DEBUG] Session ending map='{_mapName}' x={_x} y={_y} lastPacketWritten={_lastPacketWrittenDescription}");
            // StopAsync is idempotent and shared: whichever of RunAsync/DisposeAsync/an explicit
            // StopAsync call reaches it first performs the ONE shutdown sequence (cancel -> join both
            // runtime loops -> sync position -> persist once -> dispose resources); every other
            // caller just awaits the same in-flight or completed Task. This is what makes RunAsync's
            // contract "when RunAsync returns, no background scheduler remains alive" hold
            // unconditionally, not just on the happy path.
            await StopAsync();
        }
    }

    // Idempotent, concurrency-safe startup boundary for this session's two background schedulers.
    // Must be reachable from BOTH: (a) CompleteIroAuthenticationAsync, the production path, and (b)
    // RunAsync's own startup check for a session that was already authenticated through the
    // test-facing iroAuthenticated: true constructor (see RunAsync's doc comment). Those two paths
    // can race - HandleAuthOk's fire-and-forget CompleteIroAuthenticationSafelyAsync can still be
    // in flight when RunAsync's caller invokes this from the other path - so a bare "if (_field is
    // null) start" without a lock could start two independent status/movement loops. The lock body
    // never awaits: it only checks/sets a bool and kicks off the two fire-and-forget async methods,
    // so holding it is always momentary. Deliberately NOT started from a constructor: constructing a
    // session must never have an observable side effect beyond field initialization, and both
    // legitimate starting points (successful auth completion, RunAsync beginning for an
    // already-authenticated session) are only known well after construction.
    private void EnsureRuntimeLoopsStarted()
    {
        lock (_runtimeLoopStartGate)
        {
            if (_runtimeLoopsStarted)
            {
                return;
            }

            _runtimeLoopsStarted = true;
            _statusExpirationLoop = RunStatusExpirationLoopAsync(_sessionCancellation.Token);
            _movementLoop = RunMovementLoopAsync(_sessionCancellation.Token);
            _attackLoop = RunRepeatAttackLoopAsync(_sessionCancellation.Token);
        }
    }

    public void HandleAuthOk(MapAuthOkData authOk)
    {
        if (!_authRequested ||
            authOk.AccountId != _accountId ||
            authOk.CharId != _charId ||
            authOk.LoginId1 != _loginId1)
        {
            return;
        }

        if (_iroAuthRequested)
        {
            _mapName = authOk.MapName;
            _x = authOk.X;
            _y = authOk.Y;
            _ = CompleteIroAuthenticationSafelyAsync(authOk);
            return;
        }

        _ = SendAcceptEnterAsync(authOk, CancellationToken.None);
        _ = SendNotifyActorInitAsync(CancellationToken.None);
    }

    internal async Task CompleteIroAuthenticationAsync(MapAuthOkData authOk)
    {
        var state = await _gameplayStatePersistence.GetAsync(authOk.AccountId, authOk.CharId, _sessionCancellation.Token);
        if (state is null)
        {
            MapLogger.Warning($"[iRO MAP DEBUG] Character gameplay state load failed accountId={authOk.AccountId} charId={authOk.CharId}.");
            HandleAuthFail(); return;
        }

        var inventoryRead = await _inventoryListPersistence.GetInventoryAsync(authOk.AccountId, authOk.CharId, _sessionCancellation.Token);
        if (!inventoryRead.Succeeded)
        {
            MapLogger.Warning($"[iRO MAP DEBUG] Character inventory load failed accountId={authOk.AccountId} charId={authOk.CharId}.");
            HandleAuthFail(); return;
        }

        // Third fetch in the pre-bootstrap sequence, same fail-closed policy as gameplay state and
        // inventory above - an authenticated session must never have unknown skill state.
        var skillRead = await _skillPersistence.GetSkillsAsync(authOk.AccountId, authOk.CharId, _sessionCancellation.Token);
        if (!skillRead.Succeeded)
        {
            MapLogger.Warning($"[iRO MAP DEBUG] Character skill load failed accountId={authOk.AccountId} charId={authOk.CharId}.");
            HandleAuthFail(); return;
        }

        _accountId = authOk.AccountId;
        _charId = authOk.CharId;
        _mapName = authOk.MapName;
        _x = authOk.X;
        _y = authOk.Y;
        _gameplayState = new CharacterGameplayStateSession(authOk.AccountId, state, _gameplayStatePersistence, skillRead.Snapshot!, _skillPersistence);

        // Invariant: an authenticated session always has gameplay state AND inventory state
        // loaded. A failed/unavailable inventory read must never let a session become
        // authenticated with unknown inventory/equipment state - future combat/appearance code
        // must be able to trust that Inventory/Equipment are non-null whenever the session is
        // authenticated, and that a null Equipment.RightHandItemId means authoritatively
        // unarmed, never "unknown". CharacterEquipmentSnapshot is derived from the SAME
        // inventory read - never a second independent CharServer fetch.
        _inventory = inventoryRead.Snapshot;
        _equipment = CharacterEquipmentSnapshot.FromInventory(_inventory!);
        _characterName = authOk.CharacterName;
        _sex = authOk.Sex;
        _direction = authOk.Direction;
        _headDirection = 0;
        _authAppearance = new PlayerAuthAppearance(
            authOk.HairStyle, authOk.HairColor, authOk.ClothesColor, authOk.BodyStyle,
            authOk.WeaponAppearance, authOk.ShieldAppearance, authOk.HeadBottomAppearance,
            authOk.HeadTopAppearance, authOk.HeadMidAppearance, authOk.RobeAppearance,
            authOk.Option, authOk.Karma, authOk.Manner, authOk.Font);
        _authenticated = true; _positionDirty = false;
        lock (_playerPresenceGate) _playerLifecycle = PlayerSessionLifecycle.AuthenticatedButNotWorldVisible;
        MapLogger.Info($"[iRO MAP DEBUG] 0x0C1F MapAuthNode authentication succeeded accountId={authOk.AccountId} charId={authOk.CharId} sessionMatch=true gameplayStateVersion={state.Version}");
        EnsureRuntimeLoopsStarted();
        await SendIroInitialBootstrapAsync(authOk, _sessionCancellation.Token);
    }

    // One expiration scheduler per session (not one Task.Delay/Timer per active status).
    // Sleeps until CharacterStatusEffectState.NextExpiration via the shared TimeProvider (so
    // tests can drive it deterministically with a fake clock), waking early whenever
    // StartStatusAsync adds or refreshes a status (which can move the next deadline earlier).
    // Pinned status_change_end's generic tail (status.cpp:14047-14123, no SC_BLESSING/
    // SC_INCREASEAGI-specific case in the switch at status.cpp:13433-14045) is: send the
    // "off" status-change packet (clif_status_change with state=0 -> 0x0196 for this
    // PACKETVER), then recalculate and resync only the stats that actually changed
    // (status_calc_bl_ -> clif_updatestatus -> 0x0141) - exactly the sequence implemented here.
    private async Task RunStatusExpirationLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var next = _statusEffects.NextExpiration;
                if (next is null)
                {
                    await _statusExpirationSignal.WaitAsync(cancellationToken);
                    continue;
                }

                var delay = next.Value - _timeProvider.GetUtcNow();
                if (delay > TimeSpan.Zero)
                {
                    using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var wake = _statusExpirationSignal.WaitAsync(delayCancellation.Token);
                    var sleep = Task.Delay(delay, _timeProvider, delayCancellation.Token);
                    var completed = await Task.WhenAny(wake, sleep);
                    delayCancellation.Cancel();
                    if (completed == wake)
                    {
                        try { await wake; } catch (OperationCanceledException) { }
                        continue;
                    }
                    try { await sleep; } catch (OperationCanceledException) { continue; }
                }

                await ProcessDueStatusExpirationsAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    // KNOWN FUTURE CONCURRENCY-HARDENING ITEM (not addressed here - CharacterStatusEffectState's
    // internal dictionary race was the target of this fix, and is now closed: every _statuses
    // access is synchronized, ExpireDue's identify+remove is atomic, and Recalculate/
    // RecalculateBeforeExpiration each read one coherent snapshot). This method itself still
    // calls RecalculateBeforeExpiration -> ExpireDue -> Recalculate as three SEPARATELY
    // synchronized operations with no lock spanning all three. A concurrent Start() landing
    // between ExpireDue and the final Recalculate could theoretically produce a semantic
    // expiration/reapply ordering race (e.g. "after" observing a status that started after
    // "before" was captured) even though the dictionary itself can no longer throw. Revisit if
    // this becomes observable; not changing the status architecture as part of this PR.
    private async Task ProcessDueStatusExpirationsAsync(CancellationToken cancellationToken)
    {
        var before = _gameplayState is null ? default : _statusEffects.RecalculateBeforeExpiration(_gameplayState.State);
        var due = _statusEffects.ExpireDue(_timeProvider.GetUtcNow());
        if (due.Count == 0 || _gameplayState is null) return;
        var after = _statusEffects.Recalculate(_gameplayState.State);

        foreach (var status in due)
        {
            ushort efstType;
            if (status.StatusId == CharacterStatusEffectState.StatusIds.Blessing) efstType = IroStatusEffectPackets.EfstBlessing;
            else if (status.StatusId == CharacterStatusEffectState.StatusIds.IncreaseAgi) efstType = IroStatusEffectPackets.EfstIncAgi;
            else continue;

            await WriteAsync(IroStatusEffectPackets.BuildStatusChangeEnd(_accountId, efstType), cancellationToken);
        }

        if (before.Strength != after.Strength) await WriteAsync(IroStatusEffectPackets.BuildCoupleStatus(IroStatusEffectPackets.SpStr, _gameplayState.State.Strength, after.Strength - _gameplayState.State.Strength), cancellationToken);
        if (after.Agility != before.Agility) await WriteAsync(IroStatusEffectPackets.BuildCoupleStatus(IroStatusEffectPackets.SpAgi, _gameplayState.State.Agility, after.Agility - _gameplayState.State.Agility), cancellationToken);
        if (before.Intelligence != after.Intelligence) await WriteAsync(IroStatusEffectPackets.BuildCoupleStatus(IroStatusEffectPackets.SpInt, _gameplayState.State.Intelligence, after.Intelligence - _gameplayState.State.Intelligence), cancellationToken);
        if (before.Dexterity != after.Dexterity) await WriteAsync(IroStatusEffectPackets.BuildCoupleStatus(IroStatusEffectPackets.SpDex, _gameplayState.State.Dexterity, after.Dexterity - _gameplayState.State.Dexterity), cancellationToken);
    }

    // One movement scheduler per session (not one Task.Delay/Timer per cell), mirroring
    // RunStatusExpirationLoopAsync's exact shape: sleep until CharacterMovementState.NextStepDueAt
    // via the shared TimeProvider, waking early whenever HandleIroMovementAsync starts or retargets
    // a walk (which can move the next deadline earlier, or wake the loop from indefinite waiting).
    // This is what makes per-cell OnTouch/warp evaluation happen when a cell is actually reached
    // instead of only when some later packet/property-read happens to call AdvanceTo.
    private async Task RunMovementLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                DateTimeOffset? next;
                await _movementGate.WaitAsync(cancellationToken);
                try { next = _movement?.NextStepDueAt; }
                finally { _movementGate.Release(); }

                if (next is null)
                {
                    await _movementSignal.WaitAsync(cancellationToken);
                    continue;
                }

                var delay = next.Value - _timeProvider.GetUtcNow();
                if (delay > TimeSpan.Zero)
                {
                    using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var wake = _movementSignal.WaitAsync(delayCancellation.Token);
                    var sleep = Task.Delay(delay, _timeProvider, delayCancellation.Token);
                    var completed = await Task.WhenAny(wake, sleep);
                    delayCancellation.Cancel();
                    if (completed == wake)
                    {
                        try { await wake; } catch (OperationCanceledException) { }
                        continue;
                    }
                    try { await sleep; } catch (OperationCanceledException) { continue; }
                }

                await ProcessDueMovementAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    // Advances the walk to `now`, processing every newly-crossed cell IN ORDER. Only the cell that
    // is both (a) newly crossed and (b) the walk's final destination can carry a pending warp/script
    // touch action (HandleIroMovementAsync only ever attaches one to the destination of a truncated
    // path - see its own comments) - so evaluating "is this the last crossed cell AND is there a
    // pending arrival" is sufficient to fire it exactly once, at the moment that cell is truly
    // reached, matching rAthena's per-cell touch checks (unit.cpp:684-699) rather than firing
    // instantly at click time. If that action changes the map (a warp), TeleportTo resets _movement
    // and clears _pendingArrival before this method continues, so no further stale processing can
    // occur for a walk that no longer exists.
    private async Task ProcessDueMovementAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<(ushort X, ushort Y)> crossed;
        PendingMovementArrival? arrival;
        string mapAtAdvance;
        (ushort X, ushort Y)? movementDestinationAfterAdvance;
        // Set only when a deferred retarget (HandleIroMovementAsync's own "mid-walk" branch) was
        // actually consumed and applied THIS call - used purely to decide whether to send a fresh
        // 0x0087 + visibility refresh below, after the gate is released (see this method's own
        // "Movement retarget applied" diagnostic and requirement 1's pinned clif_walkok trace:
        // unit_walktoxy_sub's own unit_walktoxy_nextcell(*bl, true, ...) call is exactly what makes
        // clif_move+clif_walkok fire together at this cell boundary, carrying the REPLACEMENT path's
        // src=reached-cell/dst=latest-requested-destination - never the stale click-time response).
        (ushort FromX, ushort FromY, ResolvedMovementTarget Resolved)? appliedRetarget = null;
        await _movementGate.WaitAsync(cancellationToken);
        try
        {
            var movement = _movement;
            if (movement is null) return;
            crossed = movement.AdvanceTo(_timeProvider.GetUtcNow());
            if (crossed.Count == 0) return;
            _x = movement.CurrentX;
            _y = movement.CurrentY;
            _positionDirty = true;

            // Pinned unit_walktoxy_timer's own change_walk_target check (unit.cpp:738-744), run
            // immediately after this cell arrival, BEFORE ever considering _pendingArrival for the
            // walk's ORIGINAL destination - a pending retarget always takes priority the moment a
            // cell boundary is reached, exactly matching pinned source's own ordering. Requirement
            // 7: recompute (never reuse) the warp/script intersection here, from the cell the
            // character ACTUALLY just reached - the ORIGINAL _pendingArrival is unconditionally
            // replaced (or cleared to null, if the replacement path has none) by this recomputation,
            // so a stale warp/OnTouch belonging to the walk that was just replaced can never fire.
            var pendingRetarget = movement.ConsumePendingRetarget();
            if (pendingRetarget is { } retarget)
            {
                var fromX = _x;
                var fromY = _y;
                var resolved = ResolveMovementTarget(fromX, fromY, retarget.X, retarget.Y);
                // CurrentCellReachedAt (the exact boundary AdvanceTo just crossed to), NEVER a
                // second independent _timeProvider.GetUtcNow() sample here - re-sampling wall-clock
                // time between the AdvanceTo call above and this StartWalk silently gifts the
                // replacement step a few extra milliseconds every retarget, observed live as a
                // small compounding speed-up/hop on repeated mid-walk retargets. See
                // CharacterMovementState.CurrentCellReachedAt's own doc comment.
                movement.StartWalk(resolved.Path, CurrentCellDurationMs(), movement.CurrentCellReachedAt);
                _pendingArrival = resolved.Arrival;
                appliedRetarget = (fromX, fromY, resolved);
            }

            arrival = movement.IsMoving ? null : _pendingArrival; // Only relevant once the walk actually finished.
            mapAtAdvance = _mapName;
            movementDestinationAfterAdvance = movement.IsMoving ? movement.Destination : null;
        }
        finally { _movementGate.Release(); }

        await UpdatePresenceForCrossedCellsAsync(crossed, movementDestinationAfterAdvance, cancellationToken);

        if (appliedRetarget is { } applied)
        {
            MapLogger.Info(
                $"[iRO MAP DEBUG] Movement retarget applied from=({applied.FromX},{applied.FromY}) target=({applied.Resolved.TargetX},{applied.Resolved.TargetY})");
            var retargetTick = unchecked((uint)Environment.TickCount);
            var retargetResponse = IroMovementPackets.BuildResponse(
                retargetTick, applied.FromX, applied.FromY, applied.Resolved.TargetX, applied.Resolved.TargetY);
            MapLogger.Info(
                $"[iRO MAP DEBUG] Sending 0x0087 len=12 from=({applied.FromX},{applied.FromY}) to=({applied.Resolved.TargetX},{applied.Resolved.TargetY}) (mid-walk retarget)");
            await WriteAsync(retargetResponse, cancellationToken);
            await StartPresenceMovementAsync(applied.FromX, applied.FromY, applied.Resolved.TargetX, applied.Resolved.TargetY, retargetTick, cancellationToken);
            if (!applied.Resolved.IntersectsWarp && !applied.Resolved.IntersectsScript)
            {
                await SendVisibleWarpActorsAsync(cancellationToken);
                await SendVisibleMonsterActorsAsync(cancellationToken);
            }
        }

        if (arrival is null) return;

        // Consume the pending arrival before executing it (a re-entrant movement request arriving
        // while the arrival action itself awaits below must not also see/re-fire it, and TeleportTo
        // -triggered-by-the-arrival-action-itself will independently clear it again harmlessly).
        await _movementGate.WaitAsync(cancellationToken);
        try
        {
            if (!string.Equals(_mapName, mapAtAdvance, StringComparison.OrdinalIgnoreCase) || !ReferenceEquals(_pendingArrival, arrival)) return;
            _pendingArrival = null;
        }
        finally { _movementGate.Release(); }

        switch (arrival)
        {
            case PendingWarpArrival warpArrival:
                MapLogger.Info($"[iRO MAP DEBUG] Movement reached warp cell map='{mapAtAdvance}' at=({_x},{_y})");
                await SendSameServerWarpAsync(warpArrival.Warp, cancellationToken);
                break;
            case PendingScriptTouchArrival scriptArrival:
                await SendVisibleWarpActorsAsync(cancellationToken);
                MapLogger.Info($"[iRO MAP DEBUG] Movement reached script trigger entity='{scriptArrival.Entity.Id}' map='{mapAtAdvance}' at=({_x},{_y})");
                await StartScriptAsync(scriptArrival.Entity, scriptArrival.ActorId, scriptArrival.Script, "OnTouch", cancellationToken);
                break;
        }
    }

    private async Task CompleteIroAuthenticationSafelyAsync(MapAuthOkData authOk)
    {
        try
        {
            await CompleteIroAuthenticationAsync(authOk);
        }
        catch (OperationCanceledException) when (_sessionCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            MapLogger.Warning(
                $"[iRO MAP DEBUG] Character gameplay state initialization failed " +
                $"accountId={authOk.AccountId} charId={authOk.CharId} error={ex.GetType().Name}.");
            HandleAuthFail();
        }
    }

    public void HandleAuthFail()
    {
        if (!_authRequested)
        {
            return;
        }

        _ = SendRefuseEnterAsync(0, CancellationToken.None);
    }

    // Idempotent, shared async shutdown. Every caller (RunAsync's finally, DisposeAsync, or a direct
    // StopAsync call from a test) observes the exact same Task and therefore the exact same
    // completion/exception - there is only ever one shutdown in flight, and only one final position
    // persistence. The lock body never awaits (it only checks/creates the Task field), so it can
    // never block another thread's shutdown attempt.
    //
    // Deliberately NOT `Dispose() => StopAsync().GetAwaiter().GetResult()`: MapClientSession's
    // background loops must be genuinely joined (awaited), not blocked-on from a sync frame, so the
    // primary lifetime contract is IAsyncDisposable. Every construction site in this codebase is
    // already inside an async method (MapTcpServer.HandleClientAsync, every test's async Task
    // [Fact]), so there is no call site that actually needs synchronous disposal.
    public Task StopAsync()
    {
        lock (_shutdownGate)
        {
            return _shutdownTask ??= StopCoreAsync();
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task StopCoreAsync()
    {
        _sessionCancellation.Cancel();
        // Unblock a pending WaitAsync so a parked loop observes cancellation promptly instead of
        // waiting for its own indefinite signal wait to somehow resolve; a wake may already be
        // pending (SemaphoreFullException), which is harmless here too.
        try { _statusExpirationSignal.Release(); } catch (ObjectDisposedException) { } catch (SemaphoreFullException) { }
        try { _movementSignal.Release(); } catch (ObjectDisposedException) { } catch (SemaphoreFullException) { }
        try { _attackSignal.Release(); } catch (ObjectDisposedException) { } catch (SemaphoreFullException) { }
        _generatedContinuation?.Completion.TrySetCanceled();

        // Join ALL runtime loops before touching anything they can still access. This is the
        // invariant the earlier lifecycle audit found missing: cancellation is only a request: it
        // does not guarantee any loop has actually stopped reading _movement/_statusEffects/
        // _repeatAttack or calling WriteAsync. Some loops may not be running yet (auth never
        // completed), hence the null filter.
        var loops = new[] { _statusExpirationLoop, _movementLoop, _attackLoop }.Where(loop => loop is not null)!;
        Exception? firstError = null;
        try
        {
            await Task.WhenAll(loops!);
        }
        catch (Exception ex)
        {
            // Task.WhenAll only surfaces the first faulting task's exception via `await`, but both
            // loops already swallow every exception they can produce internally (OperationCanceled/
            // ObjectDisposed guarded by cancellationToken.IsCancellationRequested) - so reaching here
            // at all means something unexpected escaped a loop. Preserve it instead of losing it to
            // the resource cleanup below; still reachable in the finally-equivalent path.
            firstError = ex;
        }

        // Safe only now: RunMovementLoopAsync/RunStatusExpirationLoopAsync are confirmed no longer
        // running, so nothing else can mutate _movement/_pendingArrival/_statusEffects concurrently.
        // SyncPositionToNow only advances CharacterMovementState's elapsed-time position and reads it
        // back - it does NOT consult _pendingArrival, so no warp/OnTouch side effect can fire here;
        // those are owned exclusively by ProcessDueMovementAsync, which cannot run anymore.
        try
        {
            await _movementGate.WaitAsync(CancellationToken.None);
            try { SyncPositionToNow(); }
            finally { _movementGate.Release(); }

            await LeavePlayerWorldAsync(PlayerSessionLifecycle.Closed, CancellationToken.None);
            await PersistPositionIfDirtyAsync(CancellationToken.None);
        }
        catch (Exception ex) when (firstError is null)
        {
            firstError = ex;
        }
        catch
        {
            // A loop already faulted; do not let a second failure here replace the first one below.
        }
        finally
        {
            _stream.Dispose();
            _writeLock.Dispose();
            _sessionCancellation.Dispose();
            _statusExpirationSignal.Dispose();
            _movementSignal.Dispose();
            _movementGate.Dispose();
            _attackSignal.Dispose();
            _attackGate.Dispose();
        }

        if (firstError is not null)
        {
            ExceptionDispatchInfo.Throw(firstError);
        }
    }

    private async Task HandlePacketAsync(short packetType, byte[] packet, CancellationToken cancellationToken)
    {
        switch (packetType)
        {
            case PacketConstants.CzEnter:
            case PacketConstants.CzEnter2:
                await HandleEnterAsync(packet, cancellationToken);
                break;
            case PacketConstants.CzNotifyActorInit:
                if (_iroAuthRequested)
                {
                    MapLogger.Info(
                        $"[iRO MAP DEBUG] Received stock iRO map-loaded packet=0x{packetType:X4} len={packet.Length}");
                    // Pinned ordering (clif_parse_LoadEndAck, clif.cpp:10748-10817): self weapon
                    // look (clif_changelook, target=AREA which includes self), THEN the self
                    // inventory/equip-list projection (clif_inventorylist, target=SELF), both
                    // BEFORE the AREA_WOS spawn broadcast that other visible actors receive - so
                    // both go first, ahead of SendVisibleWarpActorsAsync/SendVisibleMonsterActorsAsync.
                    await SendSelfWeaponAppearanceAsync(cancellationToken);
                    await SendSelfInventoryAsync(cancellationToken);
                    _visibleActorIds.Clear();
                    await EnterPlayerWorldAsync(cancellationToken);
                    await SendVisibleWarpActorsAsync(cancellationToken);
                    await SendVisibleMonsterActorsAsync(cancellationToken);
                    foreach (var navigation in _worldMapRegistry.GetNavigationAt(_mapName, _x, _y))
                    {
                        MapLogger.Info(
                            $"[iRO MAP DEBUG] Sending 0x08E2 navigation entityId='{navigation.EntityId}' current='{_mapName}'({_x},{_y}) -> dest='{navigation.DestinationMap}'({navigation.DestinationX},{navigation.DestinationY})");
                        await WriteAsync(IroNpcDialoguePackets.BuildNavigateTo(navigation.DestinationMap, navigation.DestinationX, navigation.DestinationY), cancellationToken);
                    }
                    break;
                }

                await SendNotifyActorInitAsync(cancellationToken);
                break;
            case PacketConstants.CzClientVersion:
                break;
            case PacketConstants.CzPingLive:
                await SendPingLiveAsync(cancellationToken);
                break;
            case PacketConstants.IroCzMapAuth:
                MapLogger.Info(
                    $"[iRO MAP DEBUG] Received stock iRO map auth packet=0x{packetType:X4} len={packet.Length}");
                await HandleIroMapAuthAsync(packet, cancellationToken);
                break;
            case PacketConstants.IroCzPostEnter0360 when _iroAuthRequested:
                MapLogger.Info(
                    $"[iRO MAP DEBUG] Reached next post-enter client boundary packet=0x{packetType:X4} len={packet.Length}");
                break;
            case PacketConstants.IroCzPostEnter08c9 when _iroAuthRequested:
                MapLogger.Info(
                    $"[iRO MAP DEBUG] Received opaque stock iRO packet=0x{packetType:X4} len={packet.Length}");
                break;
            case PacketConstants.IroCzRequestMove when _iroAuthRequested:
                await HandleIroMovementAsync(packet, cancellationToken);
                break;
            case PacketConstants.IroCzActorInfoRequest when _iroAuthRequested:
                MapLogger.Info(
                    $"[iRO MAP DEBUG] Received stock iRO actor-info request packet=0x{packetType:X4} len={packet.Length}");
                var requestedActorId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2));
                if ((requestedActorId == _accountId || _visibleActorIds.IsActorVisible(requestedActorId)) && _players.TryGetByActorId(requestedActorId, out var playerPresence))
                {
                    await WriteAsync(IroPlayerActorPackets.BuildPlayerInfo(playerPresence), cancellationToken);
                }
                else if (_visibleActorIds.IsActorVisible(requestedActorId) && _worldMapRegistry.TryGetActorName(requestedActorId, _mapName, out var actorName))
                {
                    MapLogger.Info($"[iRO MAP DEBUG] Sending 0x0ADF NPC name actorId={requestedActorId} name='{actorName}'");
                    await WriteAsync(IroWorldActorPackets.BuildNpcName(requestedActorId, actorName), cancellationToken);
                }
                else if (_visibleActorIds.IsActorVisible(requestedActorId) && _monsters is not null && _monsters.TryGetInstance(requestedActorId, _mapName, out var monsterInstance))
                {
                    var monsterName = monsterInstance.Spawn.Mob.Name;
                    MapLogger.Info($"[iRO MAP DEBUG] Sending 0x0ADF monster name actorId={requestedActorId} name='{monsterName}'");
                    LogMonsterCellDiagnostics(requestedActorId);
                    await WriteAsync(IroWorldActorPackets.BuildNpcName(requestedActorId, monsterName), cancellationToken);
                }
                break;
            case PacketConstants.IroCzChangeDirection when _iroAuthRequested:
                if (IroChangeDirectionPacket.TryParse(packet, out var direction))
                {
                    MapLogger.Info($"[iRO MAP DEBUG] Received stock iRO change-direction packet=0x{packetType:X4} headDirection={direction.HeadDirection} bodyDirection={direction.BodyDirection}");
                    _headDirection = direction.HeadDirection;
                    _direction = direction.BodyDirection;
                    var current = CurrentPresence();
                    if (current is not null)
                    {
                        var changed = current with { HeadDirection = _headDirection, Direction = _direction };
                        SetCurrentPresence(changed);
                        await _playerVisibility.UpdateLookAsync(changed, cancellationToken);
                    }
                }
                break;
            case PacketConstants.IroCzNpcInteraction when _iroAuthRequested:
                await HandleNpcInteractionAsync(packet, cancellationToken);
                break;
            case PacketConstants.IroCzNpcNext when _iroAuthRequested:
                await HandleNpcNextAsync(packet, cancellationToken);
                break;
            case PacketConstants.IroCzNpcClose when _iroAuthRequested:
                if (IroNpcDialoguePackets.TryParseClose(packet, out var closeActorId))
                {
                    if (_scriptExecutionSession?.ActorId == closeActorId) _scriptExecutionSession = null;
                    if (_generatedScriptActorId == closeActorId)
                    {
                        if (!await TryResumeGeneratedScriptAsync(closeActorId, GeneratedContinuationKind.Close2, 0, cancellationToken))
                            _generatedContinuation?.Completion.TrySetCanceled();
                    }
                }
                break;
            case PacketConstants.IroCzNpcSelection when _iroAuthRequested:
                await HandleNpcSelectionAsync(packet, cancellationToken);
                break;
            case PacketConstants.IroCzAttackRequest when _iroAuthRequested:
                await HandleIroAttackRequestAsync(packet, cancellationToken);
                break;
            case PacketConstants.IroCzReqWearEquip when _iroAuthRequested:
                await HandleEquipRequestAsync(packet, cancellationToken);
                break;
            case PacketConstants.IroCzReqTakeoffEquip when _iroAuthRequested:
                await HandleUnequipRequestAsync(packet, cancellationToken);
                break;
            case PacketConstants.IroCzUseItem when _iroAuthRequested:
                await HandleIroUseItemRequestAsync(packet, cancellationToken);
                break;
            case PacketConstants.IroCzSkillLevelUp when _iroAuthRequested:
                await HandleIroSkillLevelUpRequestAsync(packet, cancellationToken);
                break;
            case PacketConstants.IroCzStatusUp when _iroAuthRequested:
                await HandleIroStatusUpRequestAsync(packet, cancellationToken);
                break;
            default:
                LogUnsupportedPacket(packetType, packet);
                RequestClose();
                break;
        }
    }

    private async Task HandleIroMapAuthAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (_authRequested || !IroMapAuthPacket.TryParse(packet, out var auth))
        {
            await SendRefuseEnterAsync(0, cancellationToken);
            return;
        }

        _accountId = auth.AccountId;
        _charId = auth.CharId;
        _loginId1 = auth.LoginId1;
        MapLogger.Info(
            $"[iRO MAP DEBUG] Parsed 0x0C1F accountId={_accountId} charId={_charId}");

        if (_accountId == 0 || _charId == 0)
        {
            await SendRefuseEnterAsync(0, cancellationToken);
            return;
        }

        var endpoint = _client.Client.RemoteEndPoint as IPEndPoint;
        var clientIp = endpoint?.Address ?? IPAddress.Loopback;
        if (!_charConnector.TrySendIroAuthRequest(this, _accountId, _charId, _loginId1, clientIp))
        {
            await SendRefuseEnterAsync(0, cancellationToken);
            MapLogger.Warning("iRO auth request to char server failed. Disconnecting map client.");
            RequestClose();
            return;
        }

        _iroAuthRequested = true;
        _authRequested = true;
    }

    // Lazily attaches CharacterMovementState to the session's current (_mapName,_x,_y) the first
    // time movement matters - _mapName/_x/_y are set earlier by several independent auth/warp paths
    // (HandleAuthOk, CompleteIroAuthenticationAsync, etc.), so the movement state cannot simply be
    // constructed once in the constructor before any of those have run.
    private CharacterMovementState EnsureMovementState()
    {
        _movement ??= new CharacterMovementState(_mapName, _x, _y);
        return _movement;
    }

    // The single place _mapName/_x/_y are assigned for a warp/map-change/auth-position-restore
    // (never a timed walk - see HandleIroMovementAsync for that path). Also resets _movement via
    // CharacterMovementState.Teleport so a stale in-flight walk from before the teleport can never
    // be advanced afterward - a same-map teleport (e.g. a same-map warp) would not otherwise be
    // caught by EnsureMovementState's now-removed map-mismatch check, which is exactly the bug this
    // helper fixes: MapClientSessionWarpTests.MovementIntoTutorialDoor_... teleports within
    // "iz_int03", so map-equality alone cannot detect that the previous walk state is stale.
    private void TeleportTo(string map, ushort x, ushort y)
    {
        _mapName = map;
        _x = x;
        _y = y;
        _movement?.Teleport(map, x, y);

        // Invalidate any pending arrival action from the walk being cancelled - otherwise a movement
        // loop iteration already in flight (or a later one, if this teleport did not itself originate
        // from the pending arrival) could still fire a stale warp/OnTouch belonging to the walk that
        // was just replaced by this teleport.
        _pendingArrival = null;

        // Pinned unit_walktoxy (unit.cpp:888) unconditionally calls unit_stop_attack before
        // starting any walk - a map change/warp is the strongest form of that. Clearing the
        // in-memory reference is enough: RunRepeatAttackLoopAsync re-reads _repeatAttack fresh on
        // every iteration, so a loop iteration already in flight for the old target simply finds
        // nothing to do next time it wakes.
        _attackGate.Wait();
        try { _repeatAttack = null; }
        finally { _attackGate.Release(); }
    }

    // Reconciles _x/_y against real elapsed walking time. Pinned rAthena's authoritative position
    // (unit_walktoxy_timer, unit.cpp:542) only ever advances one cell per real CellDurationMs -
    // never jumps straight to a requested destination - so this must run before any code (a new
    // movement request's `from`, a future melee-range check, etc.) treats _x/_y as "where the
    // character actually is right now".
    private void SyncPositionToNow()
    {
        var movement = EnsureMovementState();
        movement.AdvanceTo(_timeProvider.GetUtcNow());
        _x = movement.CurrentX;
        _y = movement.CurrentY;
    }

    private int CurrentCellDurationMs()
    {
        var haste = _gameplayState is null ? 0 : _statusEffects.Recalculate(_gameplayState.State).MoveSpeedHaste;
        return MovementSpeedCalculator.CellDurationMs(haste);
    }

    // Resolves a requested destination into: the actual movement target (truncated to the first
    // warp/script-touch cell along the route, if any, exactly like the destination itself
    // otherwise), the path to it from `fromX,fromY`, and the PendingMovementArrival to attach - one
    // shared computation used both by a fresh walk start (HandleIroMovementAsync) and by applying a
    // deferred mid-walk retarget at the cell boundary where it takes effect
    // (ProcessDueMovementAsync) - requirement 7's "recompute pending arrival according to the
    // ultimately active path" means this warp/script intersection logic must run again from
    // wherever the character ACTUALLY is when the retarget is applied, never reused from click-time.
    private readonly record struct ResolvedMovementTarget(
        ushort TargetX, ushort TargetY, IReadOnlyList<(ushort X, ushort Y)> Path, PendingMovementArrival? Arrival,
        bool IntersectsWarp, bool IntersectsScript, WarpIntersection Warp, ScriptTouchIntersection Script);

    private ResolvedMovementTarget ResolveMovementTarget(ushort fromX, ushort fromY, ushort requestedX, ushort requestedY)
    {
        var intersectsWarp = _worldMapRegistry.TryFindFirstWarpAlongRoute(
            _mapName, fromX, fromY, requestedX, requestedY, out var intersection);
        ScriptTouchIntersection scriptIntersection = default;
        var intersectsScript = !HasActiveScript && _worldMapRegistry.TryFindFirstScriptTouchEnterAlongRoute(
            _mapName, fromX, fromY, requestedX, requestedY, out scriptIntersection);
        if (intersectsWarp && intersectsScript && Distance(fromX, fromY, scriptIntersection.X, scriptIntersection.Y) < Distance(fromX, fromY, intersection.X, intersection.Y))
            intersectsWarp = false;
        else if (intersectsWarp)
            intersectsScript = false;

        var targetX = intersectsWarp ? intersection.X : intersectsScript ? scriptIntersection.X : requestedX;
        var targetY = intersectsWarp ? intersection.Y : intersectsScript ? scriptIntersection.Y : requestedY;
        var path = _movementPathProvider.ComputePath(_mapName, fromX, fromY, targetX, targetY);
        PendingMovementArrival? arrival = intersectsWarp
            ? new PendingWarpArrival(intersection.Warp)
            : intersectsScript
                ? new PendingScriptTouchArrival(scriptIntersection.Binding.Entity, scriptIntersection.Binding.Actor.ActorId, scriptIntersection.Binding.Script)
                : null;

        return new ResolvedMovementTarget(targetX, targetY, path, arrival, intersectsWarp, intersectsScript, intersection, scriptIntersection);
    }

    private async Task HandleIroMovementAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!IroMovementPackets.TryParseRequest(packet, out var request))
        {
            RequestClose();
            return;
        }

        // Pinned unit_walktoxy (unit.cpp:888) unconditionally calls unit_stop_attack REGARDLESS of
        // whether this becomes a fresh walk or a mid-walk retarget - a real client movement request
        // always cancels any active repeat attack.
        await _attackGate.WaitAsync(cancellationToken);
        try { _repeatAttack = null; }
        finally { _attackGate.Release(); }

        // Pinned unit_walktoxy (unit.cpp:884-899): "ud->to_x = x; ud->to_y = y; ... if
        // (ud->walktimer != INVALID_TIMER) { ud->state.change_walk_target = 1; return 1; }" - a
        // retarget arriving WHILE a step is already in flight does not touch the in-flight step at
        // all (no SyncPositionToNow/path recompute/StartWalk here): it only records the desired
        // destination, deferring everything else to the next real cell boundary
        // (ProcessDueMovementAsync's own ConsumePendingRetarget handling) - exactly matching pinned
        // unit_walktoxy_timer's own change_walk_target check (unit.cpp:738-744). This is the fix for
        // the live stutter/jump-forward bug: resetting _stepStartedAt here on every rapid 0x035F
        // would discard whatever real progress had already elapsed through the current cell.
        var movementState = EnsureMovementState();
        if (movementState.IsMoving)
        {
            movementState.RequestRetarget(request.TargetX, request.TargetY);
            MapLogger.Info(
                $"[iRO MAP DEBUG] Movement retarget deferred current=({movementState.CurrentX},{movementState.CurrentY}) requested=({request.TargetX},{request.TargetY}) nextCell={movementState.NextCell} currentStepDueAt={movementState.NextStepDueAt:O}");
            return;
        }

        // Not currently moving: matches pinned unit_walktoxy's OTHER branch (walktimer ==
        // INVALID_TIMER) - unit_walktoxy_sub runs immediately, computing the path from wherever the
        // character stands right now and beginning to walk it without waiting for any cell boundary.
        SyncPositionToNow();
        var fromX = _x;
        var fromY = _y;
        MapLogger.Info(
            $"[iRO MAP DEBUG] Movement request from=({fromX},{fromY}) target=({request.TargetX},{request.TargetY})");

        var resolved = ResolveMovementTarget(fromX, fromY, request.TargetX, request.TargetY);
        if (_distributedWorld is not null)
        {
            var presenceId = _presenceId ?? throw new InvalidOperationException("Authenticated movement has no world presence identity.");
            var worldMove = await _distributedWorld.MovePlayerAsync(
                new WorldMovementCommand(
                    presenceId,
                    _charId,
                    _mapName,
                    fromX,
                    fromY,
                    resolved.TargetX,
                    resolved.TargetY),
                cancellationToken);
            if (worldMove.Status != WorldMovementStatus.Moved)
            {
                MapLogger.Warning($"World authority rejected movement status={worldMove.Status} map='{_mapName}' from=({fromX},{fromY}) target=({resolved.TargetX},{resolved.TargetY}).");
                return;
            }
            if (worldMove.Path is { Count: > 1 })
                resolved = resolved with { Path = worldMove.Path.Select(cell => (cell.X, cell.Y)).ToArray() };
        }
        var now = _timeProvider.GetUtcNow();

        // Warp/OnTouch must fire only when the destination cell is actually reached over real
        // elapsed time (RunMovementLoopAsync/ProcessDueMovementAsync), matching rAthena's per-cell
        // npc_touch_area_allnpc/npc_touch_areanpc2 checks inside unit_walktoxy_timer - not the moment
        // the client clicks. Attach the pending action here (under the gate, alongside StartWalk) so
        // the movement loop can execute it exactly once, at true arrival.
        await _movementGate.WaitAsync(cancellationToken);
        try
        {
            movementState.StartWalk(resolved.Path, CurrentCellDurationMs(), now);
            _pendingArrival = resolved.Arrival;
        }
        finally { _movementGate.Release(); }
        _positionDirty = true;
        try { _movementSignal.Release(); } catch (SemaphoreFullException) { }

        var movementTick = unchecked((uint)Environment.TickCount);
        var response = IroMovementPackets.BuildResponse(
            movementTick,
            fromX,
            fromY,
            resolved.TargetX,
            resolved.TargetY);
        MapLogger.Info(
            $"[iRO MAP DEBUG] Sending 0x0087 len=12 from=({fromX},{fromY}) to=({resolved.TargetX},{resolved.TargetY})");
        await WriteAsync(response, cancellationToken);
        await StartPresenceMovementAsync(fromX, fromY, resolved.TargetX, resolved.TargetY, movementTick, cancellationToken);

        if (resolved.IntersectsWarp)
        {
            MapLogger.Info(
                $"[iRO MAP DEBUG] Movement path intersects warp map='{_mapName}' at=({resolved.Warp.X},{resolved.Warp.Y}) requestedTarget=({request.TargetX},{request.TargetY}) (deferred to actual arrival)");
        }
        else if (resolved.IntersectsScript)
        {
            MapLogger.Info($"[iRO MAP DEBUG] Movement path intersects script trigger entity='{resolved.Script.Binding.Entity.Id}' map='{_mapName}' at=({resolved.Script.X},{resolved.Script.Y}) (deferred to actual arrival)");
        }
        else
        {
            await SendVisibleWarpActorsAsync(cancellationToken);
            await SendVisibleMonsterActorsAsync(cancellationToken);
        }
    }

    private static long Distance(ushort x1, ushort y1, ushort x2, ushort y2)
    {
        var dx = (long)x2 - x1; var dy = (long)y2 - y1; return dx * dx + dy * dy;
    }

    // Diagnostic-only (source-neutral: does not decide spawn eligibility or gameplay behavior on
    // its own). Lets a tester identify a visually suspicious monster (e.g. one that appears to
    // stand on water/mountain) by hovering/clicking it in the stock client, which sends the
    // existing proven 0x0368 actor-info request this already handles, and correlates the CURRENT
    // actorId (assigned by WorldActorIdAllocator well after any spawn-time selection decision) to
    // its live position/cell state via MonsterSpatialInspector - see that type's own doc comment
    // for why spawn-time-selector-level diagnostics alone cannot answer "what is at actorId N
    // right now". See the investigation notes in ai/world-data.md for why blanket-forbidding
    // MapCellFlags.Water or inventing a stronger connectivity rule is NOT done here; this only
    // reports the exact static cell state so that question can be answered from real map_cache.dat
    // data instead of a screenshot guess.
    private void LogMonsterCellDiagnostics(uint actorId)
    {
        if (_spatialInspector is null) return;
        if (!_spatialInspector.TryDescribe(actorId, _mapName, out var diagnostics)) return;

        MapLogger.Info(
            $"[iRO MAP DEBUG][MONSTER CELL] actorId={diagnostics.ActorId} mob={diagnostics.MobAegisName} " +
            $"map='{diagnostics.Map}' x={diagnostics.X} y={diagnostics.Y} flags='{diagnostics.Flags}' " +
            $"walkable={diagnostics.IsWalkable} water={diagnostics.IsWater} " +
            $"shootable={diagnostics.IsShootable} traversal={diagnostics.IsTraversalCell}");
    }

    private async Task SendSameServerWarpAsync(WarpDefinition warp, CancellationToken cancellationToken)
    {
        _scriptExecutionSession = null;
        var authoritativeSourceMap = _presenceMapId ?? _mapName;
        await LeavePlayerWorldAsync(PlayerSessionLifecycle.AuthenticatedButNotWorldVisible, cancellationToken);
        _visibleActorIds.Clear();
        foreach (var action in warp.OrderedActions)
        {
            if (action is SetSavePointAction savePoint)
            {
                // The CharServer persistence contract currently owns only last position.
                // Preserve ordering and data, but do not pretend savepoint persistence succeeded.
                MapLogger.Info($"[iRO MAP DEBUG] SetSavePoint deferred map='{savePoint.Map}' x={savePoint.X} y={savePoint.Y}");
                continue;
            }

            if (action is WarpAction warpAction)
            {
                var sourceMap = _mapName;
                var sourceX = _x;
                var sourceY = _y;
                MapLogger.Info($"[iRO MAP DEBUG] Warp triggered map='{sourceMap}' at=({sourceX},{sourceY}) -> map='{warpAction.Map}' x={warpAction.X} y={warpAction.Y} (pinned rAthena source value)");
                // Verified stock-iRO capture compatibility override (IroWireCompatibility - see its
                // own doc comment): the field->Prontera transition captured in
                // prontera-walking.pcapng frame 3246 lands at (156,34), diverging from pinned
                // legacy/rathena's own computed (156,26) for this exact door. The generated
                // WarpDefinition/warpAction values above stay an untouched, faithful reproduction
                // of pinned source; only the actually-executed transition below is compatibility-
                // resolved, and that resolution is itself logged so a live crash investigation can
                // see both the pinned value and the value actually used.
                var (resolvedX, resolvedY) = IroWireCompatibility.ResolveVerifiedWarpDestinationOverride(sourceMap, warpAction.Map, warpAction.X, warpAction.Y);
                if (resolvedX != warpAction.X || resolvedY != warpAction.Y)
                {
                    MapLogger.Info($"[iRO MAP DEBUG] Warp destination compatibility-resolved map='{warpAction.Map}' pinned=({warpAction.X},{warpAction.Y}) -> effective=({resolvedX},{resolvedY})");
                }
                TeleportTo(warpAction.Map, resolvedX, resolvedY);
            }
        }

        await TransferDistributedPresenceAsync(authoritativeSourceMap, _mapName, _x, _y, cancellationToken);

        var response = IroMapTransitionPackets.BuildSameServerMapChange(_mapName, _x, _y);
        MapLogger.Info(
            $"[iRO MAP DEBUG] Sending 0x0091 len={response.Length} map='{IroMapTransitionPackets.NormalizeWireMapName(_mapName)}' x={_x} y={_y} bytes={Convert.ToHexString(response)}");
        await WriteAsync(response, cancellationToken);
        await PersistPositionIfDirtyAsync(cancellationToken);
    }

    // Verified capture: 0x0437/8 (clif_parse_ActionRequest, clif.cpp:11818): id.W targetActorId.L
    // actionType.B (offset 6, DMG_REPEAT=7 in every live capture) opaqueByte.B (offset 7).
    // Pinned clif_parse_ActionRequest_sub (clif.cpp:11716-11739) dispatches DMG_NORMAL/DMG_REPEAT
    // to the SAME unit_attack call - this handler does the same: it never performs a hit itself,
    // it only resolves/validates the target and registers (or replaces) this session's ONE
    // server-owned repeat-attack state (pinned unit_attack, unit.cpp:2942-2953 - "just change
    // target/type" when an attack is already active for this unit). RunRepeatAttackLoopAsync
    // (started once per session by EnsureRuntimeLoopsStarted, same pattern as the movement/status
    // loops) owns actually executing hits on the pinned attack-delay cadence. A target that does
    // not resolve to a live MobInstance on the player's current map is silently ignored (no fake
    // success), matching this handler's existing "never fake a result" rule.
    private async Task HandleIroAttackRequestAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!IroAttackRequestPacket.TryParse(packet, out var request)) return;
        var targetActorId = request.TargetActorId;
        if (_monsters is null || _combat is null || _gameplayState is null) return;
        if (!_monsters.TryGetInstance(targetActorId, _mapName, out var target) || !target.IsAlive) return;

        await _attackGate.WaitAsync(cancellationToken);
        try
        {
            // Pinned unit_attack (unit.cpp:2942-2978): a request ALWAYS updates the target
            // (unit_set_target) - but whether it also fires/reschedules immediately depends on
            // whether an attack timer is already pending: "// Just change target/type. [Skotlex]
            // if(ud->attacktimer != INVALID_TIMER) return stop_flag;" (unit.cpp:2951-2953) - a
            // retarget/duplicate request arriving mid-cooldown does NOT reset attackabletime or
            // force an immediate hit; it only takes effect the next time the ALREADY-scheduled
            // timer fires. Only when no attack is currently pending does unit_attack itself decide
            // whether to fire now or schedule for attackabletime (unit.cpp:2971-2978) - which for
            // a brand-new repeat state (no prior NextAttackAt to inherit) is always "now", since
            // Athena has no cross-target attackabletime state to carry over otherwise.
            var nextAttackAt = _repeatAttack?.NextAttackAt ?? _timeProvider.GetUtcNow();
            _repeatAttack = new RepeatAttackState(targetActorId) { NextAttackAt = nextAttackAt };
        }
        finally { _attackGate.Release(); }
        try { _attackSignal.Release(); } catch (SemaphoreFullException) { }
    }

    // One repeat-attack scheduler per session (not one Task.Delay/Timer per hit), mirroring
    // RunMovementLoopAsync/RunStatusExpirationLoopAsync's exact shape: sleep until
    // _repeatAttack.NextAttackAt via the shared TimeProvider, waking early whenever
    // HandleIroAttackRequestAsync registers or replaces the active target (which can move the
    // next deadline earlier, or wake the loop from indefinite waiting).
    private async Task RunRepeatAttackLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                RepeatAttackState? active;
                await _attackGate.WaitAsync(cancellationToken);
                try { active = _repeatAttack; }
                finally { _attackGate.Release(); }

                if (active is null)
                {
                    await _attackSignal.WaitAsync(cancellationToken);
                    continue;
                }

                var delay = active.NextAttackAt - _timeProvider.GetUtcNow();
                if (delay > TimeSpan.Zero)
                {
                    using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var wake = _attackSignal.WaitAsync(delayCancellation.Token);
                    var sleep = Task.Delay(delay, _timeProvider, delayCancellation.Token);
                    var completed = await Task.WhenAny(wake, sleep);
                    delayCancellation.Cancel();
                    if (completed == wake)
                    {
                        try { await wake; } catch (OperationCanceledException) { }
                        continue;
                    }
                    try { await sleep; } catch (OperationCanceledException) { continue; }
                }

                await PerformDueRepeatAttackAsync(active, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    // Resolves each distinct QuestId GeneratedQuestDrops.All mentions through the real persistence
    // interface (see QuestDropResolver's own doc comment) - Athena has no materialized "all active
    // quests" concept anywhere else either. Only called from MonsterCombatCoordinator.AttackAsync's
    // own `killed` branch (see PerformDueRepeatAttackAsync's own call site) - never on an ordinary
    // non-lethal hit, which is the section 15 optimization this extraction exists for.
    private async Task<Func<uint, CharacterQuestStatus>> ResolveActiveQuestStatesAsync(CancellationToken cancellationToken)
    {
        var questStates = new Dictionary<uint, CharacterQuestStatus>();
        foreach (var rule in GeneratedQuestDrops.All)
        {
            if (questStates.ContainsKey(rule.QuestId)) continue;
            questStates[rule.QuestId] = await _questPersistence.GetQuestStateAsync(_accountId, _charId, rule.QuestId, cancellationToken) ?? CharacterQuestStatus.Absent;
        }
        return questId => questStates.GetValueOrDefault(questId, CharacterQuestStatus.Absent);
    }

    // Executes exactly one authoritative hit for the repeat-attack state active at the time the
    // loop woke. Reschedules the next hit (or clears the state on death/target-loss) BEFORE
    // sending any wire notification for this hit - the pinned unit_attack_timer_sub tail
    // (unit.cpp:3290-3338): perform the hit, unit_set_attackdelay (-> AttackDelayCalculator.
    // AttackDelayMs here), then re-arm the timer only "if (ud->state.attack_continue &&
    // !status_isdead(*src))". Ordering the reschedule before the notify matters: a client that
    // reads this hit's damage packet and immediately sends a NEW attack request
    // (HandleIroAttackRequestAsync, which inherits _repeatAttack?.NextAttackAt when a repeat is
    // already active per unit_attack's "just change target/type" behavior) must observe the
    // schedule THIS hit just computed, never a stale pre-hit value. `active` is re-validated as
    // still the CURRENT session target (not merely non-null) before doing anything: a replacing
    // attack request or a teleport/movement cancellation between "the loop woke" and "this method
    // acquired the gate" must not let a stale hit execute or reschedule against a target the
    // session no longer intends to attack.
    private async Task PerformDueRepeatAttackAsync(RepeatAttackState expected, CancellationToken cancellationToken)
    {
        await _attackGate.WaitAsync(cancellationToken);
        try
        {
            if (!ReferenceEquals(_repeatAttack, expected)) return;
        }
        finally { _attackGate.Release(); }

        if (_monsters is null || _combat is null || _gameplayState is null) { ClearRepeatAttackIfCurrent(expected); return; }
        if (!_monsters.TryGetInstance(expected.TargetActorId, _mapName, out var target) || !target.IsAlive)
        {
            ClearRepeatAttackIfCurrent(expected);
            return;
        }

        // Resolve the CURRENT authoritative right-hand weapon through the same shared
        // EquippedWeaponResolver path SendSelfWeaponAppearanceAsync uses - never the
        // client-facing LOOK_WEAPON/ClientViewId, and never cached across attacks, so
        // a same-session equip/unequip changes the very next attack's calculation.
        // UnknownItem/NonWeaponInWeaponSlot are authoritative-state/data invariant
        // FAILURES (an equipped item id that isn't in the pinned item_db, or a
        // non-weapon item resolved into the weapon slot), never legitimate unarmed
        // states - they must never silently degrade into an unarmed attack. This
        // attack is rejected/aborted outright: no combat calculation runs, no wire
        // response is sent, matching this handler's existing "never fake a result"
        // rule for an unresolvable target. The repeat state is cleared rather than
        // retried - the underlying equipment invariant violation will not resolve itself.
        WeaponItemDefinition? equippedWeapon = null;
        if (_equipment is { } equipment)
        {
            var weaponResolution = EquippedWeaponResolver.Resolve(equipment, GeneratedItems.ById);
            switch (weaponResolution.Resolution)
            {
                case EquippedWeaponResolution.Weapon:
                    equippedWeapon = weaponResolution.Weapon;
                    break;
                case EquippedWeaponResolution.Unarmed:
                    break;
                default:
                    MapLogger.Warning($"[iRO MAP DEBUG] Equipped right-hand item did not resolve to a weapon (resolution={weaponResolution.Resolution}); rejecting attack.");
                    ClearRepeatAttackIfCurrent(expected);
                    return;
            }
        }

        // MANDATORY server-authoritative range re-check, run before EVERY hit (the very first one
        // included - PerformDueRepeatAttackAsync is the ONE place every attack attempt, immediate
        // or scheduled, actually executes; see RunRepeatAttackLoopAsync's own doc comment). Pinned
        // unit_attack_timer_sub (unit.cpp:3251-3266): range=status_get_range(src), then a +1
        // "chasing" bonus when the TARGET is currently walking (unit_is_walking(target) - the
        // second half of that pinned condition, "target->type==BL_PC || !CELL_CHKICEWALL", is
        // unconditionally true for every monster target in this project: Ice Wall is a Wizard
        // skill-created cell state this codebase has no skill system to ever create, so
        // CELL_CHKICEWALL can never be true here), then check_distance_client_bl(src,target,range)
        // - a PC failing this sends clif_movetoattack (0x0139) and returns WITHOUT attacking
        // (unit.cpp:3255-3258), never with any damage/HP mutation/quest-drop side effect. Player
        // position is synced to real elapsed walking time FIRST (SyncPositionToNow) so a moving
        // attacker's position is never read stale, exactly like a fresh movement request would.
        SyncPositionToNow();
        var targetPositionForRangeCheck = target.GetPosition();
        var resolvedRange = BasicAttackRangeResolver.Resolve(equippedWeapon);
        var effectiveRangeForRangeCheck = resolvedRange + (target.IsWalking ? 1 : 0);
        var dxForRangeCheck = _x - targetPositionForRangeCheck.X;
        var dyForRangeCheck = _y - targetPositionForRangeCheck.Y;
        if (!ClientDistance.CheckDistanceClient(dxForRangeCheck, dyForRangeCheck, effectiveRangeForRangeCheck))
        {
            var clientDistance = ClientDistance.DistanceClient(dxForRangeCheck, dyForRangeCheck);
            MapLogger.Info(
                $"[iRO MAP DEBUG] Attack range rejected player=({_x},{_y}) targetActorId={expected.TargetActorId} target=({targetPositionForRangeCheck.X},{targetPositionForRangeCheck.Y}) weapon={(equippedWeapon is null ? "unarmed" : $"{equippedWeapon.AegisName}/{equippedWeapon.Id}")} range={effectiveRangeForRangeCheck} clientDistance={clientDistance}");
            var failurePacket = IroCombatDistancePackets.BuildAttackFailureForDistance(
                expected.TargetActorId, targetPositionForRangeCheck.X, targetPositionForRangeCheck.Y, _x, _y, (ushort)effectiveRangeForRangeCheck);
            // Live acceptance instrumentation (task requirement): 0x0139's exact outgoing bytes.
            // IroCombatDistancePackets is PINNED-SOURCE-BACKED ONLY - no verified stock-iRO capture
            // of ZC_ATTACK_FAILURE_FOR_DISTANCE exists in this project's evidence base
            // (ai/iro-2026-wire.md has no 0x0139 entry) - see that type's own doc comment. This log
            // line exists so a live PACKETVER 20220406 capture, once obtained, can be diffed
            // byte-for-byte against what Athena actually sent.
            MapLogger.Info($"[iRO MAP DEBUG] Sending 0x0139 (PINNED-SOURCE-BACKED, NOT capture-verified) len={failurePacket.Length} bytes={Convert.ToHexString(failurePacket)}");
            await WriteAsync(failurePacket, cancellationToken);
            // Pinned unit_attack_timer_sub's far-away branch never re-arms ud->attacktimer - only
            // the tail AFTER a real hit lands does that (unit.cpp:3333, "if (attack_continue &&
            // !status_isdead)"). The repeat-attack intent is therefore cleared here, not merely
            // skipped-for-one-tick: a far-away 0x0437 must not become a background loop that keeps
            // re-checking range/spamming 0x0139 while the player is still out of range - the stock
            // client is expected to walk closer on its own and send a NEW 0x0437 when it does,
            // which HandleIroAttackRequestAsync already handles as an ordinary fresh attack request.
            ClearRepeatAttackIfCurrent(expected);
            return;
        }

        // battle_check_range's own line-of-attack/obstacle check (battle.cpp:8215-8235), run AFTER
        // the client-distance check per pinned unit_attack_timer_sub's own ordering (unit.cpp:3251-
        // 3268: check_distance_client_bl first, THEN battle_check_range). For this project's only
        // currently-modeled weapon (Knife, Range=1) this is provably a no-op: battle_check_range's
        // own distance_bl(Chebyshev)<2 short-circuit (battle.cpp:8228-8229) always fires whenever
        // check_distance_client already passed for a range<=1 weapon (circular distance is always
        // >= Chebyshev distance for the same offset) - see BasicAttackDistanceValidator's own doc
        // comment. Implemented faithfully anyway (not skipped) so a future higher-range weapon is
        // already source-correct around walls/obstacles without revisiting this method.
        if (!BasicAttackDistanceValidator.HasDirectAttackPath(_collisionProvider, _mapName, _x, _y, targetPositionForRangeCheck.X, targetPositionForRangeCheck.Y, effectiveRangeForRangeCheck))
        {
            MapLogger.Info(
                $"[iRO MAP DEBUG] Attack range rejected (no direct attack path) player=({_x},{_y}) targetActorId={expected.TargetActorId} target=({targetPositionForRangeCheck.X},{targetPositionForRangeCheck.Y}) range={effectiveRangeForRangeCheck}");
            ClearRepeatAttackIfCurrent(expected);
            return;
        }

        // Section 15: quest-state CharServer roundtrips are only genuinely needed when THIS hit
        // kills the target (QuestDropResolver.ResolveDrops is only ever reached on death) - the
        // resolver below is only invoked by AttackAsync's own `killed` branch, so an ordinary
        // non-lethal hit never touches CharServer for quest state at all (the live log's own
        // observed "quest-state roundtrip on every hit" pattern this fixes).
        Task<Func<uint, CharacterQuestStatus>> ResolveQuestStatesAsync() => ResolveActiveQuestStatesAsync(cancellationToken);

        var effectiveStats = _statusEffects.Recalculate(_gameplayState.State);
        var outcome = await _combat.AttackAsync(
            target,
            _accountId,
            effectiveStats,
            _gameplayState.State.BaseLevel,
            equippedWeapon,
            ResolveQuestStatesAsync);
        if (!outcome.Accepted) { ClearRepeatAttackIfCurrent(expected); return; }

        // Section 16: log ONLY the actual acquisition transition the coordinator reported - see
        // MonsterAttackOutcome.EngagementAcquired's own doc comment for why that pure state/rules
        // layer surfaces this as a flag rather than logging it itself.
        if (outcome.EngagementAcquired)
        {
            var position = target.GetPosition();
            MapLogger.Info($"[iRO MAP DEBUG] Mob engagement acquired mobActorId={target.ActorId} targetAccountId={_accountId} mobPosition=({position.X},{position.Y}) combatState={target.Engagement.State}");
        }

        // Reschedule (or clear, on death) the repeat-attack runtime state BEFORE any wire
        // notification for this hit - matching this project's validate -> persist -> update
        // runtime state -> notify ordering (AGENTS.md). This also closes a real race: a client
        // that reads the damage packet and immediately sends a NEW attack request
        // (HandleIroAttackRequestAsync, which inherits _repeatAttack?.NextAttackAt when a repeat
        // is already active) must observe the schedule this hit just computed, never the stale
        // pre-hit value - which it would if the reschedule happened only after WriteAsync below.
        if (outcome.KilledByThisHit)
        {
            // Pinned unit_attack_timer_sub only re-arms the timer "if (ud->state.attack_continue
            // && !status_isdead(*src))" (unit.cpp:3333) - a dead target never reschedules.
            ClearRepeatAttackIfCurrent(expected);
        }
        else
        {
            var weaponTypeForDelay = equippedWeapon?.WeaponType;
            var delayMs = AttackDelayCalculator.AttackDelayMs(effectiveStats, weaponTypeForDelay);
            await _attackGate.WaitAsync(cancellationToken);
            try
            {
                if (ReferenceEquals(_repeatAttack, expected)) expected.NextAttackAt = _timeProvider.GetUtcNow().AddMilliseconds(delayMs);
            }
            finally { _attackGate.Release(); }
        }

        var tick = unchecked((uint)Environment.TickCount);
        var damageDealt = outcome.HpBefore - outcome.HpAfter;
        MapLogger.Info(
            $"[iRO MAP DEBUG] Attack accepted attackerAccountId={_accountId} targetActorId={expected.TargetActorId} damage={damageDealt} hpBefore={outcome.HpBefore} hpAfter={outcome.HpAfter} killed={outcome.KilledByThisHit} range={effectiveRangeForRangeCheck} clientDistance={ClientDistance.DistanceClient(dxForRangeCheck, dyForRangeCheck)}");

        // dstSpeed is the TARGET's own dmotion (clif_damage's ddelay) - for a mob target that is
        // MobDefinition.DamageMotion directly (see that field's own doc comment for the pinned
        // trace); srcSpeed remains the existing capture-verified player-attacker value (460) -
        // deriving the player's own real amotion is a separate, larger, weapon-speed-dependent
        // pinned formula (status_base_amotion) out of this task's scope, not touched here.
        var damagePacket = IroMonsterCombatPackets.BuildNotifyAct3(
            _accountId,
            expected.TargetActorId,
            tick,
            srcSpeed: 460,
            dstSpeed: (uint)target.Spawn.Mob.DamageMotion,
            damage: damageDealt,
            div: 1,
            actionType: 0);
        await WriteAsync(damagePacket, cancellationToken);

        if (outcome.KilledByThisHit)
        {
            // Pinned mob_dead awards generated monster EXP before clearing the dead unit.
            // This currently-supported session is the authoritative single recipient: the
            // accepted attack was made by this authenticated account, with no party/contribution
            // policy invented. Zero-valued generated EXP produces no persistence and no packets.
            var (ratedBaseExp, ratedJobExp) = ExperienceRewardService.ResolveReward(
                _rates,
                target.Spawn.Mob.BaseExp,
                target.Spawn.Mob.JobExp,
                ExperienceSource.Monster);
            var progression = await new CharacterProgressionService(_gameplayState).AddExperienceAsync(
                ratedBaseExp,
                ratedJobExp,
                cancellationToken);
            if (progression is null)
            {
                MapLogger.Warning($"[iRO MAP DEBUG] Monster EXP persistence failed actorId={expected.TargetActorId}; no progression packets sent.");
            }
            else
            {
                foreach (var packet in IroCharacterProgressionPackets.Build(_accountId, progression.Value))
                    await WriteAsync(packet, cancellationToken);
            }

            MapLogger.Info($"[iRO MAP DEBUG] Monster died actorId={expected.TargetActorId} mob={target.Spawn.Mob.AegisName}");
            var vanishPacket = IroMonsterCombatPackets.BuildNotifyVanish(expected.TargetActorId, PacketConstants.ZcNotifyVanishReasonDied);
            await WriteAsync(vanishPacket, cancellationToken);
            _visibleActorIds.MarkNotVisible(expected.TargetActorId);

            foreach (var drop in outcome.QuestDrops)
            {
                if (!GeneratedItems.ById.TryGetValue(drop.ItemId, out var itemDefinition))
                {
                    MapLogger.Warning($"[iRO MAP DEBUG] Quest drop references unregistered itemId={drop.ItemId}; skipping client notification.");
                    continue;
                }

                var inventorySession = new CharacterInventorySession(_accountId, _charId, _inventoryPersistence);
                var addResult = await inventorySession.AddItemAsync(itemDefinition, (uint)drop.Count, cancellationToken);
                if (!addResult.Success || addResult.Item is not { } addedRow || _inventory is not { } inventory)
                {
                    MapLogger.Warning($"[iRO MAP DEBUG] Inventory persistence failed for itemId={drop.ItemId}; not notifying client.");
                    continue;
                }

                // Persistence succeeded - update the authoritative MapServer runtime snapshot with the
                // CharServer-confirmed row BEFORE notifying the client (never the other way around: a
                // client-visible 0x0B41 must never be sent while _inventory is left stale). IsNewRow
                // decides slot assignment: a brand-new row gets the first free runtime slot (reusing
                // a hole left by an earlier consume, mirroring pinned pc_additem); an existing stack's
                // amount update preserves whatever slot its DurableId already occupies. _equipment is
                // re-derived from the SAME updated snapshot for consistency, even though an ordinary
                // Etc/Usable drop like Wood never changes the right-hand slot - there is exactly one
                // place _equipment is derived from _inventory, never a second independently-maintained
                // copy.
                _inventory = addResult.IsNewRow
                    ? inventory.WithNewItem(addResult.DurableId, addedRow.ItemId, addedRow.Amount, addedRow.Equip, addedRow.Identified, addedRow.Refine, addedRow.Favorite, addedRow.Bound)
                    : inventory.WithUpdatedItem(addResult.DurableId, addedRow.ItemId, addedRow.Amount, addedRow.Equip, addedRow.Identified, addedRow.Refine, addedRow.Favorite, addedRow.Bound);
                _equipment = CharacterEquipmentSnapshot.FromInventory(_inventory);

                // client_index(): server-side runtime array position + 2 (clif.cpp:122-124). Read
                // back from the snapshot by DurableId - the authoritative source of truth for this
                // row's CURRENT runtime slot, never re-derived from anything CharServer returned.
                var slotIndex = _inventory.Items.Single(i => i.DurableId == addResult.DurableId).SlotIndex;
                var clientIndex = (ushort)(slotIndex + 2);
                var pickupPacket = IroMonsterCombatPackets.BuildItemPickupAck(clientIndex, (ushort)drop.Count, itemDefinition.Id, itemType: 3);
                MapLogger.Info($"[iRO MAP DEBUG] Sending 0x0B41 itemId={itemDefinition.Id} count={drop.Count} clientIndex={clientIndex}");
                await WriteAsync(pickupPacket, cancellationToken);
            }
        }
    }

    // Clears _repeatAttack only if it is STILL the exact instance this hit was computed for -
    // a concurrent replacing attack request (HandleIroAttackRequestAsync) or cancellation
    // (TeleportTo/HandleIroMovementAsync) may already have installed a different RepeatAttackState
    // (or null) between when this hit started and when it finished; that newer state must never be
    // clobbered by a stale outcome belonging to the target it replaced. Synchronous (never awaits),
    // so callers may invoke it directly without a surrounding _attackGate.WaitAsync of their own.
    private void ClearRepeatAttackIfCurrent(RepeatAttackState expected)
    {
        _attackGate.Wait();
        try
        {
            if (ReferenceEquals(_repeatAttack, expected)) _repeatAttack = null;
        }
        finally { _attackGate.Release(); }
    }

    // Narrow, synchronized read for the world monster-tick orchestrator (MapTcpServer) to pass
    // into MonsterEngagementDomain.Evaluate - see PlayerCombatSnapshot's own doc comment for why
    // this is deliberately narrow rather than exposing MapClientSession's full surface. Guarded by
    // _movementGate (the SAME gate HandleIroMovementAsync/ProcessDueMovementAsync already use for
    // _x/_y/_mapName) so a concurrent world tick reading this snapshot can never observe a torn
    // position mid-movement-packet-processing - this is the "use the session's existing
    // synchronization model" this task's own concurrency requirement calls for, not a new ad-hoc
    // lock. Returns null once (not before) the character has been authenticated far enough to have
    // gameplay state - a session mid-handshake is not yet a valid combat target, matching how
    // MapTcpServer's _sessions dictionary itself only holds sessions that have reached RunAsync.
    internal async Task<PlayerCombatSnapshot?> TryGetCombatSnapshotAsync(CancellationToken cancellationToken)
    {
        if (_gameplayState is null) return null;
        var stats = _statusEffects.Recalculate(_gameplayState.State);
        await _movementGate.WaitAsync(cancellationToken);
        try
        {
            SyncPositionToNow();
            var isWalking = _movement?.IsMoving ?? false;
            return new PlayerCombatSnapshot(_accountId, _mapName, _x, _y, _gameplayState.State.CurrentHp > 0, isWalking, _gameplayState.State.BaseLevel, stats.Vitality, stats.Agility);
        }
        finally { _movementGate.Release(); }
    }

    // Authoritative mob-on-player basic-attack application, called by the world monster-tick
    // orchestrator (MapTcpServer) once MonsterEngagementDomain.Evaluate has already decided (from a
    // PlayerCombatSnapshot taken moments earlier) that this hit should happen. Re-validates
    // liveness/map under _gameplayState's own MutateAsync (optimistic-concurrency: the mutation
    // callback re-reads `expected.CurrentHp` itself) so a player who died, changed map, or received
    // a DIFFERENT concurrent HP mutation between snapshot and this call cannot have this hit
    // silently clobber that newer state - MutateAsync's own persisted-row compare-and-swap is
    // exactly this project's "treat a stale snapshot as needing re-validation, not blind
    // overwrite" pattern (see ICharacterGameplayStatePersistence.MutateAsync's own doc comment).
    // Returns false (does nothing, sends nothing) if the player is no longer a valid target by the
    // time the mutation actually runs - the orchestrator feeds that back into the mob's own target-
    // unlock lifecycle exactly like a snapshot that was null/dead/wrong-map to begin with.
    // Section 12's own responsibility split: this method is VICTIM-ONLY (authoritative HP mutation
    // + the victim's own self SP_HP update) - it never sends the 0x08C8 combat action itself. The
    // action is broadcast separately, to every AREA-visible observer (victim included), via
    // MonsterAttackActionOutcome/MapTcpServer's own fan-out - see that record's own doc comment for
    // why the two are split (pinned clif_damage's own AREA-vs-SELF distinction: the action is
    // never victim-only, the HP parameter update never leaves the victim's own session).
    //
    // Section 7's own TOCTOU closure point: this is the ACTUAL hit-execution instant, re-validated
    // fully here rather than trusting whatever snapshot the caller's earlier Evaluate/Chase-vs-
    // Attack decision was based on - MutateAsync's own optimistic-concurrency compare-and-swap
    // means a stale `expected` row (the player moved/mutated concurrently) is naturally rejected
    // (returns null) rather than silently overwritten; `damage`/`isMiss` were themselves computed
    // moments earlier by the caller from ITS OWN re-snapshot taken immediately before calling this
    // method (MonsterEngagementTickProcessor's own re-snapshot-then-attack sequence), not from the
    // original Evaluate-time snapshot - see that processor's own doc comment for the full sequence.
    //
    // Returns null when the mutation could not be applied (no gameplay state, or MutateAsync
    // rejected a stale row) - the caller must NOT emit any attack action/outcome in that case
    // (section 7: "do not emit a successful attack result" when the target is no longer
    // attackable). Otherwise returns the HP AFTER this hit, and whether it actually changed (a
    // miss/0-damage hit, or a hit against an already-dead player, changes nothing - see section
    // 10's own "do not send an HP parameter update merely because the mob attempted an attack"
    // requirement).
    internal async Task<(uint HpAfter, bool HpChanged)?> ApplyIncomingMobBasicAttackAsync(uint damage, CancellationToken cancellationToken)
    {
        if (_gameplayState is null) return null;

        var before = _gameplayState.State.CurrentHp;
        var mutated = await _gameplayState.MutateAsync(current =>
        {
            if (current.CurrentHp == 0) return current; // Already dead - no further reduction (pinned status_isdead target check).
            var after = damage >= current.CurrentHp ? 0u : current.CurrentHp - damage;
            return current with { CurrentHp = after };
        }, cancellationToken);

        if (mutated is null) return null; // Persistence rejected the mutation (stale row) - treat as a normal "target no longer valid this tick", not an error.

        // The SP_HP packet itself is NOT written here - see MonsterAttackActionOutcome's own doc
        // comment for why the wire write is deferred to NotifyMonsterAttackOutcomeAsync, which sends
        // it immediately after the action packet on the same fan-out call (matching pinned wire
        // ordering: action always precedes the HP sync).
        return (mutated.CurrentHp, mutated.CurrentHp != before);
    }

    // Pinned clif_parse_UseItem (clif.cpp:12077-12106) -> pc_useitem (pc.cpp:6450-6576).
    // n = server_index(index) (client index - 2, matching every other equip/unequip/pickup
    // path's convention) - the client supplies intent only; the authoritative item is resolved
    // from THIS session's own CharacterInventorySnapshot at that slot, never trusted from the
    // client. AccountId is validated against the authenticated session (pinned clif_parse_UseItem
    // itself never re-validates the field - the request is scoped to sd via fd - but Athena's
    // packet still carries it, so it is checked defensively rather than ignored).
    //
    // This slice supports exactly one traced source-backed effect: a Type: Usable item whose
    // pinned item_db Script is a getitem-only container (ItemDataCompiler.TryParseGetItemScript,
    // e.g. First Aid Box 23484) - GeneratedItems.UsableItemDefinition.Grants is non-empty only
    // for that narrow case. Any other resolved item (a different Usable with no Grants, a
    // Healing/DelayConsume item, an unknown/non-usable slot) is rejected without mutation -
    // implementing their real effects (itemheal, itemskill, status, etc.) is explicitly out of
    // scope; this handler must never guess or fake a result for them.
    //
    // Ordering follows this project's own validate -> persist -> update runtime state -> notify
    // rule (AGENTS.md), which is ALSO consistent with pinned pc_useitem's own real ordering for
    // this exact case (clif_useitemack sent BEFORE pc_delitem at pc.cpp:6535-6536 - the ack must
    // be constructed from the row's PRE-consume state/still-existing row, per clif_useitemack's
    // own early-out at clif.cpp:4477 for a zeroed/absent row) - so persisting first and building
    // the ack from the confirmed post-persist state produces the exact same wire values pinned
    // source does for the amount-after-use case, without needing to special-case send-before-persist.
    private async Task HandleIroUseItemRequestAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!IroUseItemRequestPacket.TryParse(packet, out var request)) return;
        if (request.AccountId != _accountId) return;
        if (_inventory is not { } inventory || request.ClientIndex < 2) return;
        var slotIndex = (uint)(request.ClientIndex - 2);

        var row = inventory.Items.FirstOrDefault(i => i.SlotIndex == slotIndex);
        if (row is null || !GeneratedItems.ById.TryGetValue(row.ItemId, out var itemDefinition))
        {
            MapLogger.Warning($"[iRO MAP DEBUG] Item-use rejected: no resolvable item at slotIndex={slotIndex}.");
            return;
        }

        if (itemDefinition is not UsableItemDefinition { Grants.Count: > 0 } usable)
        {
            MapLogger.Warning(
                $"[iRO MAP DEBUG] Item-use rejected: itemId={row.ItemId} has no source-backed use effect implemented in this slice.");
            return;
        }

        // Fail closed on an incomplete container implementation: FirstaidBox10 (23485), for
        // example, has a pinned getitem grant for 23486 ("Firstaid_Box_15"), which is
        // intentionally not yet generated. Consuming the container and granting only the
        // resolvable subset would silently destroy the source item and leave the character
        // short the ungrantable one - never acceptable. Every grant must resolve BEFORE any
        // consume/persist/notify happens, so an unimplemented container is rejected outright
        // (no consume, no ack, no partial grants) while a fully-generated container (e.g.
        // FirstAidBox/23484) remains fully executable.
        foreach (var grant in usable.Grants)
        {
            if (!GeneratedItems.ById.ContainsKey(grant.ItemId))
            {
                MapLogger.Warning(
                    $"[iRO MAP DEBUG] Item-use rejected: itemId={row.ItemId} has an unimplemented grant itemId={grant.ItemId}; container use is out of scope until all grants are generated.");
                return;
            }
        }

        var inventorySession = new CharacterInventorySession(_accountId, _charId, _inventoryPersistence);
        var consumeResult = await inventorySession.ConsumeItemAsync(row.DurableId, 1, cancellationToken);
        if (!consumeResult.Success)
        {
            MapLogger.Warning($"[iRO MAP DEBUG] Item-use persistence failed for itemId={row.ItemId} durableId={row.DurableId}; not notifying client.");
            return;
        }

        // Persistence succeeded - update the authoritative runtime snapshot BEFORE granting the
        // container's items or notifying the client (same rule the reward path already follows).
        // RowDeleted leaves a HOLE at this row's former runtime slot (WithoutDurableId) - it does
        // NOT compact/renumber later rows, mirroring pinned pc_delitem exactly; a later grant may
        // reuse that exact hole via WithNewItem.
        _inventory = consumeResult.RowDeleted
            ? inventory.WithoutDurableId(row.DurableId)
            : inventory.WithUpdatedItem(row.DurableId, row.ItemId, consumeResult.NewAmount, row.Equip, row.Identified, row.Refine, row.Favorite, row.Bound);
        _equipment = CharacterEquipmentSnapshot.FromInventory(_inventory);

        MapLogger.Info(
            $"[iRO MAP DEBUG] Item-use consumed itemId={row.ItemId} durableId={row.DurableId} newAmount={consumeResult.NewAmount} rowDeleted={consumeResult.RowDeleted}.");

        var ackPacket = IroUseItemPackets.BuildUseItemAck(request.ClientIndex, itemDefinition.ClientViewId, _accountId, consumeResult.NewAmount, success: true);
        await WriteAsync(ackPacket, cancellationToken);

        // Execute the source-backed getitem grants (script.cpp BUILDIN_FUNC(getitem)) - each
        // grant is a normal authoritative inventory add through the SAME CharacterInventorySession/
        // runtime-snapshot-update path the quest-drop reward loop already uses, including the
        // SAME IsNewRow-driven slot assignment (first free slot - reusing the hole just left by
        // this same consume, mirroring pinned pc_additem's own array-search behavior). A grant
        // referencing an item id absent from the generated registry is a data/generation gap,
        // logged and skipped rather than guessed at (matching the existing quest-drop convention)
        // - it does NOT abort the remaining grants, since each is an independent getitem call in
        // the pinned script, not a single atomic operation.
        foreach (var grant in usable.Grants)
        {
            if (!GeneratedItems.ById.TryGetValue(grant.ItemId, out var grantedItem))
            {
                MapLogger.Warning($"[iRO MAP DEBUG] Item-use grant references unregistered itemId={grant.ItemId}; skipping.");
                continue;
            }

            var grantResult = await inventorySession.AddItemAsync(grantedItem, grant.Amount, cancellationToken);
            if (!grantResult.Success || grantResult.Item is not { } grantedRow)
            {
                MapLogger.Warning($"[iRO MAP DEBUG] Item-use grant persistence failed for itemId={grant.ItemId}; skipping.");
                continue;
            }

            _inventory = grantResult.IsNewRow
                ? _inventory.WithNewItem(grantResult.DurableId, grantedRow.ItemId, grantedRow.Amount, grantedRow.Equip, grantedRow.Identified, grantedRow.Refine, grantedRow.Favorite, grantedRow.Bound)
                : _inventory.WithUpdatedItem(grantResult.DurableId, grantedRow.ItemId, grantedRow.Amount, grantedRow.Equip, grantedRow.Identified, grantedRow.Refine, grantedRow.Favorite, grantedRow.Bound);
            _equipment = CharacterEquipmentSnapshot.FromInventory(_inventory);

            var grantSlotIndex = _inventory.Items.Single(i => i.DurableId == grantResult.DurableId).SlotIndex;
            var grantClientIndex = (ushort)(grantSlotIndex + 2);
            // Pinned clif_additem (clif.cpp): p.nameid = client_nameid(actual item), p.type =
            // itemtype(actual item) - never a hardcoded constant. Uses the SAME item-type mapper
            // IroInventoryListPackets uses for full-inventory serialization, so a grant's pickup
            // packet and its later reconnect/full-list packet can never disagree on item type.
            var grantPickupPacket = IroMonsterCombatPackets.BuildItemPickupAck(grantClientIndex, (ushort)grant.Amount, grantedItem.ClientViewId, IroInventoryListPackets.ItemType(grantedItem));
            MapLogger.Info($"[iRO MAP DEBUG] Sending 0x0B41 for item-use grant itemId={grant.ItemId} count={grant.Amount} clientIndex={grantClientIndex}");
            await WriteAsync(grantPickupPacket, cancellationToken);
        }
    }

    // Thin parse-and-call handler (task section 16) for the verified stock-iRO skill-up request
    // (see IroSkillLevelUpRequestPacket, ai/iro-2026-wire.md). Deliberately does NOT itself check
    // SkillPoints/MaxLevel/BaseLevel/JobLevel/prerequisites/acquisition gates/CharSkillFlag - all
    // of that already lives in CharacterSkillService.ValidateUpgrade, reached exclusively through
    // CharacterGameplayStateSession.LearnSkillAsync (the SAME authoritative mutation path proven
    // by CharacterSkillLearnIntegrationTests). A structurally valid but semantically illegal
    // request (unknown skill, out-of-tree, no points, etc.) simply returns null from
    // LearnSkillAsync here and is silently dropped - a gameplay rejection, never a malformed-
    // packet disconnect (task section 79).
    private async Task HandleIroSkillLevelUpRequestAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!IroSkillLevelUpRequestPacket.TryParse(packet, out var request)) return;
        if (_gameplayState is not { } gameplayState) return;

        MapLogger.Info($"[iRO SKILL] Received skill-up request charId={_charId} skillId={request.SkillId} currentSkillPoints={gameplayState.State.SkillPoints}");

        var tree = Athena.Net.MapServer.Generated.Skills.GeneratedSkillTreeRegistry.Get(gameplayState.State.JobClass);
        var result = await gameplayState.LearnSkillAsync(tree, request.SkillId, cancellationToken);
        if (result is null)
        {
            MapLogger.Info($"[iRO SKILL] Rejected skill-up charId={_charId} skillId={request.SkillId}");
            return;
        }

        // Response fields are derived from the POST-COMMIT state (task section 25/50) - never
        // reconstructed from what the client requested or from the pre-mutation snapshot.
        // gameplayState.State/Skills already reflect the committed result at this point
        // (LearnSkillAsync replaces both under its own lock before returning non-null).
        MapLogger.Info($"[iRO SKILL] Learned skillId={result.SkillId} newLevel={result.NewSkillLevel} remainingSkillPoints={gameplayState.State.SkillPoints} newVersion={gameplayState.State.Version}");

        var canonical = Athena.Net.MapServer.Generated.Skills.GeneratedSkillRegistry.GetById(result.SkillId);
        var effective = CharacterSkillService.CalculateEffectiveState(gameplayState.State, gameplayState.Skills, tree, out _);
        var learnedState = effective.First(s => s.SkillId == result.SkillId);
        var entry = IroSkillInfoEntry.From(learnedState, canonical, gameplayState.Skills);

        MapLogger.Info($"[iRO MAP DEBUG] Sending 0x0B33 skillId={entry.SkillId} newLevel={entry.CurrentLevel}");
        await WriteAsync(IroSkillLevelUpdatePackets.Build(entry), cancellationToken);

        MapLogger.Info($"[iRO MAP DEBUG] Sending 0x00B0 param 12 (SkillPoints) = {gameplayState.State.SkillPoints}");
        await WriteAsync(IroCharacterProgressionPackets.Parameter(12, gameplayState.State.SkillPoints), cancellationToken);
    }

    // Verified stock-iRO base-stat allocation wire (statsonly.pcapng - see ai/iro-2026-wire.md
    // for the full evidence trace). An unrecognized StatusId (Stat is null - e.g. a fourth-job
    // trait-stat id this project never wires, or a forged value) or a structurally invalid
    // packet is dropped here, before ever reaching CharacterGameplayStateSession.
    // IncreaseStatAsync/CharacterStatService - the SAME authoritative mutation path proven by
    // CharacterGameplayStateSessionTests. The verified wire carries only single-step requests
    // (every captured Amount byte was 1); this handler forwards Amount as-is and lets
    // CharacterStatService's existing cost/cap validation reject anything it cannot afford or
    // that would exceed the generated per-job cap - no separate "amount must be 1" gate is
    // added here, since that would duplicate a check the domain layer already performs
    // correctly for any amount.
    //
    // A structurally valid but semantically illegal request (unaffordable, at cap, etc.) simply
    // returns null from IncreaseStatAsync and is silently dropped - a gameplay rejection, never
    // a malformed-packet disconnect, matching HandleIroSkillLevelUpRequestAsync's own policy.
    // Stock-iRO failure-wire behavior is not captured (ai/iro-2026-wire.md's open item), so no
    // failure packet is fabricated here.
    private async Task HandleIroStatusUpRequestAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!IroStatusUpRequestPacket.TryParse(packet, out var request) || request.Stat is not { } stat) return;
        if (_gameplayState is not { } gameplayState) return;

        MapLogger.Info($"[iRO STAT] Received stat-up request charId={_charId} stat={stat} amount={request.Amount} currentStatusPoints={gameplayState.State.StatPoints}");

        var result = await gameplayState.IncreaseStatAsync(stat, request.Amount, cancellationToken);
        if (result is null)
        {
            MapLogger.Info($"[iRO STAT] Rejected stat-up charId={_charId} stat={stat}");
            return;
        }

        // Response fields are derived from the POST-COMMIT state - never reconstructed from
        // what the client requested or from the pre-mutation snapshot. gameplayState.State
        // already reflects the committed result at this point (IncreaseStatAsync replaces
        // State under its own lock before returning non-null).
        MapLogger.Info($"[iRO STAT] Increased stat={stat} newValue={result.NewValue} remainingStatusPoints={gameplayState.State.StatPoints} newVersion={gameplayState.State.Version}");

        var wireStatusId = IroStatusUpRequestPacket.WireStatusId(stat);

        // 0x0141 ZC_COUPLESTATUS: base = the new persisted value, plus = the CURRENTLY ACTIVE
        // temporary-status bonus on this stat, reused from the SAME existing projection
        // RunStatusExpirationLoopAsync above already uses for Blessing/Increase AGI resync -
        // never a duplicated formula here. Blessing affects STR/INT/DEX and Increase AGI
        // affects AGI (CharacterStatusEffectState.Recalculate's own doc comment traces both to
        // pinned status_calc_str/int/dex/agi); VIT and LUK have no currently-modeled temporary
        // bonus source, so plus naturally stays 0 for them. Recalculated AFTER the authoritative
        // mutation commits, from the POST-COMMIT gameplayState.State, so base and plus are
        // always read from the same coherent post-commit snapshot - never a stale pre-mutation
        // base paired with a fresh plus or vice versa. This remains an explicit, documented
        // PARTIAL match of the capture's full derived-status burst - the capture also shows
        // additional combat-stat packets (ATK/DEF/FLEE/HIT/ASPD-related) this project does not
        // yet compute anywhere (no derived-combat-stat engine exists, and MaxHP/MaxSP
        // recalculation only exists for the level-up formula path), so those remain deliberately
        // NOT sent here rather than fabricated. See ai/iro-2026-wire.md for the full scope note.
        var effective = _statusEffects.Recalculate(gameplayState.State);
        var plusValue = EffectiveStatBonus(stat, gameplayState.State, effective);
        await WriteAsync(IroStatusEffectPackets.BuildCoupleStatus(wireStatusId, result.NewValue, plusValue), cancellationToken);

        MapLogger.Info($"[iRO MAP DEBUG] Sending 0x00B0 param 9 (StatusPoints) = {gameplayState.State.StatPoints}");
        await WriteAsync(IroCharacterProgressionPackets.Parameter(9, gameplayState.State.StatPoints), cancellationToken);

        MapLogger.Info($"[iRO MAP DEBUG] Sending 0x00BC stat={stat} newValue={result.NewValue}");
        await WriteAsync(IroStatusUpAckPacket.BuildSuccess(wireStatusId, (byte)result.NewValue), cancellationToken);
    }

    // The 0x0141 "plus" value for a given base stat: effective (base + active temporary
    // statuses) minus the same POST-COMMIT persisted base, using CharacterStatusEffectState.
    // Recalculate as the single existing source of truth for Blessing/Increase AGI bonuses -
    // see that type's own doc comment for the pinned status_calc_str/int/dex/agi tracing. Both
    // baseState and effective must come from the same Recalculate call against the same
    // baseState, so this never mixes a stale base with a fresh effective value or vice versa.
    private static int EffectiveStatBonus(CharacterBaseStat stat, CharacterGameplayState baseState, EffectiveCharacterStats effective) => stat switch
    {
        CharacterBaseStat.Strength => effective.Strength - baseState.Strength,
        CharacterBaseStat.Agility => effective.Agility - baseState.Agility,
        CharacterBaseStat.Vitality => effective.Vitality - baseState.Vitality,
        CharacterBaseStat.Intelligence => effective.Intelligence - baseState.Intelligence,
        CharacterBaseStat.Dexterity => effective.Dexterity - baseState.Dexterity,
        CharacterBaseStat.Luck => effective.Luck - baseState.Luck,
        _ => throw new ArgumentOutOfRangeException(nameof(stat), stat, "Unknown base stat."),
    };

    // Pinned clif_parse_EquipItem (clif.cpp:12113-12159): index = server_index(p->index)
    // (client index - 2, clif.cpp:127-129) - never an item id. _inventory is guaranteed
    // non-null once authenticated (CompleteIroAuthenticationAsync fails auth outright on an
    // unsuccessful inventory read).
    //
    // Ordering matches pinned pc_equipitem exactly for the client-visible packets (ACK before
    // appearance, clif.cpp:12168-12178) - the ONLY difference from rAthena is that Athena
    // persists to CharServer and confirms success BEFORE sending either packet, whereas
    // real rAthena's single in-memory process technically assigns the field after sending both
    // (see CharacterEquipmentMutationService's own doc comment). Athena never reports success
    // before the durable write is confirmed.
    private async Task HandleEquipRequestAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!IroEquipRequestPacket.TryParse(packet, out var request)) return;
        if (_inventory is not { } inventory || request.ClientIndex < 2) return;
        var slotIndex = (uint)(request.ClientIndex - 2);

        var service = new CharacterEquipmentMutationService(_accountId, _charId, _inventoryListPersistence);
        var (outcome, updated) = await service.EquipAsync(inventory, slotIndex, request.Position, GeneratedItems.ById, cancellationToken);
        if (outcome is not { } equipOutcome) return; // invalid slot/index - pc_equipitem sends no ack either (clif.cpp:12154-12156 early-out)

        var result = equipOutcome.Result switch
        {
            EquipMutationResult.Success => PacketConstants.EquipAckResultOk,
            EquipMutationResult.FailLevel => PacketConstants.EquipAckResultFailLevel,
            _ => PacketConstants.EquipAckResultFail,
        };
        await WriteAsync(IroEquipmentMutationPackets.BuildEquipAck(request.ClientIndex, equipOutcome.WearLocation, result), cancellationToken);

        if (equipOutcome.Result != EquipMutationResult.Success || updated is null) return;

        _inventory = updated;
        _equipment = CharacterEquipmentSnapshot.FromInventory(updated);
        await RefreshPresencePublicAppearanceAsync(cancellationToken);
        await SendSelfWeaponAppearanceAsync(cancellationToken);
    }

    // Pinned clif_parse_UnequipItem (clif.cpp:12166-12189): index = server_index(p->index).
    // Ordering matches pinned pc_unequipitem exactly for the client-visible packets
    // (appearance BEFORE ACK, clif.cpp:12426-12452) - the reverse of the equip path. See
    // HandleEquipRequestAsync's doc comment for the persist-before-report rationale.
    private async Task HandleUnequipRequestAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!IroUnequipRequestPacket.TryParse(packet, out var request)) return;
        if (_inventory is not { } inventory || request.ClientIndex < 2) return;
        var slotIndex = (uint)(request.ClientIndex - 2);

        var targetItemId = inventory.Items.FirstOrDefault(i => i.SlotIndex == slotIndex)?.ItemId;
        var oldEquip = inventory.Items.FirstOrDefault(i => i.SlotIndex == slotIndex)?.Equip;
        var service = new CharacterEquipmentMutationService(_accountId, _charId, _inventoryListPersistence);
        var (outcome, updated) = await service.UnequipAsync(inventory, slotIndex, cancellationToken);
        if (outcome is not { } unequipOutcome)
        {
            MapLogger.Warning($"[iRO MAP DEBUG] Unequip rejected (unknown slotIndex={slotIndex}); no ack sent.");
            return;
        }

        MapLogger.Info(
            $"[iRO MAP DEBUG] Unequip persisted slotIndex={slotIndex} itemId={targetItemId} oldEquip={oldEquip} newEquip=0 success={unequipOutcome.Success}");

        if (unequipOutcome.Success && updated is not null)
        {
            _inventory = updated;
            _equipment = CharacterEquipmentSnapshot.FromInventory(updated);
            await RefreshPresencePublicAppearanceAsync(cancellationToken);
            MapLogger.Info("[iRO MAP DEBUG] Sending 0x01D7 (post-unequip appearance update).");
            await SendSelfWeaponAppearanceAsync(cancellationToken);
        }

        var ackPacket = IroEquipmentMutationPackets.BuildUnequipAck(request.ClientIndex, unequipOutcome.WearLocation, unequipOutcome.Success);
        MapLogger.Info(
            $"[iRO MAP DEBUG] Sending 0x099A unequip ack bytes={Convert.ToHexString(ackPacket)} clientIndex={request.ClientIndex} wearLocation={unequipOutcome.WearLocation} success={unequipOutcome.Success}");
        await WriteAsync(ackPacket, cancellationToken);
        MapLogger.Info("[iRO MAP DEBUG] 0x099A unequip ack write completed.");
    }

    private async Task HandleNpcInteractionAsync(byte[] packet, CancellationToken cancellationToken)
    {
        // A monster actor ID is never registered in _worldMapRegistry (NPC/warp actors only),
        // so TryGetInteraction below already rejects it - monster actors are never routed into
        // NPC script dispatch, intentionally, not by accident.
        if (!IroNpcDialoguePackets.TryParseInteraction(packet, out var actorId) || HasActiveScript || !_visibleActorIds.IsActorVisible(actorId) || !_worldMapRegistry.TryGetInteraction(actorId, _mapName, out var entity, out var script))
        {
            MapLogger.Info($"[iRO MAP DEBUG] NPC interaction rejected actorId={actorId}");
            return;
        }

        MapLogger.Info($"[iRO MAP DEBUG] NPC interaction actorId={actorId} entity='{entity.Id}'");
        await StartScriptAsync(entity, actorId, script, "OnClick", cancellationToken);
    }

    private async Task StartScriptAsync(WorldEntityDefinition entity, uint actorId, ScriptBehaviorDefinition script, string trigger, CancellationToken cancellationToken)
    {
        if (HasActiveScript || entity.Actor is null) return;
        if (_worldMapRegistry.Scripts.TryCreate(entity.Id, trigger, out var generatedScript))
        {
            await StartGeneratedScriptAsync(entity, actorId, script, generatedScript, trigger, cancellationToken);
            return;
        }
        if (script.Instructions is not { Count: > 0 }) return;
        _scriptExecutionSession = new ScriptExecutionSession(entity.Id, actorId, entity.Actor.Name, script.BaseNpcName, entity.Actor.Map, script.Instructions);
        MapLogger.Info($"[iRO MAP DEBUG] Script start entity='{entity.Id}' trigger={trigger}");
        await SendScriptOutputAsync(_scriptExecutionSession.Run(), cancellationToken);
    }

    private async Task HandleNpcNextAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (IroNpcDialoguePackets.TryParseNext(packet, out var generatedActorId) && await TryResumeGeneratedScriptAsync(generatedActorId, GeneratedContinuationKind.Next, 0, cancellationToken)) return;
        if (!IroNpcDialoguePackets.TryParseNext(packet, out var actorId) || _scriptExecutionSession is null) return;
        var output = _scriptExecutionSession.ResumeNext(actorId);
        if (output.Count == 0) return;
        MapLogger.Info($"[iRO MAP DEBUG] Script resumed reason=Next entity='{_scriptExecutionSession.EntityId}'");
        await SendScriptOutputAsync(output, cancellationToken);
    }

    private async Task HandleNpcSelectionAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (IroNpcDialoguePackets.TryParseSelection(packet, out var generatedActorId, out var generatedWireIndex, out _) && generatedWireIndex > 0 &&
            await TryResumeGeneratedScriptAsync(generatedActorId, GeneratedContinuationKind.Selection, generatedWireIndex, cancellationToken)) return;
        if (!IroNpcDialoguePackets.TryParseSelection(packet, out var actorId, out var wireIndex, out _) || _scriptExecutionSession is null || wireIndex == 0) return;
        var output = _scriptExecutionSession.ResumeSelection(actorId, wireIndex - 1);
        if (output.Count == 0) return;
        MapLogger.Info($"[iRO MAP DEBUG] Script selection response entity='{_scriptExecutionSession.EntityId}' wireIndex={wireIndex}");
        MapLogger.Info($"[iRO MAP DEBUG] Script resumed reason=Selection option={wireIndex - 1} entity='{_scriptExecutionSession.EntityId}'");
        await SendScriptOutputAsync(output, cancellationToken);
    }

    private async Task SendScriptOutputAsync(IReadOnlyList<ScriptInstructionDefinition> instructions, CancellationToken cancellationToken)
    {
        var execution = _scriptExecutionSession!;
        foreach (var instruction in instructions)
        {
            var result = await ExecuteInstructionAsync(execution, instruction, cancellationToken);
            if (result == InstructionExecutionResult.Stop)
            {
                return;
            }
        }

        if (execution.State == ScriptExecutionState.Closed)
        {
            ClearScript(execution);
        }
    }

    private async Task<InstructionExecutionResult> ExecuteInstructionAsync(
        ScriptExecutionSession execution,
        ScriptInstructionDefinition instruction,
        CancellationToken cancellationToken) => instruction switch
        {
            MessageInstruction message => await ExecuteMessageInstructionAsync(execution, message, cancellationToken),
            NextInstruction => await ExecuteNextInstructionAsync(execution, cancellationToken),
            SelectInstruction select => await ExecuteSelectInstructionAsync(execution, select, cancellationToken),
            CloseInstruction => await ExecuteCloseInstructionAsync(execution, cancellationToken),
            Close2Instruction => await ExecuteClose2InstructionAsync(execution, cancellationToken),
            AssignmentInstruction assignment => ExecuteAssignmentInstruction(execution, assignment),
            WarpInstruction warp => await ExecuteWarpInstructionAsync(execution, warp, cancellationToken),
            SavePointInstruction savePoint => await ExecuteSavePointInstructionAsync(execution, savePoint, cancellationToken),
            SetQuestInstruction setQuest => await ExecuteSetQuestInstructionAsync(execution, setQuest, cancellationToken),
            CompleteQuestInstruction completeQuest => await ExecuteCompleteQuestInstructionAsync(execution, completeQuest, cancellationToken),
            IfQuestStateInstruction check => await ExecuteQuestStateInstructionAsync(execution, check, cancellationToken),
            _ => InstructionExecutionResult.Continue,
        };

    private async Task<InstructionExecutionResult> ExecuteMessageInstructionAsync(
        ScriptExecutionSession execution,
        MessageInstruction message,
        CancellationToken cancellationToken)
    {
        MapLogger.Info($"[iRO MAP DEBUG] Script message entity='{execution.EntityId}' actorId={execution.ActorId}");
        await WriteAsync(IroNpcDialoguePackets.BuildMessage(execution.ActorId, message.Text), cancellationToken);
        return InstructionExecutionResult.Continue;
    }

    private async Task<InstructionExecutionResult> ExecuteNextInstructionAsync(
        ScriptExecutionSession execution,
        CancellationToken cancellationToken)
    {
        await WriteAsync(IroNpcDialoguePackets.BuildNext(execution.ActorId), cancellationToken);
        MapLogger.Info($"[iRO MAP DEBUG] Script suspended reason=Next entity='{execution.EntityId}'");
        return InstructionExecutionResult.Continue;
    }

    private async Task<InstructionExecutionResult> ExecuteSelectInstructionAsync(
        ScriptExecutionSession execution,
        SelectInstruction select,
        CancellationToken cancellationToken)
    {
        MapLogger.Info($"[iRO MAP DEBUG] Script selection shown entity='{execution.EntityId}' options={select.Options.Count}");
        await WriteAsync(
            IroNpcDialoguePackets.BuildMenu(execution.ActorId, select.Options.Select(option => option.Text).ToArray()),
            cancellationToken);
        MapLogger.Info($"[iRO MAP DEBUG] Script suspended reason=Selection entity='{execution.EntityId}'");
        return InstructionExecutionResult.Continue;
    }

    private async Task<InstructionExecutionResult> ExecuteCloseInstructionAsync(
        ScriptExecutionSession execution,
        CancellationToken cancellationToken)
    {
        // Clear server-side state before exposing the close packet to the client.
        // Otherwise a client/test can observe the close while ActiveScriptState is still Closed.
        ClearScript(execution);
        await WriteAsync(IroNpcDialoguePackets.BuildClose(execution.ActorId), cancellationToken);
        MapLogger.Info($"[iRO MAP DEBUG] Script closed entity='{execution.EntityId}'");
        return InstructionExecutionResult.Stop;
    }

    private async Task<InstructionExecutionResult> ExecuteClose2InstructionAsync(
        ScriptExecutionSession execution,
        CancellationToken cancellationToken)
    {
        await WriteAsync(IroNpcDialoguePackets.BuildClose(execution.ActorId), cancellationToken);
        MapLogger.Info($"[iRO MAP DEBUG] Script dialogue closed; execution continues entity='{execution.EntityId}'");
        return InstructionExecutionResult.Continue;
    }

    private static InstructionExecutionResult ExecuteAssignmentInstruction(
        ScriptExecutionSession execution,
        AssignmentInstruction assignment)
    {
        execution.Assign(assignment.Variable, assignment.Value);
        return InstructionExecutionResult.Continue;
    }

    private async Task<InstructionExecutionResult> ExecuteWarpInstructionAsync(
        ScriptExecutionSession execution,
        WarpInstruction warp,
        CancellationToken cancellationToken)
    {
        await ExecuteScriptWarpAsync(execution, warp, cancellationToken);
        return InstructionExecutionResult.Continue;
    }

    private async Task<InstructionExecutionResult> ExecuteSavePointInstructionAsync(
        ScriptExecutionSession execution,
        SavePointInstruction savePoint,
        CancellationToken cancellationToken)
    {
        if (await SavePointAsync(execution.Evaluate(savePoint.Map), savePoint.X, savePoint.Y, cancellationToken))
        {
            return InstructionExecutionResult.Continue;
        }

        MapLogger.Warning($"SavePoint persistence aborted script entity='{execution.EntityId}' charId={_charId}.");
        ClearScript(execution);
        return InstructionExecutionResult.Stop;
    }

    private async Task<InstructionExecutionResult> ExecuteSetQuestInstructionAsync(
        ScriptExecutionSession execution,
        SetQuestInstruction setQuest,
        CancellationToken cancellationToken)
    {
        if (await SetQuestAsync(setQuest.QuestId, cancellationToken))
        {
            return InstructionExecutionResult.Continue;
        }

        await AbortScriptForPersistenceFailureAsync(execution, setQuest.QuestId, cancellationToken);
        return InstructionExecutionResult.Stop;
    }

    private async Task<InstructionExecutionResult> ExecuteCompleteQuestInstructionAsync(
        ScriptExecutionSession execution,
        CompleteQuestInstruction completeQuest,
        CancellationToken cancellationToken)
    {
        if (await CompleteQuestAsync(completeQuest.QuestId, cancellationToken))
        {
            return InstructionExecutionResult.Continue;
        }

        await AbortScriptForPersistenceFailureAsync(execution, completeQuest.QuestId, cancellationToken);
        return InstructionExecutionResult.Stop;
    }

    private async Task<InstructionExecutionResult> ExecuteQuestStateInstructionAsync(
        ScriptExecutionSession execution,
        IfQuestStateInstruction check,
        CancellationToken cancellationToken)
    {
        if (check.QuestId == 0)
        {
            return InstructionExecutionResult.Stop;
        }

        var state = await _questPersistence.GetQuestStateAsync(_accountId, _charId, check.QuestId, cancellationToken);
        if (state is null)
        {
            await AbortScriptForPersistenceFailureAsync(execution, check.QuestId, cancellationToken);
            return InstructionExecutionResult.Stop;
        }

        await SendScriptOutputAsync(execution.ResumeQuestState(execution.ActorId, state.Value), cancellationToken);
        return InstructionExecutionResult.Continue;
    }

    private void ClearScript(ScriptExecutionSession execution)
    {
        if (ReferenceEquals(_scriptExecutionSession, execution))
        {
            _scriptExecutionSession = null;
        }
    }

    private async Task AbortScriptForPersistenceFailureAsync(ScriptExecutionSession execution, uint questId, CancellationToken cancellationToken)
    {
        MapLogger.Warning($"Quest persistence aborted script entity='{execution.EntityId}' charId={_charId} questId={questId}.");

        ClearScript(execution);
        await WriteAsync(IroNpcDialoguePackets.BuildClose(execution.ActorId), cancellationToken);
    }

    private async Task<bool> SetQuestAsync(uint questId, CancellationToken cancellationToken)
    {
        if (questId == 0) return false;
        var current = await _questPersistence.GetQuestStateAsync(_accountId, _charId, questId, cancellationToken);
        if (current is null) return false;
        var next = QuestStateRules.SetQuest(current.Value);
        if (next == current) return true;
        if (!await _questPersistence.SetQuestStateAsync(_accountId, _charId, questId, next, cancellationToken)) return false;
        await WriteAsync(IroQuestPackets.BuildAddActive(questId), cancellationToken);
        return true;
    }

    private async Task<bool> CompleteQuestAsync(uint questId, CancellationToken cancellationToken)
    {
        if (questId == 0) return false;
        var current = await _questPersistence.GetQuestStateAsync(_accountId, _charId, questId, cancellationToken);
        if (current is null) return false;
        var next = QuestStateRules.CompleteQuest(current.Value);
        if (next == current) return true;
        if (!await _questPersistence.SetQuestStateAsync(_accountId, _charId, questId, next, cancellationToken)) return false;
        await WriteAsync(IroQuestPackets.BuildRemove(questId), cancellationToken);
        return true;
    }

    private async Task ExecuteScriptWarpAsync(ScriptExecutionSession execution, WarpInstruction warp, CancellationToken cancellationToken)
    {
        var map = execution.Evaluate(warp.Map);
        if (string.IsNullOrWhiteSpace(map)) throw new InvalidOperationException("Warp map expression evaluated to an empty value.");
        MapLogger.Info($"[iRO MAP DEBUG] Script warp entity='{execution.EntityId}' map='{_mapName}' -> map='{map}' x={warp.X} y={warp.Y}");
        var sourceMap = _presenceMapId ?? _mapName;
        await LeavePlayerWorldAsync(PlayerSessionLifecycle.AuthenticatedButNotWorldVisible, cancellationToken);
        TeleportTo(map, warp.X, warp.Y); _positionDirty = true; _visibleActorIds.Clear();
        await TransferDistributedPresenceAsync(sourceMap, _mapName, _x, _y, cancellationToken);
        await WriteAsync(IroMapTransitionPackets.BuildSameServerMapChange(_mapName, _x, _y), cancellationToken);
        await PersistPositionIfDirtyAsync(cancellationToken);
    }

    private async Task<bool> SavePointAsync(string map, ushort x, ushort y, CancellationToken cancellationToken)
    {
        var saved = await _positionPersistence.SavePointAsync(_accountId, _charId, map, x, y, cancellationToken);
        if (saved) MapLogger.Info($"SavePoint persistence succeeded charId={_charId} map='{map}' x={x} y={y}.");
        else MapLogger.Warning($"SavePoint persistence failed charId={_charId} map='{map}' x={x} y={y}.");
        return saved;
    }

    private bool HasActiveScript => _scriptExecutionSession is not null || _generatedScriptTask is not null;

    private async Task StartGeneratedScriptAsync(
        WorldEntityDefinition entity,
        uint actorId,
        ScriptBehaviorDefinition binding,
        INpcScript script,
        string trigger,
        CancellationToken cancellationToken)
    {
        _generatedScriptEntityId = entity.Id;
        _generatedScriptActorId = actorId;
        _generatedSuspended = NewSignal();
        var context = new ScriptContext(this, entity.Id, actorId, entity.Actor!.Name, binding.BaseNpcName);
        _generatedScriptTask = ExecuteGeneratedScriptAsync(script, context, cancellationToken);
        MapLogger.Info($"[iRO MAP DEBUG] Generated script start entity='{entity.Id}' trigger={trigger}");
        await WaitForGeneratedBoundaryAsync(cancellationToken);
    }

    private async Task ExecuteGeneratedScriptAsync(INpcScript script, ScriptContext context, CancellationToken cancellationToken)
    {
        try
        {
            await script.ExecuteAsync(context, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _sessionCancellation.IsCancellationRequested)
        {
        }
        catch (ScriptMutationFailedException exception)
        {
            // A fallible generated-script mutation (delitem/getitem) reported an authoritative
            // persistence failure. The remaining statement sequence never ran (no further
            // reward/completequest/success dialogue), matching AGENTS.md's "do not report success
            // before required persistence succeeds". Already-applied earlier statements in this
            // same script are NOT rolled back (no distributed idempotency - ai/world-data.md).
            // Minimal client-side interaction close, matching the generic catch below's own
            // precedent for a mid-flow script abort.
            MapLogger.Warning($"[iRO MAP DEBUG] Generated script mutation failed entity='{context.EntityId}': {exception.Message}");
            await WriteAsync(IroNpcDialoguePackets.BuildClose(context.ActorId), CancellationToken.None);
        }
        catch (Exception exception)
        {
            MapLogger.Warning($"Generated script aborted entity='{context.EntityId}' error={exception.GetType().Name}: {exception.Message}");
            await WriteAsync(IroNpcDialoguePackets.BuildClose(context.ActorId), CancellationToken.None);
        }
    }

    private async Task<bool> TryResumeGeneratedScriptAsync(uint actorId, GeneratedContinuationKind kind, int value, CancellationToken cancellationToken)
    {
        var continuation = _generatedContinuation;
        if (_generatedScriptTask is null || continuation is null || _generatedScriptActorId != actorId || continuation.Kind != kind) return false;
        _generatedSuspended = NewSignal();
        _generatedContinuation = null;
        continuation.Completion.TrySetResult(value);
        MapLogger.Info($"[iRO MAP DEBUG] Generated script resumed reason={kind} entity='{_generatedScriptEntityId}'");
        await WaitForGeneratedBoundaryAsync(cancellationToken);
        return true;
    }

    private async Task WaitForGeneratedBoundaryAsync(CancellationToken cancellationToken)
    {
        var scriptTask = _generatedScriptTask;
        if (scriptTask is null) return;
        await Task.WhenAny(scriptTask, _generatedSuspended.Task).WaitAsync(cancellationToken);
        if (!scriptTask.IsCompleted) return;
        await scriptTask;
        MapLogger.Info($"[iRO MAP DEBUG] Generated script completed entity='{_generatedScriptEntityId}'");
        _generatedScriptTask = null;
        _generatedScriptEntityId = null;
        _generatedScriptActorId = 0;
        _generatedContinuation = null;
    }

    async Task INpcScriptHost.MesAsync(uint actorId, string text, CancellationToken cancellationToken) =>
        await WriteAsync(IroNpcDialoguePackets.BuildMessage(actorId, text), cancellationToken);

    string INpcScriptHost.GetActiveCharacterName() =>
        !string.IsNullOrWhiteSpace(_characterName)
            ? _characterName
            : throw new InvalidOperationException("The active authenticated character name is not loaded.");

    CharacterGameplayState INpcScriptHost.GetGameplayState() =>
        _gameplayState?.State ?? throw new InvalidOperationException("Character gameplay state is not loaded.");

    async Task INpcScriptHost.NextAsync(uint actorId, CancellationToken cancellationToken)
    {
        var continuation = new GeneratedContinuation(GeneratedContinuationKind.Next, NewContinuation());
        var suspended = _generatedSuspended;
        // Publish the accepted input before exposing the boundary packet to the client. A peer can
        // receive 0x00B5 and send 0x00B9 before NetworkStream.WriteAsync's continuation runs; if
        // publication happens after the write, that valid reply observes null and is discarded.
        _generatedContinuation = continuation;
        await WriteAsync(IroNpcDialoguePackets.BuildNext(actorId), cancellationToken);
        // Signal the boundary captured above, not the mutable field. An early 0x00B9 replaces the
        // field with the NEXT boundary; signaling through the field would then wake the wrong wait
        // and orphan the StartGeneratedScriptAsync/previous-resume waiter.
        suspended.TrySetResult();
        await continuation.Completion.Task.WaitAsync(cancellationToken);
    }

    async Task<int> INpcScriptHost.SelectAsync(uint actorId, IReadOnlyList<string> options, CancellationToken cancellationToken)
    {
        var continuation = new GeneratedContinuation(GeneratedContinuationKind.Selection, NewContinuation());
        var suspended = _generatedSuspended;
        _generatedContinuation = continuation;
        await WriteAsync(IroNpcDialoguePackets.BuildMenu(actorId, options), cancellationToken);
        suspended.TrySetResult();
        return await continuation.Completion.Task.WaitAsync(cancellationToken);
    }

    Task INpcScriptHost.CloseAsync(uint actorId, CancellationToken cancellationToken) =>
        WriteAsync(IroNpcDialoguePackets.BuildClose(actorId), cancellationToken);

    async Task INpcScriptHost.Close2Async(uint actorId, CancellationToken cancellationToken)
    {
        var continuation = new GeneratedContinuation(GeneratedContinuationKind.Close2, NewContinuation());
        var suspended = _generatedSuspended;
        _generatedContinuation = continuation;
        await WriteAsync(IroNpcDialoguePackets.BuildClose(actorId), cancellationToken);
        suspended.TrySetResult();
        await continuation.Completion.Task.WaitAsync(cancellationToken);
    }

    async Task<CharacterQuestStatus> INpcScriptHost.GetQuestStateAsync(QuestId questId, CancellationToken cancellationToken) =>
        await _questPersistence.GetQuestStateAsync(_accountId, _charId, questId.Value, cancellationToken)
            ?? throw new InvalidOperationException($"Quest state query failed for quest {questId.Value}.");

    async Task INpcScriptHost.SetQuestAsync(QuestId questId, CancellationToken cancellationToken)
    {
        if (!await SetQuestAsync(questId.Value, cancellationToken)) throw new InvalidOperationException($"SetQuest persistence failed for quest {questId.Value}.");
    }

    async Task INpcScriptHost.CompleteQuestAsync(QuestId questId, CancellationToken cancellationToken)
    {
        if (!await CompleteQuestAsync(questId.Value, cancellationToken)) throw new InvalidOperationException($"CompleteQuest persistence failed for quest {questId.Value}.");
    }

    async Task INpcScriptHost.WarpAsync(string map, ushort x, ushort y, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(map)) throw new InvalidOperationException("Generated script warp map is empty.");
        var sourceMap = _presenceMapId ?? _mapName;
        await LeavePlayerWorldAsync(PlayerSessionLifecycle.AuthenticatedButNotWorldVisible, cancellationToken);
        TeleportTo(map, x, y); _positionDirty = true; _visibleActorIds.Clear();
        await TransferDistributedPresenceAsync(sourceMap, _mapName, _x, _y, cancellationToken);
        await WriteAsync(IroMapTransitionPackets.BuildSameServerMapChange(map, x, y), cancellationToken);
        await PersistPositionIfDirtyAsync(cancellationToken);
    }

    async Task INpcScriptHost.SetSavePointAsync(string map, ushort x, ushort y, CancellationToken cancellationToken)
    {
        if (!await SavePointAsync(map, x, y, cancellationToken)) throw new InvalidOperationException($"SavePoint persistence failed for map '{map}'.");
    }

    Task INpcScriptHost.CutinAsync(string image, byte position, CancellationToken cancellationToken)
    {
        MapLogger.Info($"[iRO MAP DEBUG] Sending 0x01B3 cutin image='{image}' position={position} entity='{_generatedScriptEntityId}'");
        return WriteAsync(IroNpcDialoguePackets.BuildCutin(image, position), cancellationToken);
    }

    Task INpcScriptHost.NpcTalkAsync(uint actorId, string text, CancellationToken cancellationToken) =>
        WriteAsync(IroNpcDialoguePackets.BuildNpcTalk(actorId, text), cancellationToken);

    Task INpcScriptHost.SetNpcCloakAsync(string entityIdOrName, bool cloaked, CancellationToken cancellationToken)
    {
        if (!_worldMapRegistry.TryGetActor(entityIdOrName, _mapName, out var actor))
            throw new InvalidOperationException($"Generated script NPC target '{entityIdOrName}' was not found on map '{_mapName}'.");
        return WriteAsync(IroNpcDialoguePackets.BuildNpcOption(actor.ActorId, cloaked ? 4u : 0u), cancellationToken);
    }

    Task INpcScriptHost.NavigateToAsync(string map, ushort x, ushort y, CancellationToken cancellationToken) =>
        WriteAsync(IroNpcDialoguePackets.BuildNavigateTo(map, x, y), cancellationToken);

    async Task INpcScriptHost.GrantExperienceAsync(long baseExperience, long jobExperience, CancellationToken cancellationToken)
    {
        var state = _gameplayState ?? throw new InvalidOperationException("Character gameplay state is not loaded.");
        var (ratedBaseExp, ratedJobExp) = ExperienceRewardService.ResolveReward(_rates, baseExperience, jobExperience, ExperienceSource.Script);
        var result = await new CharacterProgressionService(state).AddExperienceAsync(ratedBaseExp, ratedJobExp, cancellationToken)
            ?? throw new InvalidOperationException("Character progression persistence failed.");
        foreach (var packet in IroCharacterProgressionPackets.Build(_accountId, result)) await WriteAsync(packet, cancellationToken);
    }

    // Frame 3496 of npc-interaction-heal-action.pcapng (Captain Carocc's completion burst;
    // see ai/iro-2026-wire.md for the complete byte segmentation) proves heal 9999,0 is
    // followed by a ZC_USE_SKILL (0x09CB) visual: SKID=28 (AL_HEAL), level=9999 (the exact
    // heal amount, not the resulting HP), src=the executing NPC's actor, target=player,
    // result=1. The pinned BUILDIN_FUNC(heal)/status_heal path does not itself call
    // clif_skill_nodamage for this case, so the precise source line producing this packet
    // was not conclusively located in static source; the packet's existence, layout, and
    // field values are wire-proven and used as-is, attributed to HealAsync (not
    // SpecialEffectAsync/SkillEffectAsync - see their remarks) because its level value
    // matches the heal amount, not any skilleffect argument in Captain's script.
    async Task INpcScriptHost.HealAsync(int hp, int sp, CancellationToken cancellationToken)
    {
        var state = _gameplayState ?? throw new InvalidOperationException("Character gameplay state is not loaded.");
        var result = await new CharacterHealService(state).HealAsync(hp, sp, cancellationToken)
            ?? throw new InvalidOperationException("Heal persistence failed.");
        // No parameter packet is sent when the mutation left HP/SP unchanged (e.g. an
        // already-full heal) - there is nothing to synchronize. This mirrors the existing
        // GrantExperienceAsync policy of only emitting packets for fields that actually
        // changed; it is not Captain-specific behavior.
        if (result.HpChanged) await WriteAsync(IroCharacterProgressionPackets.Parameter(5, result.After.CurrentHp), cancellationToken);
        if (result.SpChanged) await WriteAsync(IroCharacterProgressionPackets.Parameter(7, result.After.CurrentSp), cancellationToken);
        if (result.HpChanged && hp > 0) await WriteAsync(IroStatusEffectPackets.BuildUseSkillVisual(IroStatusEffectPackets.AlHeal, hp, _accountId, _generatedScriptActorId), cancellationToken);
    }

    // Pinned legacy/rathena/src/map/script.cpp BUILDIN_FUNC(specialeffect2) calls
    // clif_specialeffect (-> ZC_NOTIFY_EFFECT2, 0x01F3), which the capture proves was NOT
    // sent anywhere in Captain's completion burst (no 0x01F3 bytes anywhere in the
    // reassembled stream; see ai/iro-2026-wire.md). This remains a documented, unproven gap:
    // the server-side effect is presentation-only (no persistent state), so there is nothing
    // to mutate, and no packet is synthesized without independent wire proof.
    Task INpcScriptHost.SpecialEffectAsync(int effectId, CancellationToken cancellationToken) => Task.CompletedTask;

    // Pinned skilleffect(skillId, level) -> script_skill_effect -> clif_skill_nodamage for a
    // CAST_NODAMAGE skill (AL_INCAGI/AL_BLESSING both are), producing the same ZC_USE_SKILL
    // (0x09CB) family independently proven by the capture for Captain's Blessing/Increase AGI
    // activation (SKID=34/level=10 and SKID=29/level=10 respectively, src=Captain's actor,
    // target=player). Emitted here (once per skilleffect call, exactly matching Captain's
    // script order) rather than folded into StartStatusAsync, since skilleffect and sc_start
    // are independent script commands with independent capture-proven packets.
    Task INpcScriptHost.SkillEffectAsync(int skillId, int level, CancellationToken cancellationToken) =>
        WriteAsync(IroStatusEffectPackets.BuildUseSkillVisual((ushort)skillId, level, _accountId, _generatedScriptActorId), cancellationToken);

    // sc_start's generic server-side semantics (CharacterStatusEffectState.Start) plus, for
    // the two currently modeled statuses, their capture-proven client synchronization: a
    // ZC_MSG_STATE_CHANGE3 (0x0983) activation icon and the ZC_COUPLESTATUS (0x0141)/generic
    // parameter packets for whichever effective stats that status changes, all independently
    // verified against frame 3496 of npc-interaction-heal-action.pcapng (see
    // ai/iro-2026-wire.md and CharacterStatusEffectState's remarks for the exact val1/val2
    // derivation). Other statusIds apply only server-side state, matching the generic,
    // non-Captain-specific policy used throughout this host.
    async Task INpcScriptHost.StartStatusAsync(int statusId, int durationMilliseconds, int val1, CancellationToken cancellationToken)
    {
        var id = (ushort)statusId;
        _statusEffects.Start(id, durationMilliseconds, val1);
        // Wake the expiration loop: this may have moved the next deadline earlier. Best-effort -
        // SemaphoreFullException means a wake is already pending (the loop hasn't consumed it
        // yet), which is fine, it will re-read NextExpiration fresh when it does; the signal may
        // also already be disposed if the session is tearing down concurrently.
        try { _statusExpirationSignal.Release(); } catch (ObjectDisposedException) { } catch (SemaphoreFullException) { }
        if (!_statusEffects.TryGet(id, out var status)) return;
        var actorId = _accountId;

        if (id == CharacterStatusEffectState.StatusIds.Blessing)
        {
            await WriteAsync(IroStatusEffectPackets.BuildStatusChange3(actorId, IroStatusEffectPackets.EfstBlessing, true, durationMilliseconds, durationMilliseconds, status.Val1), cancellationToken);
            await WriteAsync(IroStatusEffectPackets.BuildCoupleStatus(IroStatusEffectPackets.SpStr, (_gameplayState?.State.Strength ?? 0), status.Val2), cancellationToken);
            await WriteAsync(IroStatusEffectPackets.BuildCoupleStatus(IroStatusEffectPackets.SpInt, (_gameplayState?.State.Intelligence ?? 0), status.Val2), cancellationToken);
            await WriteAsync(IroStatusEffectPackets.BuildCoupleStatus(IroStatusEffectPackets.SpDex, (_gameplayState?.State.Dexterity ?? 0), status.Val2), cancellationToken);
        }
        else if (id == CharacterStatusEffectState.StatusIds.IncreaseAgi)
        {
            await WriteAsync(IroStatusEffectPackets.BuildStatusChange3(actorId, IroStatusEffectPackets.EfstIncAgi, true, durationMilliseconds, durationMilliseconds, status.Val1), cancellationToken);
            await WriteAsync(IroStatusEffectPackets.BuildCoupleStatus(IroStatusEffectPackets.SpAgi, (_gameplayState?.State.Agility ?? 0), status.Val2), cancellationToken);
        }
    }

    // Pinned countitem(itemId) (script.cpp BUILDIN_FUNC(countitem)) sums the character's
    // authoritative quantity of a given item id across every matching stack - never a single-row
    // assumption (a stackable item can legitimately be split across more than one CharInventory
    // row). This reads directly from the session's already-authoritative in-memory
    // CharacterInventorySnapshot (_inventory) rather than a fresh CharServer round-trip: the
    // snapshot is kept in lockstep with every confirmed persistence mutation (see the class-level
    // remarks on _inventory), so it is already the authoritative live count for this session.
    Task<uint> INpcScriptHost.CountItemAsync(int itemId, CancellationToken cancellationToken) =>
        Task.FromResult(_inventory is { } inventory
            ? (uint)inventory.Items.Where(item => item.ItemId == itemId).Sum(item => (long)item.Amount)
            : 0u);

    // Pinned delitem itemId,amount (pc.cpp pc_delitem, script.cpp BUILDIN_FUNC(delitem)) is
    // ITEM-ID based, unlike CharacterInventorySession.ConsumeItemAsync which is DURABLE-ROW
    // based (see that method's own doc comment) - this host method is exactly the itemId ->
    // durableId(s) resolution layer pinned pc_delitem performs internally by scanning its own
    // inventory array for every matching row.
    //
    // Sufficiency is checked BEFORE any persistence call (matches CountItemAsync's own summation
    // exactly, so a caller that just checked countitem(itemId) >= amount can never observe this
    // method disagree) - a partial consumption across some-but-not-all rows must never be allowed
    // to proceed once the upfront total already proved insufficient. Rows are then consumed in
    // stable ascending-DurableId order (CharacterInventorySnapshot's own row-identity ordering,
    // matching pinned pc_delitem's own array-scan-in-order behavior) until `amount` is satisfied,
    // each via the existing single-row ConsumeItemAsync/_inventory update pattern already used at
    // this class's other consume call sites (HandleIroUseItemRequestAsync above).
    //
    // No fake success: if every row succeeds, this returns true and _inventory/_equipment already
    // reflect every consumed row. If a persistence call fails PART-WAY through (a genuine mid-loop
    // persistence failure after some rows already succeeded), the already-consumed rows' state is
    // NOT rolled back (matching this project's already-documented "no distributed idempotency"
    // limitation for inventory persistence - ai/world-data.md's "Inventory persistence guarantees"
    // section), but this method still returns false rather than reporting a false success, and logs
    // the partial-failure condition loudly so it is never silently swallowed.
    async Task<bool> INpcScriptHost.DeleteItemAsync(int itemId, uint amount, CancellationToken cancellationToken)
    {
        if (_inventory is not { } inventory) return false;

        var matchingRows = inventory.Items.Where(item => item.ItemId == itemId).OrderBy(item => item.DurableId).ToArray();
        var available = (uint)matchingRows.Sum(item => (long)item.Amount);
        if (available < amount)
        {
            MapLogger.Warning($"[iRO MAP DEBUG] delitem rejected: itemId={itemId} requested={amount} available={available}.");
            return false;
        }

        var inventorySession = new CharacterInventorySession(_accountId, _charId, _inventoryPersistence);
        var remaining = amount;
        foreach (var row in matchingRows)
        {
            if (remaining == 0) break;
            var consumeAmount = Math.Min(remaining, row.Amount);

            var consumeResult = await inventorySession.ConsumeItemAsync(row.DurableId, consumeAmount, cancellationToken);
            if (!consumeResult.Success)
            {
                MapLogger.Warning($"[iRO MAP DEBUG] delitem persistence failed mid-loop for itemId={itemId} durableId={row.DurableId}; {amount - remaining} of {amount} already consumed and NOT rolled back.");
                return false;
            }

            _inventory = consumeResult.RowDeleted
                ? _inventory.WithoutDurableId(row.DurableId)
                : _inventory.WithUpdatedItem(row.DurableId, row.ItemId, consumeResult.NewAmount, row.Equip, row.Identified, row.Refine, row.Favorite, row.Bound);
            _equipment = CharacterEquipmentSnapshot.FromInventory(_inventory);
            remaining -= consumeAmount;

            // ZC_DELETE_ITEM_FROM_BODY (0x07FA) - sent per-row, immediately after that row's own
            // authoritative persistence/snapshot update, using THAT row's own client-facing slot
            // (SlotIndex + 2, same transform GetItemAsync uses a few lines below for 0x0B41) and
            // THAT row's own consumed amount, never the total requested amount. See
            // sailor-packet-export.txt frame 7291 / IroMonsterCombatPackets.BuildDeleteItemFromBody.
            var deleteClientIndex = (ushort)(row.SlotIndex + 2);
            var deletePacket = IroMonsterCombatPackets.BuildDeleteItemFromBody((ushort)deleteClientIndex, (ushort)consumeAmount, PacketConstants.ZcDeleteItemFromBodyReasonScriptDelitem);
            MapLogger.Info($"[iRO MAP DEBUG] Sending 0x07FA for delitem itemId={itemId} durableId={row.DurableId} amount={consumeAmount} clientIndex={deleteClientIndex}");
            await WriteAsync(deletePacket, cancellationToken);
        }

        MapLogger.Info($"[iRO MAP DEBUG] delitem consumed itemId={itemId} amount={amount}.");
        return true;
    }

    // Pinned getitem itemId,amount (script.cpp BUILDIN_FUNC(getitem)) - a normal authoritative
    // inventory add through the SAME CharacterInventorySession/runtime-snapshot-update/0x0B41
    // pickup-ack path the quest-drop reward loop and item-use container grants already use (see
    // their own doc comments for the ordering rationale: persist -> update runtime snapshot ->
    // notify client). Resolves itemId generically via GeneratedItems.ById, matching every other
    // generated-script item lookup in this class - never a hardcoded item id here. An itemId
    // absent from the generated registry is a data/generation gap, logged and skipped rather than
    // guessed at, matching the existing quest-drop/item-use-grant convention exactly.
    //
    // Returns false (never throws itself) for every "the reward was not actually granted/the
    // client was not notified" outcome - unregistered itemId, missing runtime inventory snapshot,
    // or a genuine CharServer persistence failure alike - so ScriptContext's wrapper (the one seam
    // that decides generic generated-script mutation-failure semantics) can uniformly stop the
    // remaining generated statement sequence (no completequest after a failed reward) regardless
    // of which of these caused the failure.
    async Task<bool> INpcScriptHost.GetItemAsync(int itemId, uint amount, CancellationToken cancellationToken)
    {
        if (!GeneratedItems.ById.TryGetValue(itemId, out var itemDefinition))
        {
            MapLogger.Warning($"[iRO MAP DEBUG] getitem references unregistered itemId={itemId}; skipping.");
            return false;
        }

        if (_inventory is not { } inventory) return false;

        var inventorySession = new CharacterInventorySession(_accountId, _charId, _inventoryPersistence);
        var addResult = await inventorySession.AddItemAsync(itemDefinition, amount, cancellationToken);
        if (!addResult.Success || addResult.Item is not { } addedRow)
        {
            MapLogger.Warning($"[iRO MAP DEBUG] getitem persistence failed for itemId={itemId}; not notifying client.");
            return false;
        }

        _inventory = addResult.IsNewRow
            ? inventory.WithNewItem(addResult.DurableId, addedRow.ItemId, addedRow.Amount, addedRow.Equip, addedRow.Identified, addedRow.Refine, addedRow.Favorite, addedRow.Bound)
            : inventory.WithUpdatedItem(addResult.DurableId, addedRow.ItemId, addedRow.Amount, addedRow.Equip, addedRow.Identified, addedRow.Refine, addedRow.Favorite, addedRow.Bound);
        _equipment = CharacterEquipmentSnapshot.FromInventory(_inventory);

        var slotIndex = _inventory.Items.Single(i => i.DurableId == addResult.DurableId).SlotIndex;
        var clientIndex = (ushort)(slotIndex + 2);
        var pickupPacket = IroMonsterCombatPackets.BuildItemPickupAck(clientIndex, (ushort)amount, itemDefinition.ClientViewId, IroInventoryListPackets.ItemType(itemDefinition));
        MapLogger.Info($"[iRO MAP DEBUG] Sending 0x0B41 for getitem itemId={itemId} count={amount} clientIndex={clientIndex}");
        await WriteAsync(pickupPacket, cancellationToken);
        return true;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<int> NewContinuation() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed record GeneratedContinuation(GeneratedContinuationKind Kind, TaskCompletionSource<int> Completion);
    private enum InstructionExecutionResult { Continue, Stop }
    private enum GeneratedContinuationKind { Next, Selection, Close2 }

    // Pinned map_session_data::update_look(LOOK_WEAPON) (pc.cpp:623-647): the wire value is the
    // equipped item's AliasName-resolved view_id, falling back to its own nameid - NOT the
    // weapon_type enum (verified stock-iRO capture, kill-poring-heal-jobup frame 210: Knife
    // 1201's LOOK_WEAPON val=1201, not 1=W_DAGGER). A confirmed-unarmed right hand sends 0
    // (update_look's "Nothing equipped" branch), never skips the packet.
    // _equipment is guaranteed non-null once authenticated (CompleteIroAuthenticationAsync
    // fails auth outright on an unsuccessful equipment read - see
    // ICharacterEquipmentPersistence/CharacterEquipmentReadResult), so this never runs with
    // an unknown equipment state. Resolution goes through the one shared EquippedWeaponResolver
    // path - no Knife-specific branching. An UnknownItem/NonWeaponInWeaponSlot resolution is a
    // data/generation invariant violation, not a legitimate appearance case, so it is logged and
    // skipped rather than guessed at.
    private async Task SendSelfWeaponAppearanceAsync(CancellationToken cancellationToken)
    {
        if (_equipment is not { } equipment) return;

        var resolution = EquippedWeaponResolver.Resolve(equipment, GeneratedItems.ById);
        var weaponViewId = resolution.Resolution switch
        {
            EquippedWeaponResolution.Unarmed => (uint?)0,
            EquippedWeaponResolution.Weapon => (uint)resolution.Weapon!.ClientViewId,
            _ => null,
        };
        if (weaponViewId is null)
        {
            MapLogger.Warning($"[iRO MAP DEBUG] Equipped right-hand item did not resolve to a weapon (resolution={resolution.Resolution}); skipping 0x01D7.");
            return;
        }

        var packet = IroCharacterAppearancePackets.BuildSpriteChangeWeapon(_accountId, weaponViewId.Value);
        MapLogger.Info($"[iRO MAP DEBUG] Sending 0x01D7 self weapon look weaponViewId={weaponViewId.Value}");
        await WriteAsync(packet, cancellationToken);
    }

    // Pinned clif_inventorylist(sd) (clif.cpp:3062-3143): sends the full authoritative
    // CharInventory snapshot to the client's own inventory/equip window - split into
    // equippable (0x0B39) and stackable (0x0B09) lists, bracketed by inventoryStart/End.
    // _inventory is guaranteed non-null once authenticated (CompleteIroAuthenticationAsync
    // fails auth outright on an unsuccessful inventory read), so this never runs with unknown
    // inventory state. An item id absent from GeneratedItems.ById is a data/generation gap
    // (never silently dropped from combat correctness, since it also never reaches
    // EquippedWeaponResolver's right-hand path any differently) - logged and excluded from
    // the client-facing list rather than guessed at, matching the existing 0x0B41 convention.
    private async Task SendSelfInventoryAsync(CancellationToken cancellationToken)
    {
        if (_inventory is not { } inventory) return;

        var equip = new List<(ushort ClientIndex, CharacterInventoryItem Item, IEquippableItemDefinition Definition)>();
        var normal = new List<(ushort ClientIndex, CharacterInventoryItem Item, ItemDefinition Definition)>();
        foreach (var item in inventory.Items)
        {
            if (!GeneratedItems.ById.TryGetValue(item.ItemId, out var definition))
            {
                MapLogger.Warning($"[iRO MAP DEBUG] Inventory row references unregistered itemId={item.ItemId}; excluding from 0x0B09/0x0B39.");
                continue;
            }

            // client_index(): server-side array position + 2 (clif.cpp:122-124) - same
            // convention as the existing 0x0B41 pickup path.
            var clientIndex = (ushort)(item.SlotIndex + 2);
            if (definition is IEquippableItemDefinition equippable)
                equip.Add((clientIndex, item, equippable));
            else
                normal.Add((clientIndex, item, definition));
        }

        MapLogger.Info($"[iRO MAP DEBUG] Sending self inventory/equip list equipCount={equip.Count} normalCount={normal.Count}");
        await WriteAsync(IroInventoryListPackets.BuildInventoryStart(), cancellationToken);
        // Pinned clif_inventorylist (clif.cpp:3112-3130): when neither batch fills mid-loop
        // (this slice's starter inventory never does), the normal-item flush happens BEFORE
        // the equip-item flush - order matters, do not swap.
        if (normal.Count > 0)
            await WriteAsync(IroInventoryListPackets.BuildItemListNormal(normal), cancellationToken);
        if (equip.Count > 0)
            await WriteAsync(IroInventoryListPackets.BuildItemListEquip(equip), cancellationToken);
        await WriteAsync(IroInventoryListPackets.BuildInventoryEnd(), cancellationToken);
    }

    private async Task EnterPlayerWorldAsync(CancellationToken cancellationToken)
    {
        Guid presenceId;
        bool firstRegistration;
        lock (_playerPresenceGate)
        {
            if (_playerLifecycle == PlayerSessionLifecycle.AuthenticatedButNotWorldVisible)
            {
                presenceId = _presenceId ??= Guid.NewGuid();
                firstRegistration = true;
            }
            else if (_playerLifecycle == PlayerSessionLifecycle.WorldVisible)
            {
                // A repeated load-end can represent a caller replay after it did not observe the
                // first registration result. Reuse the lifecycle identity and let the grain's
                // idempotent contract distinguish this from a genuinely different presence.
                presenceId = _presenceId ?? throw new InvalidOperationException("World-visible player has no logical presence identity.");
                firstRegistration = false;
            }
            else return;
        }

        var presence = BuildCurrentPresence(movement: null);
        // Some legacy focused tests authenticate with an intentionally empty character name.
        // Production CharServer auth always supplies the authoritative name; only a complete
        // public projection is eligible to become world-visible.
        if (presence is null) return;

        if (firstRegistration)
            await _playerVisibility.RegisterAsync(presence, this, cancellationToken);
        if (_distributedWorld is not null)
        {
            try
            {
                var registration = await _distributedWorld.RegisterPresenceAsync(
                    presence.MapName,
                    new WorldPlayerPresence(presenceId, presence.ActorId, presence.CharacterId, presence.MapName, presence.X, presence.Y),
                    cancellationToken);
                if (registration.Status == WorldPresenceRegistrationStatus.Conflict)
                    throw new InvalidOperationException($"Character {presence.CharacterId} is already present in map authority '{registration.MapId}'.");
            }
            catch
            {
                if (firstRegistration)
                    await _playerVisibility.UnregisterAsync(presence.ActorId, CancellationToken.None);
                throw;
            }
        }
        if (!firstRegistration) return;
        lock (_playerPresenceGate)
        {
            _presence = presence;
            _presenceMapId = presence.MapName;
            _playerLifecycle = PlayerSessionLifecycle.WorldVisible;
        }
    }

    private PlayerPresence? BuildCurrentPresence(PlayerMovementPresence? movement)
    {
        var gameplay = _gameplayState?.State;
        if (gameplay is null || _accountId == 0 || _charId == 0 || string.IsNullOrWhiteSpace(_characterName)) return null;

        var weapon = ResolveEquippedView(EquipSlots.Bitmask[EquipSlot.HandRight], _authAppearance.Weapon);
        var shield = ResolveEquippedView(EquipSlots.Bitmask[EquipSlot.HandLeft], _authAppearance.Shield);
        var headBottom = ResolveEquippedView(EquipSlots.Bitmask[EquipSlot.HeadLow], _authAppearance.HeadBottom);
        var headTop = ResolveEquippedView(EquipSlots.Bitmask[EquipSlot.HeadTop], _authAppearance.HeadTop);
        var headMid = ResolveEquippedView(EquipSlots.Bitmask[EquipSlot.HeadMid], _authAppearance.HeadMid);

        return new PlayerPresence(
            _accountId, _charId, _characterName, _mapName, _x, _y, _direction, _headDirection,
            movement, gameplay.JobClass, _sex, gameplay.BaseLevel, (ushort)CurrentCellDurationMs(),
            _authAppearance.HairStyle, _authAppearance.HairColor, _authAppearance.ClothesColor,
            _authAppearance.BodyStyle, weapon, shield,
            ToUShortAppearance(headBottom, _authAppearance.HeadBottom),
            ToUShortAppearance(headTop, _authAppearance.HeadTop),
            ToUShortAppearance(headMid, _authAppearance.HeadMid),
            _authAppearance.Robe, _authAppearance.Manner, _authAppearance.Karma,
            _authAppearance.Option, _authAppearance.Font);
    }

    private uint ResolveEquippedView(uint equipMask, uint fallback)
    {
        var item = _inventory?.Items.FirstOrDefault(row => (row.Equip & equipMask) != 0);
        return item is not null && GeneratedItems.ById.TryGetValue(item.ItemId, out var definition)
            ? (uint)definition.ClientViewId
            : fallback;
    }

    private static ushort ToUShortAppearance(uint value, ushort fallback) => value <= ushort.MaxValue ? (ushort)value : fallback;

    // Deliberately ignores the caller's cancellationToken for the actual unregister: this method's
    // whole contract is "never leave a ghost presence registered under a wedged
    // ChangingMapOrUnregistering lifecycle" (see the type's remarks on ghost-presence prevention).
    // A warp/script-warp/disconnect can race a session-cancellation exactly here (SendSameServerWarpAsync,
    // ExecuteScriptWarpAsync, and INpcScriptHost.WarpAsync all forward the live session token), and
    // PlayerVisibilityCoordinator.UnregisterAsync's very first await is a cancellable gate wait - an
    // OperationCanceledException thrown there before TryUnregister runs would otherwise strand the
    // presence in the registry/old map's spatial index forever with no lifecycle path back out.
    private async Task LeavePlayerWorldAsync(PlayerSessionLifecycle after, CancellationToken cancellationToken)
    {
        uint actorId = 0;
        Guid? presenceId;
        string? presenceMapId;
        bool wasWorldVisible;
        lock (_playerPresenceGate)
        {
            wasWorldVisible = _playerLifecycle == PlayerSessionLifecycle.WorldVisible;
            if (!wasWorldVisible && after != PlayerSessionLifecycle.Closed)
            {
                return;
            }
            _playerLifecycle = PlayerSessionLifecycle.ChangingMapOrUnregistering;
            actorId = _accountId;
            presenceId = _presenceId;
            presenceMapId = _presenceMapId;
        }

        try
        {
            if (wasWorldVisible)
                await _playerVisibility.UnregisterAsync(actorId, CancellationToken.None);
            // Map changes preserve the logical presence and transfer it separately. Only the end
            // of the connected world session unregisters distributed ownership.
            if (after == PlayerSessionLifecycle.Closed && _distributedWorld is not null && presenceId is { } id && presenceMapId is not null)
                await _distributedWorld.UnregisterPresenceAsync(presenceMapId, _charId, id, CancellationToken.None);
        }
        finally
        {
            lock (_playerPresenceGate)
            {
                _presence = null;
                if (after == PlayerSessionLifecycle.Closed)
                {
                    _presenceId = null;
                    _presenceMapId = null;
                    _pendingTransferId = null;
                    _pendingTransfer = null;
                }
                _playerLifecycle = after;
            }
        }
    }

    private async Task TransferDistributedPresenceAsync(string sourceMap, string destinationMap, ushort x, ushort y, CancellationToken cancellationToken)
    {
        if (_distributedWorld is null) { _presenceMapId = destinationMap; return; }
        var presenceId = _presenceId ?? throw new InvalidOperationException("Map transfer has no logical presence identity.");
        var operation = (WorldMapId.Normalize(sourceMap), WorldMapId.Normalize(destinationMap), x, y);
        if (_pendingTransfer != operation)
        {
            _pendingTransfer = operation;
            _pendingTransferId = Guid.NewGuid();
        }

        var result = await _distributedWorld.TransferPlayerAsync(
            new WorldTransferCommand(
                _pendingTransferId!.Value,
                presenceId,
                _charId,
                operation.Item1,
                operation.Item2,
                x,
                y),
            cancellationToken);
        if (result.Status is not (WorldTransferStatus.Completed or WorldTransferStatus.AlreadyCompleted))
            throw new InvalidOperationException($"World transfer failed status={result.Status} source='{sourceMap}' destination='{destinationMap}'.");

        lock (_playerPresenceGate)
        {
            _presenceMapId = destinationMap;
            _pendingTransferId = null;
            _pendingTransfer = null;
        }
    }

    private PlayerPresence? CurrentPresence()
    {
        lock (_playerPresenceGate) return _presence;
    }

    private void SetCurrentPresence(PlayerPresence presence)
    {
        lock (_playerPresenceGate)
        {
            if (_playerLifecycle == PlayerSessionLifecycle.WorldVisible) _presence = presence;
        }
    }

    private async Task RefreshPresencePublicAppearanceAsync(CancellationToken cancellationToken)
    {
        var current = CurrentPresence();
        if (current is null) return;
        var refreshed = BuildCurrentPresence(current.Movement);
        if (refreshed is null) return;
        SetCurrentPresence(refreshed);
        await _playerVisibility.ReplacePublicStateAsync(refreshed, cancellationToken);
    }

    private async Task StartPresenceMovementAsync(ushort fromX, ushort fromY, ushort destinationX, ushort destinationY, uint startTick, CancellationToken cancellationToken)
    {
        var current = CurrentPresence();
        if (current is null) return;
        var changed = current with
        {
            X = fromX,
            Y = fromY,
            WalkSpeed = (ushort)CurrentCellDurationMs(),
            Movement = new PlayerMovementPresence(fromX, fromY, destinationX, destinationY, startTick),
        };
        SetCurrentPresence(changed);
        await _playerVisibility.UpdateMovementAsync(changed, broadcastMovement: true, cancellationToken);
    }

    private async Task UpdatePresenceForCrossedCellsAsync(
        IReadOnlyList<(ushort X, ushort Y)> crossed,
        (ushort X, ushort Y)? destination,
        CancellationToken cancellationToken)
    {
        foreach (var cell in crossed)
        {
            var current = CurrentPresence();
            if (current is null) return;
            PlayerMovementPresence? movement = null;
            if (destination is not null && current.Movement is { } active)
                movement = active with { DestinationX = destination.Value.X, DestinationY = destination.Value.Y };
            var changed = current with { X = cell.X, Y = cell.Y, Movement = movement };
            SetCurrentPresence(changed);
            await _playerVisibility.UpdateMovementAsync(changed, broadcastMovement: false, cancellationToken);
        }
    }

    private async Task SendVisibleWarpActorsAsync(CancellationToken cancellationToken)
    {
        foreach (var actor in _worldMapRegistry.GetVisibleWarpActors(_mapName, _x, _y))
        {
            if (!_visibleActorIds.TryMarkVisible(actor.ActorId))
            {
                continue;
            }

            var packet = IroWorldActorPackets.BuildWorldActor(actor);
            MapLogger.Info(
                $"[iRO MAP DEBUG] Sending NPC actor id={actor.ActorId} name='{actor.Name}' class={actor.SpriteClass} map='{actor.MapName}' x={actor.X} y={actor.Y}");
            await WriteAsync(packet, cancellationToken);
        }
    }

    // Sends 0x09FF for every alive monster instance in range, reusing the same _visibleActorIds
    // dedup set NPC/warp actors already share (one visibility-tracking collection, matching the
    // one shared WorldActorIdAllocator namespace all actor kinds draw from - MapServerWorld.Build).
    // Null _monsters (test-facing constructor default) means no monster runtime is composed for
    // this session; the method is then a no-op rather than throwing, matching how existing tests
    // exercise NPC/warp/dialogue behavior without ever touching monster state.
    private async Task SendVisibleMonsterActorsAsync(CancellationToken cancellationToken)
    {
        if (_monsters is null) return;

        foreach (var instance in _monsters.GetVisibleInstances(_mapName, _x, _y))
        {
            if (!_visibleActorIds.TryMarkVisible(instance.ActorId))
            {
                continue;
            }

            var mob = instance.Spawn.Mob;
            var position = instance.GetPosition(); // One atomic snapshot - never torn between axes.
            var packet = IroMonsterActorPackets.BuildStandEntry(
                instance.ActorId,
                (ushort)mob.Id,
                (ushort)mob.WalkSpeed,
                mob.Name,
                position.X,
                position.Y,
                direction: 0,
                currentHp: instance.CurrentHp,
                maxHp: mob.MaxHp);
            MapLogger.Info(
                $"[iRO MAP DEBUG] Sending monster actor id={instance.ActorId} name='{mob.Name}' class={mob.Id} map='{instance.Map}' x={position.X} y={position.Y} hp={instance.CurrentHp}/{mob.MaxHp}");
            await WriteAsync(packet, cancellationToken);
        }
    }

    // Called by MapTcpServer's shared MonsterRuntime tick loop for every MonsterMovementChange
    // reported this tick - see MonsterRuntime.ProcessTick's and MonsterMovementChangeKind's own doc
    // comments for why WalkStarted/CellCrossed/WalkFinished are NOT interchangeable here. This is
    // intentionally NOT driven from inside this session's own per-connection loop: monster movement
    // is authoritative/shared world state (MobInstance), and different connected players observing
    // the SAME walk must all originate from that one authoritative source (MapTcpServer's one tick,
    // fanned out to every session), never from N independent per-session simulations of the same
    // monster.
    //
    // Scoped to this session's own _mapName/_visibleActorIds (never global):
    //   - Not yet visible but now within GetVisibleInstances' range of this session's OWN (_x,_y):
    //     a monster can walk INTO a stationary player's visibility, which the existing
    //     SendVisibleMonsterActorsAsync call sites (0x007D map-load, and after the PLAYER's own
    //     movement) never re-check on their own - nothing re-invokes that scan when the player
    //     hasn't moved. Pinned clif_spawn (clif.cpp, dispatches to clif_set_unit_walking when the
    //     unit's ud.walktimer is active, else clif_set_unit_idle) means a monster discovered WHILE
    //     already walking must receive the walking-entry (0x09FD) layout, landing it at its CURRENT
    //     position with its real in-flight destination - never the plain 0x09FF standing entry,
    //     and never a fabricated/fractional subcell value (BuildWalkEntry's own subX/subY=8 is the
    //     only capture-verified value, reused as-is here, not re-derived from elapsed step
    //     progress). A monster discovered while NOT walking still gets the ordinary 0x09FF.
    //   - Already visible: only WalkStarted sends a packet (the capture-verified 0x09FD walk
    //     entry). CellCrossed AND WalkFinished both send NOTHING: pinned unit_walktoxy_nextcell's
    //     ordinary per-cell continuation always passes sendMove=false (unit.cpp:749 vs. the
    //     sendMove=true initial call at unit.cpp:317), and reaching the end of the walkpath
    //     (ud->walkpath.path_pos >= path_len) simply returns false with NO clif_fixpos/stop
    //     notification for either a PC or a MOB (unit.cpp:186-192) - a normal completed walk is
    //     silent on the wire past its own initial 0x09FD. The captured 0x0088 (ZC_STOPMOVE, frame
    //     674 of kill-poring-heal-jobup.pcapng) occurs in a COMBAT sequence together with the
    //     Poring's own attack-back, not as part of an ordinary idle walk's own completion - it is
    //     evidence for the packet's layout and for SOME fix-position/interruption scenario, not
    //     evidence that every natural walk completion sends it. BuildStopMove is kept available
    //     (IroMonsterActorPackets) for a future source-backed movement-interruption/fixpos use case
    //     (e.g. a walk cancelled by combat), just not wired to ordinary WalkFinished here.
    //     Only MobInstance's own authoritative position is updated for CellCrossed/WalkFinished,
    //     matching that the client is expected to already be animating the walk it was told about
    //     once, at WalkStarted, exactly like a real rAthena client does.
    //   - The reverse of the discovery case (a monster walking OUT of visibility while the player
    //     stays still) has no capture-verified packet in this project: ai/iro-2026-wire.md documents
    //     0x0080 ZC_NOTIFY_VANISH type=1 ("died") from a real capture, and separately notes pinned
    //     source's own comment that type=0 means "out of sight" - but that specific type value has
    //     NOT been independently captured/verified here. Sending it on inference alone would
    //     violate this project's "no invented/unverified packet" rule, so it is deliberately NOT
    //     implemented in this slice; a monster that walks out of a stationary player's visibility
    //     range will incorrectly continue to appear to that client. This is a known, documented gap
    //     (see this task's own report), not a silent omission.
    public async Task NotifyMonsterMovedAsync(MonsterMovementChange change, CancellationToken cancellationToken)
    {
        var instance = change.Instance;
        if (!string.Equals(instance.Map, _mapName, StringComparison.OrdinalIgnoreCase)) return;

        var mob = instance.Spawn.Mob;
        var position = instance.GetPosition();

        if (!_visibleActorIds.IsActorVisible(instance.ActorId))
        {
            if (!_visibilityOptions.IsVisible(_mapName, _x, _y, instance.Map, position.X, position.Y)) return;
            if (!_visibleActorIds.TryMarkVisible(instance.ActorId)) return;

            if (instance.IsWalking)
            {
                var walkingDiscoveryDestination = instance.MovementDestination;
                var walkingDiscoveryPacket = IroMonsterActorPackets.BuildWalkEntry(
                    instance.ActorId,
                    (ushort)mob.Id,
                    (ushort)mob.WalkSpeed,
                    mob.Name,
                    position.X,
                    position.Y,
                    walkingDiscoveryDestination.X,
                    walkingDiscoveryDestination.Y,
                    moveStartTime: (uint)Environment.TickCount,
                    currentHp: instance.CurrentHp,
                    maxHp: mob.MaxHp);
                await WriteAsync(walkingDiscoveryPacket, cancellationToken);
                return;
            }

            var standPacket = IroMonsterActorPackets.BuildStandEntry(
                instance.ActorId,
                (ushort)mob.Id,
                (ushort)mob.WalkSpeed,
                mob.Name,
                position.X,
                position.Y,
                direction: 0,
                currentHp: instance.CurrentHp,
                maxHp: mob.MaxHp);
            await WriteAsync(standPacket, cancellationToken);
            return;
        }

        switch (change.Kind)
        {
            case MonsterMovementChangeKind.CellCrossed:
            case MonsterMovementChangeKind.WalkFinished:
                // Pinned unit_walktoxy_nextcell never resends the walk packet for an ordinary
                // per-cell continuation (sendMove=false, unit.cpp:749), and reaching the end of the
                // walkpath sends nothing at all (unit.cpp:186-192, no clif_fixpos) - see this
                // method's own doc comment for why the captured 0x0088 does NOT apply here.
                return;

            case MonsterMovementChangeKind.ChaseInterrupted:
                // Pinned mob_ai_sub_hard's own "target in range -> unit_stop_walking(md,
                // USW_FIXPOS|USW_RELEASE_TARGET)" (unit.cpp:2165-2166): USW_FIXPOS makes pinned
                // unit_stop_walking call clif_fixpos (unit.cpp:1732-1737) - this is the ONE case
                // (combat interruption, never an ordinary WalkFinished) where the capture-verified
                // 0x0088 is sent, at the mob's authoritative CURRENT cell.
                var fixPosPacket = IroMonsterActorPackets.BuildStopMove(instance.ActorId, position.X, position.Y);
                await WriteAsync(fixPosPacket, cancellationToken);
                MapLogger.Info($"[iRO MAP DEBUG] Sent 0x0088 fixpos mobActorId={instance.ActorId} accountId={_accountId} mobPosition=({position.X},{position.Y})");
                return;

            case MonsterMovementChangeKind.WalkStarted:
                var destination = instance.MovementDestination;
                var walkPacket = IroMonsterActorPackets.BuildWalkEntry(
                    instance.ActorId,
                    (ushort)mob.Id,
                    (ushort)mob.WalkSpeed,
                    mob.Name,
                    position.X,
                    position.Y,
                    destination.X,
                    destination.Y,
                    moveStartTime: (uint)Environment.TickCount,
                    currentHp: instance.CurrentHp,
                    maxHp: mob.MaxHp);
                await WriteAsync(walkPacket, cancellationToken);
                MapLogger.Info($"[iRO MAP DEBUG] Sent 0x09FD walk-entry mobActorId={instance.ActorId} accountId={_accountId} from=({position.X},{position.Y}) to=({destination.X},{destination.Y})");
                return;
        }
    }

    // Sends the COMPLETE wire outcome of one mob-on-player basic-attack hit - both the AREA-visible
    // combat action and (victim-only) the resulting SP_HP update, in the exact order and gating
    // pinned rAthena's own real attack path produces, traced (not assumed) from:
    //   - battle.cpp:7399 "clif_damage(*src, *target, tick, wd.amotion, wd.dmotion, wd.damage,
    //     ...)" - the 0x08C8 action is sent IMMEDIATELY/synchronously the instant the attack
    //     executes, to AREA (clif.cpp:5297: "clif_send(&p, sizeof(p), &dst, AREA)" - every session
    //     whose visibility already covers the mob receives it, victim included, regardless of who
    //     else is the target).
    //   - battle.cpp:7437 "battle_delay_damage(tick, wd.amotion, ...)" runs AFTER that clif_damage
    //     call - the actual HP mutation (status_fix_damage -> status_damage -> pc_damage) is
    //     genuinely DEFERRED until the attack-motion delay elapses, not simultaneous with the
    //     action packet. This project does not model that extra delay (a disclosed simplification,
    //     matching this slice's own scope boundary) but the RELATIVE ORDER - action always
    //     observable before the resulting HP change - is preserved by sending both here, action
    //     first, on the same call.
    //   - pc.cpp:9682-9687 "void pc_damage(...) { ... if (hp) clif_updatestatus(*sd,SP_HP); else
    //     return; }" - SP_HP is PLAYER-SELF-ONLY (clif_updatestatus targets exactly one session,
    //     never AREA) and is skipped ENTIRELY when hp==0 - a miss/zero-damage hit never produces an
    //     HP packet, matching this method's own HpChanged guard below exactly.
    public async Task NotifyMonsterAttackOutcomeAsync(MonsterAttackActionOutcome action, CancellationToken cancellationToken)
    {
        if (!string.Equals(action.Map, _mapName, StringComparison.OrdinalIgnoreCase)) return;
        if (!_visibleActorIds.IsActorVisible(action.MobActorId)) return;

        var tick = unchecked((uint)Environment.TickCount);
        var damagePacket = IroMonsterCombatPackets.BuildNotifyAct3(
            action.MobActorId, action.VictimAccountId, tick, action.SrcSpeed, action.DstSpeed, action.Damage, div: 1, actionType: 0);
        await WriteAsync(damagePacket, cancellationToken);
        MapLogger.Info($"[iRO MAP DEBUG] Sent 0x08C8 combat action mobActorId={action.MobActorId} observerAccountId={_accountId} victimAccountId={action.VictimAccountId} damage={action.Damage} isMiss={action.IsMiss}");

        // Self-only, action-then-HP order, HP==0 never sent - see this method's own doc comment
        // for the exact pinned citations (pc.cpp:9682-9687, battle.cpp:7399/7437).
        if (action.VictimAccountId == _accountId && action.HpChanged)
        {
            var hpPacket = IroCharacterProgressionPackets.Parameter(5, action.HpAfter); // SP_HP.
            await WriteAsync(hpPacket, cancellationToken);
            MapLogger.Info($"[iRO MAP DEBUG] Sent 0x00B0 SP_HP accountId={_accountId} hpAfter={action.HpAfter}");
        }
    }

    private async Task PersistPositionIfDirtyAsync(CancellationToken cancellationToken)
    {
        if (!_authenticated || !_positionDirty || string.IsNullOrWhiteSpace(_mapName))
        {
            return;
        }

        // Reconcile against real elapsed walking time first - without this, a disconnect mid-walk
        // would persist the position from the last click/request rather than wherever the character
        // has actually walked to by now (the movement loop advances _x/_y proactively too, but a
        // disconnect can race ahead of its next scheduled wake, so this call is still required here).
        await _movementGate.WaitAsync(cancellationToken);
        try { SyncPositionToNow(); }
        finally { _movementGate.Release(); }

        try
        {
            MapLogger.Info(
                $"[iRO MAP DEBUG] Persisting character position charId={_charId} map='{_mapName}' x={_x} y={_y}");
            if (await _positionPersistence.SavePositionAsync(
                    _accountId,
                    _charId,
                    _mapName,
                    _x,
                    _y,
                    cancellationToken))
            {
                _positionDirty = false;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            MapLogger.Warning($"Character position persistence failed: {ex.Message}");
        }
    }

    private Task SendIroInitialBootstrapAsync(MapAuthOkData authOk, CancellationToken cancellationToken)
    {
        MapLogger.Info("[iRO MAP DEBUG] Sending 0x0B18 len=4");
        MapLogger.Info($"[iRO MAP DEBUG] Sending 0x0283 len=6 accountId={authOk.AccountId}");
        MapLogger.Info("[iRO MAP DEBUG] Sending 0x0ADE len=6 overweightPercent=70");
        MapLogger.Info(
            $"[iRO MAP DEBUG] Sending 0x02EB len=13 map='{authOk.MapName}' x={authOk.X} y={authOk.Y}");
        var skillListPacket = BuildIroSkillInfoListPacket();
        MapLogger.Info($"[iRO MAP DEBUG] Sending 0x0B32 len={skillListPacket.Length}");
        var bootstrap = IroMapEnterPackets.BuildInitialBootstrap(authOk, unchecked((uint)Environment.TickCount));
        var payload = new byte[bootstrap.Length + skillListPacket.Length];
        bootstrap.CopyTo(payload, 0);
        skillListPacket.CopyTo(payload, bootstrap.Length);
        return WriteAsync(payload, cancellationToken);
    }

    // Builds the verified initial map-entry 0x0B32: every effective-tree skill filtered to
    // ClientVisible per CharacterSkillService.CalculateEffectiveState's traced pinned semantics
    // (see ai/map-server.md), then projected through IroSkillInfoEntry.From. Requires an
    // authenticated session (_gameplayState non-null) - only called from
    // SendIroInitialBootstrapAsync, itself only reachable after CompleteIroAuthenticationAsync
    // succeeds.
    private byte[] BuildIroSkillInfoListPacket()
    {
        var gameplayState = _gameplayState ?? throw new InvalidOperationException("Gameplay state must be loaded before building the skill list.");
        var tree = Athena.Net.MapServer.Generated.Skills.GeneratedSkillTreeRegistry.Get(gameplayState.State.JobClass);
        var effective = CharacterSkillService.CalculateEffectiveState(gameplayState.State, gameplayState.Skills, tree, out var inconsistentSkillIds);
        foreach (var skillId in inconsistentSkillIds)
        {
            MapLogger.Warning(
                $"[iRO MAP DEBUG] Persisted skill inconsistency accountId={_accountId} charId={_charId} " +
                $"skillId={skillId}: persisted level exceeds effective tree MaxLevel; clamped for display.");
        }
        var entries = new List<IroSkillInfoEntry>();
        foreach (var state in effective)
        {
            if (!state.ClientVisible) continue;
            var canonical = Athena.Net.MapServer.Generated.Skills.GeneratedSkillRegistry.GetById(state.SkillId);
            entries.Add(IroSkillInfoEntry.From(state, canonical, gameplayState.Skills));
        }
        return IroSkillInfoListPackets.Build(entries);
    }

    private async Task HandleEnterAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (_authRequested)
        {
            return;
        }

        _accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4));
        _charId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(6, 4));
        _loginId1 = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(10, 4));
        _sex = packet[18];

        if (_accountId == 0)
        {
            await SendRefuseEnterAsync(2, cancellationToken);
            return;
        }

        if (_charId == 0)
        {
            await SendRefuseEnterAsync(3, cancellationToken);
            return;
        }

        if (_sex != 0 && _sex != 1)
        {
            await SendRefuseEnterAsync(6, cancellationToken);
            return;
        }

        var endpoint = _client.Client.RemoteEndPoint as IPEndPoint;
        var clientIp = endpoint?.Address ?? IPAddress.Loopback;

        if (!_charConnector.TrySendAuthRequest(this, _accountId, _charId, _loginId1, _sex, clientIp))
        {
            await SendRefuseEnterAsync(0, cancellationToken);
            MapLogger.Warning("Auth request to char server failed. Disconnecting map client.");
            RequestClose();
            return;
        }

        _authRequested = true;
    }

    private Task SendAcceptEnterAsync(MapAuthOkData authOk, CancellationToken cancellationToken)
    {
        var buffer = new byte[13];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.ZcAcceptEnter);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2, 4), (uint)Environment.TickCount);
        WritePackedPosition(buffer.AsSpan(6, 3), authOk.X, authOk.Y, authOk.Direction);
        buffer[9] = 5;
        buffer[10] = 5;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(11, 2), authOk.Font);
        return WriteAsync(buffer, cancellationToken);
    }

    private Task SendNotifyActorInitAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.ZcNotifyActorInit);
        return WriteAsync(buffer, cancellationToken);
    }

    private Task SendPingLiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.ZcPingLive);
        return WriteAsync(buffer, cancellationToken);
    }

    private Task SendRefuseEnterAsync(byte errorCode, CancellationToken cancellationToken)
    {
        var buffer = new byte[3];
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(0, 2), PacketConstants.ZcRefuseEnter);
        buffer[2] = errorCode;
        return WriteAsync(buffer, cancellationToken);
    }

    internal static async Task<byte[]> ReadNextPacketAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = await ReadExactAsync(stream, 2, cancellationToken);
        if (header.Length == 0)
        {
            return Array.Empty<byte>();
        }

        var packetType = BinaryPrimitives.ReadInt16LittleEndian(header);
        return await ReadPacketAsync(stream, packetType, header, cancellationToken);
    }

    private static async Task<byte[]> ReadPacketAsync(
        Stream stream,
        short packetType,
        byte[] header,
        CancellationToken cancellationToken)
    {
        if (!PacketLengths.TryGetValue(packetType, out var length))
        {
            LogUnsupportedPacket(packetType, header);
            return Array.Empty<byte>();
        }

        var payloadLength = length - 2;
        var payload = payloadLength == 0
            ? Array.Empty<byte>()
            : await ReadExactAsync(stream, payloadLength, cancellationToken);
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

        // TEMPORARY framing-investigation instrumentation, scoped to only the equip/unequip
        // opcodes under investigation - never logs auth/session traffic. Remove once the
        // 0x0998 length question is resolved.
        if (packetType is PacketConstants.IroCzReqWearEquip or PacketConstants.IroCzReqTakeoffEquip)
        {
            MapLogger.Info(
                $"[iRO MAP DEBUG][FRAMING] Consumed packetType=0x{packetType:X4} declaredLength={length} bytes={Convert.ToHexString(packet)}");
        }

        return packet;
    }

    private static async Task<byte[]> ReadExactAsync(
        Stream stream,
        int length,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            int bytes;
            try
            {
                bytes = await stream.ReadAsync(buffer.AsMemory(read, length - read), cancellationToken);
            }
            catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset })
            {
                // An abrupt client-side close (TcpClient.Close()/Dispose() without a graceful
                // shutdown, e.g. every test in this project's socket-based test suites) can
                // surface as a TCP RST rather than a clean FIN - .NET reports that as a thrown
                // ConnectionReset SocketException instead of ReadAsync returning 0. Both mean
                // exactly the same thing to this session: the peer is gone. Treat it identically
                // to every other disconnect path in this method (empty result -> RunAsync's
                // packet.Length == 0 check -> ordinary session shutdown), rather than letting a
                // routine disconnect surface as an unhandled exception out of RunAsync.
                return Array.Empty<byte>();
            }

            if (bytes == 0)
            {
                return Array.Empty<byte>();
            }

            read += bytes;
        }

        return buffer;
    }

    private static void LogUnsupportedPacket(short packetType, ReadOnlySpan<byte> packet)
    {
        MapLogger.Warning(
            $"[iRO MAP DEBUG] Unsupported map client packet=0x{packetType:X4} len={packet.Length}");
    }

    public Task PlayerEnteredViewAsync(PlayerPresence presence, PlayerEntryKind kind, CancellationToken cancellationToken)
    {
        if (presence.ActorId == _accountId || !_visibleActorIds.TryMarkVisible(presence.ActorId)) return Task.CompletedTask;
        var packet = kind == PlayerEntryKind.NewlySpawned
            ? IroPlayerActorPackets.BuildSpawnEntry(presence)
            : presence.Movement is null
                ? IroPlayerActorPackets.BuildStandEntry(presence)
                : IroPlayerActorPackets.BuildWalkEntry(presence);
        return WriteAsync(packet, cancellationToken);
    }

    public Task PlayerMovementChangedAsync(PlayerPresence presence, CancellationToken cancellationToken)
    {
        if (presence.ActorId == _accountId || !_visibleActorIds.IsActorVisible(presence.ActorId) || presence.Movement is null) return Task.CompletedTask;
        return WriteAsync(IroPlayerActorPackets.BuildWalkEntry(presence), cancellationToken);
    }

    public Task PlayerLookChangedAsync(PlayerPresence presence, CancellationToken cancellationToken)
    {
        if (presence.ActorId == _accountId || !_visibleActorIds.IsActorVisible(presence.ActorId)) return Task.CompletedTask;
        return WriteAsync(IroPlayerActorPackets.BuildDirection(presence.ActorId, presence.HeadDirection, presence.Direction), cancellationToken);
    }

    public Task PlayerLeftViewAsync(uint actorId, CancellationToken cancellationToken)
    {
        if (actorId == _accountId || !_visibleActorIds.TryMarkNotVisible(actorId)) return Task.CompletedTask;
        return WriteAsync(IroPlayerActorPackets.BuildVanish(actorId), cancellationToken);
    }

    public void ForgetPlayer(uint actorId) => _visibleActorIds.MarkNotVisible(actorId);


    private async Task WriteAsync(byte[] payload, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _stream.WriteAsync(payload, cancellationToken);
            // Only reached if the write above did not throw - see this field's own doc comment.
            var packetId = payload.Length >= 2 ? BinaryPrimitives.ReadInt16LittleEndian(payload) : (short)-1;
            _lastPacketWrittenDescription = $"0x{packetId:X4} len={payload.Length}";
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void RequestClose()
    {
        if (!_sessionCancellation.IsCancellationRequested)
        {
            _sessionCancellation.Cancel();
        }
    }

    private static void WritePackedPosition(Span<byte> buffer, ushort x, ushort y, byte direction)
    {
        IroCoordinatePacking.WritePosition(buffer, x, y, direction);
    }

    // Test-only default for the internal test-facing constructor (see its
    // inventoryListPersistence parameter). Always reports a successful read with an empty
    // inventory (confirmed unarmed), so tests that never pass inventoryListPersistence
    // explicitly still authenticate successfully.
    private sealed class AlwaysEmptyInventoryListPersistence : ICharacterInventoryListPersistence
    {
        internal static readonly AlwaysEmptyInventoryListPersistence Instance = new();
        private static readonly CharacterInventoryReadResult Empty =
            CharacterInventoryReadResult.Success(new CharacterInventorySnapshot(Array.Empty<CharacterInventoryItem>()));

        public Task<CharacterInventoryReadResult> GetInventoryAsync(uint accountId, uint characterId, CancellationToken cancellationToken)
            => Task.FromResult(Empty);

        public Task<bool> SetItemEquipAsync(uint accountId, uint characterId, uint slotIndex, uint equip, CancellationToken cancellationToken)
            => Task.FromResult(false);
    }

    // Test-only default for the internal test-facing constructor (see its skillPersistence
    // parameter). Always reports a successful read with no learned skills, so tests that never
    // pass skillPersistence explicitly still authenticate successfully.
    private sealed class AlwaysEmptySkillPersistence : ICharacterSkillPersistence
    {
        internal static readonly AlwaysEmptySkillPersistence Instance = new();
        private static readonly CharacterSkillReadResult Empty = CharacterSkillReadResult.Success(CharacterSkillSnapshot.Empty);

        public Task<CharacterSkillReadResult> GetSkillsAsync(uint accountId, uint characterId, CancellationToken cancellationToken)
            => Task.FromResult(Empty);

        public Task<CharacterSkillLearnResult?> LearnSkillAsync(uint accountId, CharacterGameplayState expectedGameplayState, ushort skillId, byte expectedCurrentLevel, CancellationToken cancellationToken)
            => Task.FromResult<CharacterSkillLearnResult?>(null);
    }
}
