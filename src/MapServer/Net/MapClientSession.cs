using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Logging;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Net;

public sealed class MapClientSession : IDisposable
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
    private readonly WorldMapRegistry _worldMapRegistry;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _sessionCancellation = new();
    private readonly HashSet<uint> _visibleActorIds = new();
    private ScriptExecutionSession? _scriptExecutionSession;
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

    public MapClientSession(int sessionId, TcpClient client, CharServerConnector charConnector)
        : this(sessionId, client, charConnector, WorldMapRegistry.Tutorial)
    {
    }

    private MapClientSession(
        int sessionId,
        TcpClient client,
        CharServerConnector charConnector,
        WorldMapRegistry worldMapRegistry,
        ICharacterPositionPersistence? positionPersistence = null,
        ICharacterQuestPersistence? questPersistence = null)
    {
        SessionId = sessionId;
        _client = client;
        _stream = client.GetStream();
        _charConnector = charConnector;
        _positionPersistence = positionPersistence ?? charConnector;
        _questPersistence = questPersistence ?? charConnector;
        _worldMapRegistry = worldMapRegistry;
    }

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
        uint charId = 0)
        : this(
            sessionId,
            client,
            charConnector,
            worldMapRegistry ?? WorldMapRegistry.Tutorial,
            positionPersistence,
            questPersistence)
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
    internal ushort CurrentX => _x;
    internal ushort CurrentY => _y;
    internal ScriptExecutionState? ActiveScriptState => _scriptExecutionSession?.State;

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
            _authenticated = true;
            _positionDirty = false;
            MapLogger.Info(
                $"[iRO MAP DEBUG] 0x0C1F MapAuthNode authentication succeeded accountId={authOk.AccountId} charId={authOk.CharId} sessionMatch=true");
            _ = SendIroInitialBootstrapAsync(authOk, CancellationToken.None);
            return;
        }

        _ = SendAcceptEnterAsync(authOk, CancellationToken.None);
        _ = SendNotifyActorInitAsync(CancellationToken.None);
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
        _stream.Dispose();
        _writeLock.Dispose();
        _sessionCancellation.Dispose();
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
                if (IroNpcDialoguePackets.TryParseClose(packet, out var closeActorId) && _scriptExecutionSession?.ActorId == closeActorId)
                    _scriptExecutionSession = null;
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

    private async Task HandleIroMovementAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!IroMovementPackets.TryParseRequest(packet, out var request))
        {
            RequestClose();
            return;
        }

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
        var intersectsScript = _scriptExecutionSession is null && _worldMapRegistry.TryFindFirstScriptTouchEnterAlongRoute(
            _mapName, fromX, fromY, request.TargetX, request.TargetY, out scriptIntersection);
        if (intersectsWarp && intersectsScript && Distance(fromX, fromY, scriptIntersection.X, scriptIntersection.Y) < Distance(fromX, fromY, intersection.X, intersection.Y))
            intersectsWarp = false;
        else if (intersectsWarp)
            intersectsScript = false;
        var movementTargetX = intersectsWarp ? intersection.X : intersectsScript ? scriptIntersection.X : request.TargetX;
        var movementTargetY = intersectsWarp ? intersection.Y : intersectsScript ? scriptIntersection.Y : request.TargetY;

        var response = IroMovementPackets.BuildResponse(
            unchecked((uint)Environment.TickCount),
            fromX,
            fromY,
            movementTargetX,
            movementTargetY);
        MapLogger.Info(
            $"[iRO MAP DEBUG] Sending 0x0087 len=12 from=({fromX},{fromY}) to=({movementTargetX},{movementTargetY})");
        await WriteAsync(response, cancellationToken);

        _x = movementTargetX;
        _y = movementTargetY;
        _positionDirty = true;

        if (intersectsWarp)
        {
            MapLogger.Info(
                $"[iRO MAP DEBUG] Movement path intersects warp map='{_mapName}' at=({intersection.X},{intersection.Y}) requestedTarget=({request.TargetX},{request.TargetY})");
            await SendSameServerWarpAsync(intersection.Warp, cancellationToken);
        }
        else if (intersectsScript)
        {
            await SendVisibleWarpActorsAsync(cancellationToken);
            MapLogger.Info($"[iRO MAP DEBUG] Movement entered script trigger entity='{scriptIntersection.Binding.Entity.Id}' map='{_mapName}' at=({scriptIntersection.X},{scriptIntersection.Y})");
            await StartScriptAsync(scriptIntersection.Binding.Entity, scriptIntersection.Binding.Actor.ActorId, scriptIntersection.Binding.Script, "OnTouch", cancellationToken);
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
                _mapName = warpAction.Map;
                _x = warpAction.X;
                _y = warpAction.Y;
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
        if (!IroNpcDialoguePackets.TryParseInteraction(packet, out var actorId) || _scriptExecutionSession is not null || !_visibleActorIds.Contains(actorId) || !_worldMapRegistry.TryGetInteraction(actorId, _mapName, out var entity, out var script) || script.Instructions is not { Count: > 0 })
        {
            MapLogger.Info($"[iRO MAP DEBUG] NPC interaction rejected actorId={actorId}");
            return;
        }

        MapLogger.Info($"[iRO MAP DEBUG] NPC interaction actorId={actorId} entity='{entity.Id}'");
        await StartScriptAsync(entity, actorId, script, "OnClick", cancellationToken);
    }

    private async Task StartScriptAsync(WorldEntityDefinition entity, uint actorId, ScriptBehaviorDefinition script, string trigger, CancellationToken cancellationToken)
    {
        if (_scriptExecutionSession is not null || entity.Actor is null || script.Instructions is not { Count: > 0 }) return;
        _scriptExecutionSession = new ScriptExecutionSession(entity.Id, actorId, entity.Actor.Name, script.BaseNpcName, entity.Actor.Map, script.Instructions);
        MapLogger.Info($"[iRO MAP DEBUG] Script start entity='{entity.Id}' trigger={trigger}");
        await SendScriptOutputAsync(_scriptExecutionSession.Run(), cancellationToken);
    }

    private async Task HandleNpcNextAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!IroNpcDialoguePackets.TryParseNext(packet, out var actorId) || _scriptExecutionSession is null) return;
        var output = _scriptExecutionSession.ResumeNext(actorId);
        if (output.Count == 0) return;
        MapLogger.Info($"[iRO MAP DEBUG] Script resumed reason=Next entity='{_scriptExecutionSession.EntityId}'");
        await SendScriptOutputAsync(output, cancellationToken);
    }

    private async Task HandleNpcSelectionAsync(byte[] packet, CancellationToken cancellationToken)
    {
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
            switch (instruction)
            {
                case MessageInstruction message:
                    MapLogger.Info($"[iRO MAP DEBUG] Script message entity='{execution.EntityId}' actorId={execution.ActorId}");
                    await WriteAsync(IroNpcDialoguePackets.BuildMessage(execution.ActorId, message.Text), cancellationToken);
                    break;
                case NextInstruction:
                    await WriteAsync(IroNpcDialoguePackets.BuildNext(execution.ActorId), cancellationToken);
                    MapLogger.Info($"[iRO MAP DEBUG] Script suspended reason=Next entity='{execution.EntityId}'");
                    break;
                case SelectInstruction select:
                    MapLogger.Info($"[iRO MAP DEBUG] Script selection shown entity='{execution.EntityId}' options={select.Options.Count}");
                    await WriteAsync(IroNpcDialoguePackets.BuildMenu(execution.ActorId, select.Options.Select(option => option.Text).ToArray()), cancellationToken);
                    MapLogger.Info($"[iRO MAP DEBUG] Script suspended reason=Selection entity='{execution.EntityId}'");
                    break;
                case CloseInstruction:
                    await WriteAsync(IroNpcDialoguePackets.BuildClose(execution.ActorId), cancellationToken);
                    MapLogger.Info($"[iRO MAP DEBUG] Script closed entity='{execution.EntityId}'");
                    _scriptExecutionSession = null;
                    break;
                case Close2Instruction:
                    await WriteAsync(IroNpcDialoguePackets.BuildClose(execution.ActorId), cancellationToken);
                    MapLogger.Info($"[iRO MAP DEBUG] Script dialogue closed; execution continues entity='{execution.EntityId}'");
                    break;
                case AssignmentInstruction assignment:
                    execution.Assign(assignment.Variable, assignment.Value);
                    break;
                case WarpInstruction warp:
                    await ExecuteScriptWarpAsync(execution, warp, cancellationToken);
                    break;
                case SavePointInstruction savePoint:
                    if (!await SavePointAsync(execution.Evaluate(savePoint.Map), savePoint.X, savePoint.Y, cancellationToken))
                    {
                        MapLogger.Warning($"SavePoint persistence aborted script entity='{execution.EntityId}' charId={_charId}.");
                        _scriptExecutionSession = null;
                        return;
                    }
                    break;
                case SetQuestInstruction setQuest:
                    if (!await SetQuestAsync(setQuest.QuestId, cancellationToken))
                    {
                        await AbortScriptForPersistenceFailureAsync(execution, setQuest.QuestId, cancellationToken);
                        return;
                    }
                    break;
                case CompleteQuestInstruction completeQuest:
                    if (!await CompleteQuestAsync(completeQuest.QuestId, cancellationToken))
                    {
                        await AbortScriptForPersistenceFailureAsync(execution, completeQuest.QuestId, cancellationToken);
                        return;
                    }
                    break;
                case IfQuestStateInstruction check:
                    if (check.QuestId == 0) return;
                    var state = await _questPersistence.GetQuestStateAsync(_accountId, _charId, check.QuestId, cancellationToken);
                    if (state is null)
                    {
                        await AbortScriptForPersistenceFailureAsync(execution, check.QuestId, cancellationToken);
                        return;
                    }
                    await SendScriptOutputAsync(execution.ResumeQuestState(execution.ActorId, state.Value), cancellationToken);
                    break;
            }
        }
        if (execution.State == ScriptExecutionState.Closed) _scriptExecutionSession = null;
    }

    private async Task AbortScriptForPersistenceFailureAsync(
        ScriptExecutionSession execution,
        uint questId,
        CancellationToken cancellationToken)
    {
        MapLogger.Warning(
            $"Quest persistence aborted script entity='{execution.EntityId}' charId={_charId} questId={questId}.");
        await WriteAsync(IroNpcDialoguePackets.BuildClose(execution.ActorId), cancellationToken);
        _scriptExecutionSession = null;
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
        _mapName = map; _x = warp.X; _y = warp.Y; _positionDirty = true; _visibleActorIds.Clear();
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

    private async Task SendVisibleWarpActorsAsync(CancellationToken cancellationToken)
    {
        foreach (var actor in _worldMapRegistry.GetVisibleWarpActors(_mapName, _x, _y))
        {
            if (!_visibleActorIds.Add(actor.ActorId))
            {
                continue;
            }

            var packet = IroWorldActorPackets.BuildWarpActor(actor);
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
