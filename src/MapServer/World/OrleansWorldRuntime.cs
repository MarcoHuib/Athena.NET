using System.Diagnostics;
using Athena.Net.MapServer.Telemetry;
using Athena.Net.World.Contracts;
using Orleans;

namespace Athena.Net.MapServer.World;

public sealed class OrleansWorldRuntime(IClusterClient clusterClient) : IWorldRuntime
{
    public async Task<MapPresenceRegistration> RegisterPresenceAsync(string mapId, MapPlayerPresence presence, CancellationToken cancellationToken)
    {
        using var activity = MapTelemetry.ActivitySource.StartActivity("world.map.register-presence", ActivityKind.Client);
        activity?.SetTag("world.map.id", NormalizeMapId(mapId));
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await clusterClient.GetGrain<IMapGrain>(NormalizeMapId(mapId)).RegisterPresenceAsync(presence).WaitAsync(cancellationToken);
        }
        catch
        {
            MapTelemetry.WorldCommandFailures.Add(1, new KeyValuePair<string, object?>("world.command", "register-presence"));
            throw;
        }
        finally
        {
            MapTelemetry.WorldCommandDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, new KeyValuePair<string, object?>("world.command", "register-presence"));
        }
    }

    public Task<bool> UnregisterPresenceAsync(string mapId, uint characterId, CancellationToken cancellationToken) =>
        clusterClient.GetGrain<IMapGrain>(NormalizeMapId(mapId)).UnregisterPresenceAsync(characterId).WaitAsync(cancellationToken);

    private static string NormalizeMapId(string mapId) => MapName.NormalizeWorld(mapId).ToLowerInvariant();
}
