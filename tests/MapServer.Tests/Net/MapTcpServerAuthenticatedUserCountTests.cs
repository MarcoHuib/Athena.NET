using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

// Live bug: the LoginServer server-selection screen showed 0 online players even with one already in
// the world. These tests pin the MapServer-side half of the fix: MapTcpServer's own authenticated-
// player count (what it reports to CharServer via CharServerConnector.TrySendUserCountAsync, and what
// CharServer in turn aggregates - see MapServerSessionUserCountTests/MapServerRegistryUserCountTests
// on the CharServer side for the rest of the pipeline) must count ONLY sessions whose map
// authentication actually succeeded, and must go back down the instant an authenticated session is
// removed - for any disconnect reason, since every one of them (normal close, IOException, session
// cancellation) converges onto the SAME _sessions.TryRemove + ReportAuthenticatedUserCount cleanup in
// HandleClientAsync's own finally block.
public sealed class MapTcpServerAuthenticatedUserCountTests
{
    private static MapConfigStore ConfigStore() => new(new MapConfig(), "unused.conf");

    private static MapServerWorld MakeWorld() => new(
        WorldMapRegistry.Tutorial, [], new MonsterCombatCoordinator(new QuestDropResolver([]), new RenewalBasicAttackRules()),
        EmptyMapCollisionProvider.Instance, new UnverifiedGridLineMovementPathProvider(), new MonsterFeedProjectionRegistry(), new MonsterAttackCadenceStore());

    private static MapTcpServer MakeServer() => new(ConfigStore(), new CharServerConnector(ConfigStore()), MakeWorld());

    // Same narrow reflection seam MapTcpServerRunAsyncSupervisionTests already establishes for
    // MapTcpServer's own private `_sessions` dictionary (there is no other production seam for
    // inserting/removing an already-constructed session directly).
    private static void InjectSession(MapTcpServer server, int sessionId, MapClientSession session)
    {
        var field = typeof(MapTcpServer).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("MapTcpServer._sessions field not found - test seam broken by a rename.");
        ((ConcurrentDictionary<int, MapClientSession>)field.GetValue(server)!)[sessionId] = session;
    }

    private static void RemoveSessionAndReportCount(MapTcpServer server, int sessionId)
    {
        var field = typeof(MapTcpServer).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((ConcurrentDictionary<int, MapClientSession>)field.GetValue(server)!).TryRemove(sessionId, out _);
        // Mirrors HandleClientAsync's own finally block exactly - the one place every disconnect
        // reason (normal close, IOException, cancellation) converges onto this same call.
        typeof(MapTcpServer).GetMethod("ReportAuthenticatedUserCount", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(server, null);
    }

    private static (TcpClient Client, MapClientSession Session) MakeSession(int sessionId, bool authenticated)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = listener.AcceptTcpClient();
        connect.GetAwaiter().GetResult();
        listener.Stop();
        var session = new MapClientSession(sessionId, serverClient, new CharServerConnector(ConfigStore()), authenticated);
        return (client, session);
    }

    [Fact]
    public void NoSessions_CountIsZero()
    {
        var server = MakeServer();
        Assert.Equal(0, server.AuthenticatedSessionCountForTest);
    }

    [Fact]
    public async Task UnauthenticatedSession_DoesNotIncrementTheCount()
    {
        var server = MakeServer();
        var (client, session) = MakeSession(1, authenticated: false);
        using var _ = client;
        InjectSession(server, 1, session);

        Assert.Equal(0, server.AuthenticatedSessionCountForTest);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task OneAuthenticatedSession_CountIsOne()
    {
        var server = MakeServer();
        var (client, session) = MakeSession(1, authenticated: true);
        using var _ = client;
        InjectSession(server, 1, session);

        Assert.Equal(1, server.AuthenticatedSessionCountForTest);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task TwoAuthenticatedSessions_CountIsTwo()
    {
        var server = MakeServer();
        var (clientA, sessionA) = MakeSession(1, authenticated: true);
        var (clientB, sessionB) = MakeSession(2, authenticated: true);
        using var _a = clientA; using var _b = clientB;
        InjectSession(server, 1, sessionA);
        InjectSession(server, 2, sessionB);

        Assert.Equal(2, server.AuthenticatedSessionCountForTest);
        await sessionA.DisposeAsync(); await sessionB.DisposeAsync();
    }

    [Fact]
    public async Task MixOfAuthenticatedAndUnauthenticated_OnlyAuthenticatedAreCounted()
    {
        var server = MakeServer();
        var (clientA, sessionA) = MakeSession(1, authenticated: true);
        var (clientB, sessionB) = MakeSession(2, authenticated: false);
        using var _a = clientA; using var _b = clientB;
        InjectSession(server, 1, sessionA);
        InjectSession(server, 2, sessionB);

        Assert.Equal(1, server.AuthenticatedSessionCountForTest);
        await sessionA.DisposeAsync(); await sessionB.DisposeAsync();
    }

    [Fact]
    public async Task DisconnectingAnAuthenticatedSession_DecrementsTheCount()
    {
        var server = MakeServer();
        var (clientA, sessionA) = MakeSession(1, authenticated: true);
        var (clientB, sessionB) = MakeSession(2, authenticated: true);
        using var _a = clientA; using var _b = clientB;
        InjectSession(server, 1, sessionA);
        InjectSession(server, 2, sessionB);
        Assert.Equal(2, server.AuthenticatedSessionCountForTest);

        RemoveSessionAndReportCount(server, 1); // The exact cleanup HandleClientAsync's finally performs.

        Assert.Equal(1, server.AuthenticatedSessionCountForTest);
        await sessionA.DisposeAsync(); await sessionB.DisposeAsync();
    }

    [Fact]
    public async Task DisconnectingTheOnlyAuthenticatedSession_ReturnsTheCountToZero_NeverNegative()
    {
        var server = MakeServer();
        var (client, session) = MakeSession(1, authenticated: true);
        using var _ = client;
        InjectSession(server, 1, session);
        Assert.Equal(1, server.AuthenticatedSessionCountForTest);

        RemoveSessionAndReportCount(server, 1);
        RemoveSessionAndReportCount(server, 1); // Redundant removal (already gone) - must never go negative.

        Assert.Equal(0, server.AuthenticatedSessionCountForTest);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task DisconnectingAnUnauthenticatedSession_NeverAffectsTheCount()
    {
        var server = MakeServer();
        var (clientA, sessionA) = MakeSession(1, authenticated: true);
        var (clientB, sessionB) = MakeSession(2, authenticated: false);
        using var _a = clientA; using var _b = clientB;
        InjectSession(server, 1, sessionA);
        InjectSession(server, 2, sessionB);
        Assert.Equal(1, server.AuthenticatedSessionCountForTest);

        RemoveSessionAndReportCount(server, 2);

        Assert.Equal(1, server.AuthenticatedSessionCountForTest);
        await sessionA.DisposeAsync(); await sessionB.DisposeAsync();
    }
}
