using System.Net;
using Athena.Net.CharServer.Net;

namespace Athena.Net.CharServer.Tests.Net;

// Live bug: the server-selection screen showed 0 online players even with one already in the world.
// Root cause was that CharServer never aggregated/forwarded any online-player count at all - these
// tests pin the aggregation half of the fix: MapServerRegistry.TotalUsers must represent the SUM of
// every currently-registered MapServer connection's own last-reported authenticated-player count,
// never a raw connection count, never accumulated across a MapServer reconnect.
public sealed class MapServerRegistryUserCountTests
{
    private static void Register(MapServerRegistry registry, int sessionId) =>
        Assert.True(registry.TryRegister(sessionId, IPAddress.Loopback, 5121, null!));

    [Fact]
    public void NoMapServersRegistered_TotalUsersIsZero()
    {
        var registry = new MapServerRegistry();
        Assert.Equal(0, registry.TotalUsers);
    }

    [Fact]
    public void OneMapServer_ReportsOnePlayer_TotalUsersIsOne()
    {
        var registry = new MapServerRegistry();
        Register(registry, 1);

        registry.UpdateUserCount(1, 1);

        Assert.Equal(1, registry.TotalUsers);
    }

    [Fact]
    public void OneMapServer_ReportsTwoPlayers_TotalUsersIsTwo()
    {
        var registry = new MapServerRegistry();
        Register(registry, 1);

        registry.UpdateUserCount(1, 2);

        Assert.Equal(2, registry.TotalUsers);
    }

    [Fact]
    public void TwoMapServers_AreAggregated_NotOverwritten()
    {
        var registry = new MapServerRegistry();
        Register(registry, 1);
        Register(registry, 2);

        registry.UpdateUserCount(1, 2);
        registry.UpdateUserCount(2, 3);

        Assert.Equal(5, registry.TotalUsers); // 2 + 3, matching the exact example from the task.
    }

    [Fact]
    public void LaterReportFromTheSameMapServer_ReplacesItsOwnContribution_NeverAdds()
    {
        var registry = new MapServerRegistry();
        Register(registry, 1);

        registry.UpdateUserCount(1, 1);
        registry.UpdateUserCount(1, 2); // A later, absolute report from the SAME connection.

        Assert.Equal(2, registry.TotalUsers); // Not 1 + 2 = 3.
    }

    [Fact]
    public void DisconnectingOneMapServer_RemovesOnlyItsOwnContribution()
    {
        var registry = new MapServerRegistry();
        Register(registry, 1);
        Register(registry, 2);
        registry.UpdateUserCount(1, 2);
        registry.UpdateUserCount(2, 3);

        registry.Remove(1);

        Assert.Equal(3, registry.TotalUsers);
    }

    [Fact]
    public void DisconnectingTheOnlyMapServer_ReturnsTotalUsersToZero()
    {
        var registry = new MapServerRegistry();
        Register(registry, 1);
        registry.UpdateUserCount(1, 1);

        registry.Remove(1);

        Assert.Equal(0, registry.TotalUsers);
    }

    [Fact]
    public void Reconnect_UnderANewSessionId_DoesNotInheritOrAccumulateTheStaleCount()
    {
        var registry = new MapServerRegistry();
        Register(registry, 1);
        registry.UpdateUserCount(1, 5);
        registry.Remove(1); // MapServer disconnects.

        Register(registry, 2); // Reconnects, assigned a new session id.
        Assert.Equal(0, registry.TotalUsers); // No stale contribution survives the reconnect.

        registry.UpdateUserCount(2, 5); // Reconstructs its own current count after reconnecting.
        Assert.Equal(5, registry.TotalUsers); // Exactly its own fresh report, not 5 + 5.
    }

    [Fact]
    public void UpdateUserCount_ForAnUnregisteredSessionId_IsASilentNoOp()
    {
        var registry = new MapServerRegistry();
        registry.UpdateUserCount(999, 7); // Never registered.
        Assert.Equal(0, registry.TotalUsers);
    }
}
