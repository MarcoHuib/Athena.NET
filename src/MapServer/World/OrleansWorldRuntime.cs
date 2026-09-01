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
