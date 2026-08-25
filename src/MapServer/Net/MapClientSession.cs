using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Logging;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.GeneratedScripts;

namespace Athena.Net.MapServer.Net;

public sealed class MapClientSession : IDisposable, INpcScriptHost
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
    };

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly CharServerConnector _charConnector;
    private readonly ICharacterPositionPersistence _positionPersistence;
    private readonly ICharacterQuestPersistence _questPersistence;
    private readonly ICharacterGameplayStatePersistence _gameplayStatePersistence;
    private readonly WorldMapRegistry _worldMapRegistry;
    // Null when no MapServerWorld was supplied (test-facing constructor default). No packet
    // handler reads this yet - see MonsterRegistry/MonsterCombatCoordinator doc comments for why
    // no live attack path exists - but it is threaded through explicitly now so a future handler
    // does not need another constructor-plumbing change.
    private readonly MonsterRegistry? _monsters;
    private readonly IMovementPathProvider _movementPathProvider;
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
    // Single-slot wake signal (same semantics as _statusExpirationSignal above): a new/retargeted
    // walk may need the loop to wake earlier than its current sleep, or wake it from indefinite
    // waiting when it starts moving from a standstill.
    private readonly SemaphoreSlim _movementSignal = new(0, 1);
    private Task? _movementLoop;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _sessionCancellation = new();
    private readonly HashSet<uint> _visibleActorIds = new();
    private ScriptExecutionSession? _scriptExecutionSession;
    private Task? _generatedScriptTask;
    private string? _generatedScriptEntityId;
    private uint _generatedScriptActorId;
    private GeneratedContinuation? _generatedContinuation;
    private TaskCompletionSource _generatedSuspended = NewSignal();
    private uint _accountId;
    private uint _charId;
    private uint _loginId1;
    private string _mapName = string.Empty;
    private ushort _x;
    private ushort _y;
    private byte _sex;
    private bool _authRequested;
    private bool _iroAuthRequested;
    private bool _authenticated;
    private bool _positionDirty;
    private int _disposed;
    private CharacterGameplayStateSession? _gameplayState;
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
    public MapClientSession(int sessionId, TcpClient client, CharServerConnector charConnector, MapServerWorld world)
        : this(sessionId, client, charConnector, world.Maps, monsters: world.Monsters)
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
        IMovementPathProvider? movementPathProvider = null)
    {
        SessionId = sessionId;
        _client = client;
        _stream = client.GetStream();
        _charConnector = charConnector;
        _positionPersistence = positionPersistence ?? charConnector;
        _questPersistence = questPersistence ?? charConnector;
        _gameplayStatePersistence = gameplayStatePersistence ?? charConnector;
        _worldMapRegistry = worldMapRegistry;
        _monsters = monsters;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _movementPathProvider = movementPathProvider ?? new UnverifiedGridLineMovementPathProvider();
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
        IMovementPathProvider? movementPathProvider = null)
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
            movementPathProvider)
    {
        _iroAuthRequested = iroAuthenticated;
        _authRequested = iroAuthenticated;
        _mapName = mapName;
        _x = x;
        _y = y;
        _authenticated = iroAuthenticated;
        _accountId = accountId;
        _charId = charId;
    }

    public int SessionId { get; }

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
    internal CharacterStatusEffectState StatusEffects => _statusEffects;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _sessionCancellation.Token);
        var sessionToken = linkedCancellation.Token;

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
            await PersistPositionIfDirtyAsync(CancellationToken.None);
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
        _gameplayState = new CharacterGameplayStateSession(authOk.AccountId, state, _gameplayStatePersistence);
        _authenticated = true; _positionDirty = false;
        MapLogger.Info($"[iRO MAP DEBUG] 0x0C1F MapAuthNode authentication succeeded accountId={authOk.AccountId} charId={authOk.CharId} sessionMatch=true gameplayStateVersion={state.Version}");
        _statusExpirationLoop = RunStatusExpirationLoopAsync(_sessionCancellation.Token);
        _movementLoop = RunMovementLoopAsync(_sessionCancellation.Token);
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
            arrival = movement.IsMoving ? null : _pendingArrival; // Only relevant once the walk actually finished.
            mapAtAdvance = _mapName;
        }
        finally { _movementGate.Release(); }

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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _sessionCancellation.Cancel();
        // Unblock a pending WaitAsync so the loop observes cancellation promptly; a wake may
        // already be pending (SemaphoreFullException), which is harmless here too.
        try { _statusExpirationSignal.Release(); } catch (SemaphoreFullException) { }
        try { _movementSignal.Release(); } catch (SemaphoreFullException) { }
        _generatedContinuation?.Completion.TrySetCanceled();
        _stream.Dispose();
        _writeLock.Dispose();
        _sessionCancellation.Dispose();
        _statusExpirationSignal.Dispose();
        _movementSignal.Dispose();
        _movementGate.Dispose();
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
                    _visibleActorIds.Clear();
                    await SendVisibleWarpActorsAsync(cancellationToken);
                    foreach (var navigation in _worldMapRegistry.GetNavigationAt(_mapName, _x, _y))
                        await WriteAsync(IroNpcDialoguePackets.BuildNavigateTo(navigation.DestinationMap, navigation.DestinationX, navigation.DestinationY), cancellationToken);
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
                if (_visibleActorIds.Contains(requestedActorId) && _worldMapRegistry.TryGetActorName(requestedActorId, _mapName, out var actorName))
                {
                    MapLogger.Info($"[iRO MAP DEBUG] Sending 0x0ADF NPC name actorId={requestedActorId} name='{actorName}'");
                    await WriteAsync(IroWorldActorPackets.BuildNpcName(requestedActorId, actorName), cancellationToken);
                }
                break;
            case PacketConstants.IroCzChangeDirection when _iroAuthRequested:
                if (IroChangeDirectionPacket.TryParse(packet, out var direction))
                {
                    MapLogger.Info($"[iRO MAP DEBUG] Received stock iRO change-direction packet=0x{packetType:X4} headDirection={direction.HeadDirection} bodyDirection={direction.BodyDirection}");
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

    private async Task HandleIroMovementAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!IroMovementPackets.TryParseRequest(packet, out var request))
        {
            RequestClose();
            return;
        }

        // Advance any walk already in progress to the character's ACTUAL current cell before doing
        // anything else - this is the fix for the diagnosed bug: a second movement request must
        // retarget from wherever the character has really walked to by now, not from a previous
        // request's destination that the client may not have visually reached yet (see
        // CharacterMovementState.StartWalk's doc comment for the exact rAthena unit_walktoxy
        // mid-walk-retarget citation this mirrors).
        SyncPositionToNow();
        var fromX = _x;
        var fromY = _y;
        MapLogger.Info(
            $"[iRO MAP DEBUG] Movement request from=({fromX},{fromY}) target=({request.TargetX},{request.TargetY})");

        var intersectsWarp = _worldMapRegistry.TryFindFirstWarpAlongRoute(
            _mapName,
            fromX,
            fromY,
            request.TargetX,
            request.TargetY,
            out var intersection);
        ScriptTouchIntersection scriptIntersection = default;
        var intersectsScript = !HasActiveScript && _worldMapRegistry.TryFindFirstScriptTouchEnterAlongRoute(
            _mapName, fromX, fromY, request.TargetX, request.TargetY, out scriptIntersection);
        if (intersectsWarp && intersectsScript && Distance(fromX, fromY, scriptIntersection.X, scriptIntersection.Y) < Distance(fromX, fromY, intersection.X, intersection.Y))
            intersectsWarp = false;
        else if (intersectsWarp)
            intersectsScript = false;
        var movementTargetX = intersectsWarp ? intersection.X : intersectsScript ? scriptIntersection.X : request.TargetX;
        var movementTargetY = intersectsWarp ? intersection.Y : intersectsScript ? scriptIntersection.Y : request.TargetY;

        // Start (or retarget) the timed walk from the current cell toward movementTarget, rather
        // than jumping _x/_y there immediately. The wire response below still reports the full
        // destination (unchanged, capture-proven 0x0087 semantics) - only the SERVER-authoritative
        // position now advances gradually, matching rAthena. Computed before the response is sent so
        // that, by the time a caller observes the response, the new walk is already authoritative -
        // avoiding a race where a caller could read stale position between the write and this update.
        var path = _movementPathProvider.ComputePath(_mapName, fromX, fromY, movementTargetX, movementTargetY);
        var now = _timeProvider.GetUtcNow();

        // Warp/OnTouch must fire only when the destination cell is actually reached over real
        // elapsed time (RunMovementLoopAsync/ProcessDueMovementAsync), matching rAthena's per-cell
        // npc_touch_area_allnpc/npc_touch_areanpc2 checks inside unit_walktoxy_timer - not the moment
        // the client clicks. Attach the pending action here (under the gate, alongside StartWalk) so
        // the movement loop can execute it exactly once, at true arrival.
        await _movementGate.WaitAsync(cancellationToken);
        try
        {
            EnsureMovementState().StartWalk(path, CurrentCellDurationMs(), now);
            _pendingArrival = intersectsWarp
                ? new PendingWarpArrival(intersection.Warp)
                : intersectsScript
                    ? new PendingScriptTouchArrival(scriptIntersection.Binding.Entity, scriptIntersection.Binding.Actor.ActorId, scriptIntersection.Binding.Script)
                    : null;
        }
        finally { _movementGate.Release(); }
        _positionDirty = true;
        try { _movementSignal.Release(); } catch (SemaphoreFullException) { }

        var response = IroMovementPackets.BuildResponse(
            unchecked((uint)Environment.TickCount),
            fromX,
            fromY,
            movementTargetX,
            movementTargetY);
        MapLogger.Info(
            $"[iRO MAP DEBUG] Sending 0x0087 len=12 from=({fromX},{fromY}) to=({movementTargetX},{movementTargetY})");
        await WriteAsync(response, cancellationToken);

        if (intersectsWarp)
        {
            MapLogger.Info(
                $"[iRO MAP DEBUG] Movement path intersects warp map='{_mapName}' at=({intersection.X},{intersection.Y}) requestedTarget=({request.TargetX},{request.TargetY}) (deferred to actual arrival)");
        }
        else if (intersectsScript)
        {
            MapLogger.Info($"[iRO MAP DEBUG] Movement path intersects script trigger entity='{scriptIntersection.Binding.Entity.Id}' map='{_mapName}' at=({scriptIntersection.X},{scriptIntersection.Y}) (deferred to actual arrival)");
        }
        else
        {
            await SendVisibleWarpActorsAsync(cancellationToken);
        }
    }

    private static long Distance(ushort x1, ushort y1, ushort x2, ushort y2)
    {
        var dx = (long)x2 - x1; var dy = (long)y2 - y1; return dx * dx + dy * dy;
    }

    private async Task SendSameServerWarpAsync(WarpDefinition warp, CancellationToken cancellationToken)
    {
        _scriptExecutionSession = null;
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
                MapLogger.Info($"[iRO MAP DEBUG] Warp triggered map='{_mapName}' at=({_x},{_y}) -> map='{warpAction.Map}' x={warpAction.X} y={warpAction.Y}");
                TeleportTo(warpAction.Map, warpAction.X, warpAction.Y);
            }
        }

        var response = IroMapTransitionPackets.BuildSameServerMapChange(_mapName, _x, _y);
        MapLogger.Info(
            $"[iRO MAP DEBUG] Sending 0x0091 len={response.Length} map='{IroMapTransitionPackets.NormalizeWireMapName(_mapName)}' x={_x} y={_y}");
        await WriteAsync(response, cancellationToken);
        await PersistPositionIfDirtyAsync(cancellationToken);
    }

    private async Task HandleNpcInteractionAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!IroNpcDialoguePackets.TryParseInteraction(packet, out var actorId) || HasActiveScript || !_visibleActorIds.Contains(actorId) || !_worldMapRegistry.TryGetInteraction(actorId, _mapName, out var entity, out var script))
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
        TeleportTo(map, warp.X, warp.Y); _positionDirty = true; _visibleActorIds.Clear();
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

    async Task INpcScriptHost.NextAsync(uint actorId, CancellationToken cancellationToken)
    {
        var continuation = new GeneratedContinuation(GeneratedContinuationKind.Next, NewContinuation());
        _generatedContinuation = continuation;
        await WriteAsync(IroNpcDialoguePackets.BuildNext(actorId), cancellationToken);
        _generatedSuspended.TrySetResult();
        await continuation.Completion.Task.WaitAsync(cancellationToken);
    }

    async Task<int> INpcScriptHost.SelectAsync(uint actorId, IReadOnlyList<string> options, CancellationToken cancellationToken)
    {
        var continuation = new GeneratedContinuation(GeneratedContinuationKind.Selection, NewContinuation());
        _generatedContinuation = continuation;
        await WriteAsync(IroNpcDialoguePackets.BuildMenu(actorId, options), cancellationToken);
        _generatedSuspended.TrySetResult();
        return await continuation.Completion.Task.WaitAsync(cancellationToken);
    }

    Task INpcScriptHost.CloseAsync(uint actorId, CancellationToken cancellationToken) =>
        WriteAsync(IroNpcDialoguePackets.BuildClose(actorId), cancellationToken);

    async Task INpcScriptHost.Close2Async(uint actorId, CancellationToken cancellationToken)
    {
        var continuation = new GeneratedContinuation(GeneratedContinuationKind.Close2, NewContinuation());
        _generatedContinuation = continuation;
        await WriteAsync(IroNpcDialoguePackets.BuildClose(actorId), cancellationToken);
        _generatedSuspended.TrySetResult();
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
        TeleportTo(map, x, y); _positionDirty = true; _visibleActorIds.Clear();
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
        var result = await new CharacterProgressionService(state).AddExperienceAsync(baseExperience, jobExperience, cancellationToken)
            ?? throw new InvalidOperationException("Character progression persistence failed.");
        foreach (var packet in IroCharacterProgressionPackets.Build(result, baseExperience > 0, jobExperience > 0)) await WriteAsync(packet, cancellationToken);
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

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<int> NewContinuation() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed record GeneratedContinuation(GeneratedContinuationKind Kind, TaskCompletionSource<int> Completion);
    private enum InstructionExecutionResult { Continue, Stop }
    private enum GeneratedContinuationKind { Next, Selection, Close2 }

    private async Task SendVisibleWarpActorsAsync(CancellationToken cancellationToken)
    {
        foreach (var actor in _worldMapRegistry.GetVisibleWarpActors(_mapName, _x, _y))
        {
            if (!_visibleActorIds.Add(actor.ActorId))
            {
                continue;
            }

            var packet = IroWorldActorPackets.BuildWorldActor(actor);
            MapLogger.Info(
                $"[iRO MAP DEBUG] Sending NPC actor id={actor.ActorId} name='{actor.Name}' class={actor.SpriteClass} map='{actor.MapName}' x={actor.X} y={actor.Y}");
            await WriteAsync(packet, cancellationToken);
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
        var payload = IroMapEnterPackets.BuildInitialBootstrap(
            authOk,
            unchecked((uint)Environment.TickCount));
        return WriteAsync(payload, cancellationToken);
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
            var bytes = await stream.ReadAsync(buffer.AsMemory(read, length - read), cancellationToken);
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

    private void RequestClose()
    {
        if (!_sessionCancellation.IsCancellationRequested)
        {
            _sessionCancellation.Cancel();
        }
    }

    private static void WritePackedPosition(Span<byte> buffer, ushort x, ushort y, byte direction)
    {
        buffer[0] = (byte)(x >> 2);
        buffer[1] = (byte)((x << 6) | ((y >> 4) & 0x3f));
        buffer[2] = (byte)((y << 4) | (direction & 0x0f));
    }
}
