using System.Diagnostics;
using Athena.Net.MapServer.Telemetry;
using Athena.Net.World.Contracts;
using Orleans;

namespace Athena.Net.MapServer.World;

public sealed class OrleansWorldRuntime(IClusterClient clusterClient, IWorldPartitionResolver resolver) : IWorldRuntime
{
    public async Task<WorldPresenceRegistration> RegisterPresenceAsync(string mapId, WorldPlayerPresence presence, CancellationToken cancellationToken)
    {
        var normalized = WorldMapId.Normalize(mapId);
        var partitionId = resolver.ResolvePartition(normalized);
        using var activity = Start("world.partition.register-presence", partitionId, normalized);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await Grain(partitionId).RegisterPresenceAsync(presence with { MapId = normalized }).WaitAsync(cancellationToken);
            if (result.Status == WorldPresenceRegistrationStatus.Conflict) Failure("register-presence-conflict");
            return result;
        }
        catch { Failure("register-presence"); throw; }
        finally { Duration(started, "register-presence", partitionId); }
    }

    public Task<WorldPresenceUnregistration> UnregisterPresenceAsync(string mapId, uint characterId, Guid presenceId, CancellationToken cancellationToken)
    {
        var normalized = WorldMapId.Normalize(mapId);
        return Grain(resolver.ResolvePartition(normalized)).UnregisterPresenceAsync(normalized, characterId, presenceId).WaitAsync(cancellationToken);
    }

    public async Task<WorldMovementResult> MovePlayerAsync(WorldMovementCommand command, CancellationToken cancellationToken)
    {
        var partitionId = resolver.ResolvePartition(command.MapId);
        var started = Stopwatch.GetTimestamp();
        try { return await Grain(partitionId).MovePlayerAsync(command with { MapId = WorldMapId.Normalize(command.MapId) }).WaitAsync(cancellationToken); }
        catch { Failure("move-player"); throw; }
        finally { Duration(started, "move-player", partitionId); }
    }

    public Task<WorldMovementResult> TruncateMovementAsync(WorldMovementTruncation command, CancellationToken cancellationToken)
    {
        var mapId = WorldMapId.Normalize(command.MapId);
        return Grain(resolver.ResolvePartition(mapId)).TruncateMovementAsync(command with { MapId = mapId }).WaitAsync(cancellationToken);
    }

    public Task<WorldMovementAdvanceResult> AdvanceMovementAsync(WorldMovementAdvance command, CancellationToken cancellationToken)
    {
        var mapId = WorldMapId.Normalize(command.MapId);
        return Grain(resolver.ResolvePartition(mapId)).AdvanceMovementAsync(command with { MapId = mapId }).WaitAsync(cancellationToken);
    }

    public Task<WorldMovementCancellationResult> CancelMovementAsync(WorldMovementCancellation command, CancellationToken cancellationToken)
    {
        var mapId = WorldMapId.Normalize(command.MapId);
        return Grain(resolver.ResolvePartition(mapId)).CancelMovementAsync(command with { MapId = mapId }).WaitAsync(cancellationToken);
    }

    public async Task<WorldTransferResult> TransferPlayerAsync(WorldTransferCommand command, CancellationToken cancellationToken)
    {
        var sourcePartition = resolver.ResolvePartition(command.SourceMapId);
        var routed = command with
        {
            SourceMapId = WorldMapId.Normalize(command.SourceMapId),
            DestinationMapId = WorldMapId.Normalize(command.DestinationMapId),
        };
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await Grain(sourcePartition).TransferPlayerAsync(routed).WaitAsync(cancellationToken);
            if (result.Status is WorldTransferStatus.Conflict or WorldTransferStatus.NotFound or WorldTransferStatus.SourceMismatch) Failure("transfer");
            return result;
        }
        catch { Failure("transfer"); throw; }
        finally { Duration(started, "transfer-player", sourcePartition); }
    }

    // `batch.MapId` is normalized here BEFORE resolving the partition (matching every other
    // command's own ordering) - the grain itself also normalizes internally (defense in depth,
    // per its own RequireOwnedMap contract), but resolving the WRONG partition from an
    // un-normalized map id would route the call to the wrong grain entirely, which internal
    // normalization alone cannot fix after the fact.
    public async Task<WorldMonsterSpawnLoadResult> LoadMonsterSpawnsAsync(WorldMonsterSpawnBatch batch, CancellationToken cancellationToken)
    {
        var mapId = WorldMapId.Normalize(batch.MapId);
        var partitionId = resolver.ResolvePartition(mapId);
        var started = Stopwatch.GetTimestamp();
        try { return await Grain(partitionId).LoadMonsterSpawnsAsync(batch with { MapId = mapId }).WaitAsync(cancellationToken); }
        catch { Failure("load-monster-spawns"); throw; }
        finally { Duration(started, "load-monster-spawns", partitionId); }
    }

    public async Task<WorldMonsterFeedPage> PollMonsterFeedAsync(WorldMonsterFeedCursor? cursor, string mapId, CancellationToken cancellationToken)
    {
        var normalized = WorldMapId.Normalize(mapId);
        var partitionId = resolver.ResolvePartition(normalized);
        var started = Stopwatch.GetTimestamp();
        try { return await Grain(partitionId).PollMonsterFeedAsync(cursor, normalized).WaitAsync(cancellationToken); }
        catch { Failure("poll-monster-feed"); throw; }
        finally { Duration(started, "poll-monster-feed", partitionId); }
    }

    public async Task<WorldMonsterDeathResult> TryMarkMonsterDeadAsync(WorldMonsterLifeReference reference, CancellationToken cancellationToken)
    {
        var mapId = WorldMapId.Normalize(reference.MapId);
        var partitionId = resolver.ResolvePartition(mapId);
        var started = Stopwatch.GetTimestamp();
        try { return await Grain(partitionId).TryMarkMonsterDeadAsync(reference with { MapId = mapId }).WaitAsync(cancellationToken); }
        catch { Failure("try-mark-monster-dead"); throw; }
        finally { Duration(started, "try-mark-monster-dead", partitionId); }
    }

    public async Task<WorldMonsterAttackedResult> NotifyMonsterAttackedAsync(WorldMonsterAttackedCommand command, CancellationToken cancellationToken)
    {
        var mapId = WorldMapId.Normalize(command.Life.MapId);
        var partitionId = resolver.ResolvePartition(mapId);
        var started = Stopwatch.GetTimestamp();
        try { return await Grain(partitionId).NotifyMonsterAttackedAsync(command with { Life = command.Life with { MapId = mapId } }).WaitAsync(cancellationToken); }
        catch { Failure("notify-monster-attacked"); throw; }
        finally { Duration(started, "notify-monster-attacked", partitionId); }
    }

    public async Task<WorldMonsterAttackWindowResult> ValidateMonsterAttackWindowAsync(WorldMonsterAttackWindowQuery query, CancellationToken cancellationToken)
    {
        var mapId = WorldMapId.Normalize(query.Life.MapId);
        var partitionId = resolver.ResolvePartition(mapId);
        var started = Stopwatch.GetTimestamp();
        try { return await Grain(partitionId).ValidateMonsterAttackWindowAsync(query with { Life = query.Life with { MapId = mapId } }).WaitAsync(cancellationToken); }
        catch { Failure("validate-monster-attack-window"); throw; }
        finally { Duration(started, "validate-monster-attack-window", partitionId); }
    }

    // UpdatePresenceLifeStateAsync has no map id of its own on the wire contract (it addresses a
    // presence purely by CharacterId+PresenceId - see that update's own doc comment) - it must
    // still be routed to the SAME partition the character's own presence is currently registered
    // under. `mapId` is therefore an explicit parameter here (not part of WorldPresenceLifeStateUpdate
    // itself, which stays exactly as the grain contract defines it) - the caller (MapClientSession,
    // which already knows its own current map) is the only place that legitimately has this
    // information without a speculative extra lookup/round-trip.
    public async Task<WorldPresenceLifeStateResult> UpdatePresenceLifeStateAsync(string mapId, WorldPresenceLifeStateUpdate update, CancellationToken cancellationToken)
    {
        var normalized = WorldMapId.Normalize(mapId);
        var partitionId = resolver.ResolvePartition(normalized);
        var started = Stopwatch.GetTimestamp();
        try { return await Grain(partitionId).UpdatePresenceLifeStateAsync(update).WaitAsync(cancellationToken); }
        catch { Failure("update-presence-life-state"); throw; }
        finally { Duration(started, "update-presence-life-state", partitionId); }
    }

    private IWorldPartitionGrain Grain(string partitionId) => clusterClient.GetGrain<IWorldPartitionGrain>(partitionId);
    private static Activity? Start(string name, string partitionId, string mapId)
    {
        var activity = MapTelemetry.ActivitySource.StartActivity(name, ActivityKind.Client);
        activity?.SetTag("world.partition.id", partitionId);
        activity?.SetTag("world.map.id", mapId);
        return activity;
    }
    private static void Failure(string command) => MapTelemetry.WorldCommandFailures.Add(1, new KeyValuePair<string, object?>("world.command", command));
    private static void Duration(long started, string command, string partitionId) => MapTelemetry.WorldCommandDuration.Record(
        Stopwatch.GetElapsedTime(started).TotalMilliseconds,
        new KeyValuePair<string, object?>("world.command", command),
        new KeyValuePair<string, object?>("world.partition.id", partitionId));
}
