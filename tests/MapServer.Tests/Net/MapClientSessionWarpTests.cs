using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Generated.GameData.Mobs;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.GeneratedScripts;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class MapClientSessionWarpTests
{
    // Bug 1 root-cause regression (izlude_a (20,97) movement lock): spawning INSIDE a static warp's
    // rectangle must fire the warp immediately on load (mirroring pinned clif_parse_LoadEndAck's
    // "so you don't need to walk 1 step first"), before any movement packet - never leaving the
    // player stuck making rejected movement requests forever.
    [Fact]
    public async Task SpawningInsideWarpRectangle_FiresWarpImmediatelyOnLoad_BeforeAnyMovementPacket()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var connectTask = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var serverClient = await listener.AcceptTcpClientAsync();
        await connectTask;
        await using var clientStream = client.GetStream();
        var connector = new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf"));
        // iz001_a-shaped fixture: center (20,98) radius (3,3), destination prt_fild08a - matching
        // the exact live izlude_a (20,97) scenario, which sits inside this rectangle at spawn.
        var warp = new WarpDefinition("iz001_a", "izlude_a", 20, 98, 3, 3, "prt_fild08a", 25, 99, true, "test", 1);
        var registry = new WorldMapRegistry([warp]);
        await using var session = new MapClientSession(
            1, serverClient, connector, iroAuthenticated: true, mapName: "izlude_a", x: 20, y: 97,
            worldMapRegistry: registry);
        var runTask = session.RunAsync(CancellationToken.None);

        // No CompleteIroAuthenticationAsync/gameplay state here, so no self-weapon/inventory
        // packets are sent (SendSelfWeaponAppearanceAsync/SendSelfInventoryAsync return early with
        // no equipment/gameplay state loaded) - the touch check response is the very first packet.
        await clientStream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });

        // The warp must fire immediately - no movement packet was ever sent.
        var mapChange = new byte[22];
        await clientStream.ReadExactlyAsync(mapChange);
        Assert.Equal((short)0x0091, BinaryPrimitives.ReadInt16LittleEndian(mapChange));
        Assert.Equal("prt_fild08a", session.CurrentMapName);
        Assert.Equal((ushort)25, session.CurrentX);
        Assert.Equal((ushort)99, session.CurrentY);

        client.Close();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();
    }

    // Correction #5/#6: a script OnTouch entity whose handler does NOT warp must fire immediately
    // on load too (the generic touch mechanism is not warp-only), but map initialization for the
    // (unchanged) current map must still continue afterward - never a blanket "always break".
    [Fact]
    public async Task SpawningInsideScriptTouchRectangle_WithoutWarping_FiresImmediately_AndContinuesMapInitialization()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var connectTask = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var serverClient = await listener.AcceptTcpClientAsync();
        await connectTask;
        await using var clientStream = client.GetStream();
        var connector = new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf"));

        var entity = BuildTouchEntity("test-map", 50, 50, radius: 3, out var actorName);
        var registration = new GeneratedScriptRegistration(entity, "OnTouch", static () => new NoOpScript());
        var scripts = new NpcScriptRegistryBuilder().AddCustom(registration).Build();
        var registry = new WorldMapRegistry([], [entity], scripts: scripts);
        await using var session = new MapClientSession(
            1, serverClient, connector, iroAuthenticated: true, mapName: "test-map", x: 50, y: 50,
            worldMapRegistry: registry);
        var runTask = session.RunAsync(CancellationToken.None);

        await clientStream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });

        // Never warped - stays on the same map, at the same cell.
        Assert.Equal("test-map", session.CurrentMapName);
        Assert.Equal((ushort)50, session.CurrentX);
        Assert.Equal((ushort)50, session.CurrentY);

        // Map initialization for the (unchanged) current map must still continue: the visible warp
        // actor list send is drained next by reading a further packet with no map-change opcode.
        // The touch entity's actor spawn (from SendVisibleWarpActorsAsync, driven by
        // GetVisibleWarpActors) is the next packet on the wire.
        var next = await ReadDynamicOrFixed(clientStream);
        Assert.NotEqual((short)0x0091, BinaryPrimitives.ReadInt16LittleEndian(next.AsSpan(0, 2)));

        client.Close();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();
    }

    // Correction #5: map change is not exclusive to the WARP branch - a script's OnTouch handler
    // that itself warps must ALSO report MapChanged (derived from the actual post-touch map, never
    // from "it was a script, not a warp").
    [Fact]
    public async Task SpawningInsideScriptTouchRectangle_WhoseHandlerWarps_ReportsMapChanged_SkipsOldMapProjection()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var connectTask = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var serverClient = await listener.AcceptTcpClientAsync();
        await connectTask;
        await using var clientStream = client.GetStream();
        var connector = new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf"));

        var entity = BuildTouchEntity("test-map", 50, 50, radius: 3, out _);
        var registration = new GeneratedScriptRegistration(entity, "OnTouch", static () => new WarpingScript("other-map", 10, 10));
        var scripts = new NpcScriptRegistryBuilder().AddCustom(registration).Build();
        var registry = new WorldMapRegistry([], [entity], scripts: scripts);
        await using var session = new MapClientSession(
            1, serverClient, connector, iroAuthenticated: true, mapName: "test-map", x: 50, y: 50,
            worldMapRegistry: registry);
        var runTask = session.RunAsync(CancellationToken.None);

        await clientStream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });

        var mapChange = new byte[22];
        await clientStream.ReadExactlyAsync(mapChange);
        Assert.Equal((short)0x0091, BinaryPrimitives.ReadInt16LittleEndian(mapChange));
        Assert.Equal("other-map", session.CurrentMapName);
        Assert.Equal((ushort)10, session.CurrentX);
        Assert.Equal((ushort)10, session.CurrentY);

        client.Close();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();
    }

    // Priority 6: a script OnTouch handler warping WITHIN THE SAME map name (e.g. a same-map
    // teleport pad) still sends a real 0x0091 client-facing map transition and must report
    // MapChanged - the OLD map-name-equality-only check would have wrongly reported
    // ScriptStartedSameMap here, since _mapName itself never changes. The transition-generation
    // counter (bumped by TeleportTo, the single funnel every warp path goes through) is what makes
    // this distinguishable from a script that merely opens dialogue with no warp at all.
    [Fact]
    public async Task SpawningInsideScriptTouchRectangle_WhoseHandlerWarpsWithinTheSameMap_ReportsMapChanged()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var connectTask = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var serverClient = await listener.AcceptTcpClientAsync();
        await connectTask;
        await using var clientStream = client.GetStream();
        var connector = new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf"));

        var entity = BuildTouchEntity("test-map", 50, 50, radius: 3, out _);
        // Warps to a DIFFERENT (x,y) on the SAME map name the entity/session already occupy.
        var registration = new GeneratedScriptRegistration(entity, "OnTouch", static () => new WarpingScript("test-map", 150, 150));
        var scripts = new NpcScriptRegistryBuilder().AddCustom(registration).Build();
        var registry = new WorldMapRegistry([], [entity], scripts: scripts);
        await using var session = new MapClientSession(
            1, serverClient, connector, iroAuthenticated: true, mapName: "test-map", x: 50, y: 50,
            worldMapRegistry: registry);
        var runTask = session.RunAsync(CancellationToken.None);

        await clientStream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });

        // A real 0x0091 map transition IS sent, even though the map name never changes.
        var mapChange = new byte[22];
        await clientStream.ReadExactlyAsync(mapChange);
        Assert.Equal((short)0x0091, BinaryPrimitives.ReadInt16LittleEndian(mapChange));
        Assert.Equal("test-map", session.CurrentMapName);
        Assert.Equal((ushort)150, session.CurrentX);
        Assert.Equal((ushort)150, session.CurrentY);

        client.Close();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();
    }

    // Issue 2 root-cause regression (prtf004_a on prt_fild08a): a warp actor entering the player's
    // visibility radius as their route approaches it must be sent to the client, even though the
    // route has a pending warp arrival - actor visibility must never be suppressed by
    // ResolvedMovementTarget.IntersectsWarp/IntersectsScript. The warp must become visible BEFORE
    // the eventual map-change, never merely fire correctly while invisible.
    [Fact]
    public async Task WarpEnteringVisibilityWhileRouteIntersectsIt_IsSentBeforeTheMapChange()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var connectTask = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var serverClient = await listener.AcceptTcpClientAsync();
        await connectTask;
        await using var clientStream = client.GetStream();
        var connector = new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf"));
        // prtf004_a-shaped fixture, matching the exact live shape: the player's CURRENT cell (10,0)
        // is already within visibility range (WorldVisibilityOptions.DefaultAreaSize=14) of the
        // warp at click time, but the click's route intersects the warp - the live bug was
        // click-time visibility being skipped for exactly this reason (IntersectsWarp==true),
        // never a "walked gradually into range" scenario.
        var warp = new WarpDefinition("prtf004_a", "test-warp-map", 20, 0, 3, 2, "other-map", 156, 26, true, "test", 1);
        var registry = new WorldMapRegistry([warp]);
        await using var session = new MapClientSession(
            1, serverClient, connector, iroAuthenticated: true, mapName: "test-warp-map", x: 10, y: 0,
            worldMapRegistry: registry);
        var runTask = session.RunAsync(CancellationToken.None);

        await clientStream.WriteAsync(BuildMovementRequest(23, 0));

        // 0x0087 (the movement response) is always first, THEN the warp actor must be sent -
        // BEFORE the eventual map-change - even though this exact route's first click already
        // intersects the warp (deferred to actual arrival).
        var movementResponse = await ReadDynamicOrFixed(clientStream);
        Assert.Equal((short)0x0087, BinaryPrimitives.ReadInt16LittleEndian(movementResponse));

        var actorOrMapChange = await ReadDynamicOrFixed(clientStream);
        Assert.NotEqual((short)0x0091, BinaryPrimitives.ReadInt16LittleEndian(actorOrMapChange));
        Assert.Equal((short)0x09ff, BinaryPrimitives.ReadInt16LittleEndian(actorOrMapChange));

        client.Close();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();
    }

    // Issue 2: monster visibility must ALSO be independent of route-trigger metadata - a standing
    // monster within visibility range at click time must be sent even when the click's route
    // intersects a warp/script trigger elsewhere along it.
    [Fact]
    public async Task MonsterEnteringVisibilityWhileRouteAlsoIntersectsAWarp_IsSentBeforeTheMapChange()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var connectTask = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var serverClient = await listener.AcceptTcpClientAsync();
        await connectTask;
        await using var clientStream = client.GetStream();
        var connector = new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf"));
        // Same shape as the warp-only test above, PLUS a monster placed within visibility range of
        // the player's own click-time cell, on a completely different bearing from the warp - the
        // trigger metadata belongs to the warp; it must not suppress the monster's visibility either.
        var warp = new WarpDefinition("prtf004_a", "test-warp-map", 20, 0, 3, 2, "other-map", 156, 26, true, "test", 1);
        // Production (MapServerWorld.Build) shares ONE WorldActorIdAllocator between WorldMapRegistry
        // and MonsterRegistry so every actor kind draws from one ID namespace - a synthetic fixture
        // that instead gives each registry its OWN allocator can accidentally assign the warp and the
        // monster the SAME ActorId (both allocators start at the same base value), which silently
        // breaks _visibleActorIds' shared dedup set: the second actor with a colliding ID is treated
        // as "already announced" and never sent. Reproduce the shared-allocator invariant here.
        var allocator = new WorldActorIdAllocator();
        var registry = new WorldMapRegistry([warp], [], scripts: null, allocator: allocator);
        var spawn = new MobSpawnDefinition(GeneratedMobs.GPoring, "test-warp-map", 1, 5000, 0, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 0));
        var monsters = new MonsterRegistry([spawn], allocator.Allocate, new FixedCellSelector(10, 5), TimeProvider.System);
        Assert.Single(monsters.AllInstances);
        Assert.True(monsters.AllInstances[0].IsAlive);
        Assert.Single(monsters.GetVisibleInstances("test-warp-map", 10, 0));
        var warpActorId = registry.GetVisibleWarpActors("test-warp-map", 20, 0).Single().ActorId;
        var monsterActorId = monsters.AllInstances[0].ActorId;
        Assert.NotEqual(warpActorId, monsterActorId);
        // Step 6 cutover: SendVisibleMonsterActorsAsync (the monster spawn fan-out this test asserts
        // on) requires a non-null combatState too - it reads CurrentHp from it for the spawn
        // packet's own HP fields; see MapClientSessionMonsterMovementTests.SetupAsync's own doc
        // comment for the identical fix.
        var warpTestEpoch = WorldSimulationEpoch.NewEpoch();
        var warpTestCombatState = new MonsterCombatStateStore();
        warpTestCombatState.Register(monsters.AllInstances[0].Map, warpTestEpoch, monsters.AllInstances[0].ActorId, new WorldMonsterIncarnationId(monsters.AllInstances[0].IncarnationId.Value), monsters.AllInstances[0].Spawn.Mob.MaxHp);
        var warpTestProjections = WorldMonsterProjectionTestHelper.SeedProjection(monsters.AllInstances[0].Map, warpTestEpoch, warpTestCombatState, monsters.AllInstances);
        await using var session = new MapClientSession(
            1, serverClient, connector, iroAuthenticated: true, mapName: "test-warp-map", x: 10, y: 0,
            worldMapRegistry: registry, monsterProjections: warpTestProjections, combatState: warpTestCombatState);
        var runTask = session.RunAsync(CancellationToken.None);

        await clientStream.WriteAsync(BuildMovementRequest(23, 0));

        var movementResponse = await ReadDynamicOrFixed(clientStream);
        Assert.Equal((short)0x0087, BinaryPrimitives.ReadInt16LittleEndian(movementResponse));

        // Drain up to 2 further packets (warp actor + monster actor, order not asserted) and
        // confirm the monster's own standing-entry packet appears among them before any map-change.
        var sawMonsterActor = false;
        for (var i = 0; i < 2; i++)
        {
            var packet = await ReadDynamicOrFixed(clientStream);
            var opcode = BinaryPrimitives.ReadInt16LittleEndian(packet);
            Assert.NotEqual((short)0x0091, opcode);
            if (opcode == (short)0x09ff && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(5)) == monsters.AllInstances[0].ActorId)
                sawMonsterActor = true;
        }
        Assert.True(sawMonsterActor);

        client.Close();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();
    }

    // Priority 2: a warp genuinely OUTSIDE click-time AOI (distance > 14) must become visible
    // partway through an UNINTERRUPTED walk with no retarget - the gap this PR fixes
    // (RefreshVisibleWorldActorsAsync previously only ran on the initial click and on an applied
    // retarget, never on a plain crossed cell). Route: straight line along y=0 from (0,0) to
    // (30,0); the warp's own trigger rectangle is centered off that line (25,5) with radius 1 so
    // the route NEVER intersects it (IntersectsWarp stays false throughout) - this is purely an
    // AOI-visibility scenario, not a route-intersection one. Visibility range is Chebyshev
    // (WorldVisibilityOptions.DefaultAreaSize=14), so the warp (at distance max(|x-25|,5) from the
    // player's own (x,0)) enters range only once x>=11.
    [Fact]
    public async Task WarpEntersVisibilityDuringAnUninterruptedWalk_WithNoRetarget_IsSentOnceInRange()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var connectTask = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var serverClient = await listener.AcceptTcpClientAsync();
        await connectTask;
        await using var clientStream = client.GetStream();
        var connector = new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf"));
        var warp = new WarpDefinition("aoi-warp", "aoi-map", 25, 5, 1, 1, "other-map", 1, 1, true, "test", 1);
        var registry = new WorldMapRegistry([warp]);
        var clock = new Athena.Net.MapServer.Tests.Testing.ControllableTimeProvider();
        await using var session = new MapClientSession(
            1, serverClient, connector, iroAuthenticated: true, mapName: "aoi-map", x: 0, y: 0,
            worldMapRegistry: registry, timeProvider: clock);
        var runTask = session.RunAsync(CancellationToken.None);

        await clientStream.WriteAsync(BuildMovementRequest(30, 0));
        var movement = new byte[12];
        await clientStream.ReadExactlyAsync(movement);
        Assert.Equal((short)0x0087, BinaryPrimitives.ReadInt16LittleEndian(movement));
        // No warp actor at click time - (0,0) to warp (25,5) is distance 25, well outside range 14.

        // Drive the clock cell-by-cell up to x=10 (still out of range: distance max(15,5)=15) and
        // confirm no actor packet appears yet - each boundary is synchronized via the registration
        // generation, exactly matching RetargetAwayFromADoor_MidWalk_...'s own idiom.
        for (var x = 1; x <= 10; x++)
        {
            var generation = clock.RegistrationGeneration;
            await clock.AdvanceAsync(TimeSpan.FromMilliseconds(150));
            await clock.WaitForRegistrationAfterAsync(generation).WaitAsync(TimeSpan.FromSeconds(5));
        }
        await clientStream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var pingReply = new byte[2];
        await clientStream.ReadExactlyAsync(pingReply);
        Assert.Equal((short)0x0b1d, BinaryPrimitives.ReadInt16LittleEndian(pingReply));

        // Cross the boundary into range (x=11, distance max(14,5)=14 <= 14) - the warp actor must
        // now appear, with no map-change ever following from this same uninterrupted walk.
        var generationAtBoundary = clock.RegistrationGeneration;
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(150));
        await clock.WaitForRegistrationAfterAsync(generationAtBoundary).WaitAsync(TimeSpan.FromSeconds(5));

        var actorPacket = await ReadUntilOpcode(clientStream, 0x09ff);
        Assert.Equal((short)0x09ff, BinaryPrimitives.ReadInt16LittleEndian(actorPacket));

        client.Close();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();
    }

    // Priority 2: the monster equivalent of the warp test above - a monster genuinely outside
    // click-time AOI must become visible partway through an uninterrupted walk with no retarget.
    // Reuses the shared-allocator fixture from Priority 1's own fix.
    [Fact]
    public async Task MonsterEntersVisibilityDuringAnUninterruptedWalk_WithNoRetarget_IsSentOnceInRange()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var connectTask = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var serverClient = await listener.AcceptTcpClientAsync();
        await connectTask;
        await using var clientStream = client.GetStream();
        var connector = new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf"));
        var allocator = new WorldActorIdAllocator();
        var registry = new WorldMapRegistry([], [], scripts: null, allocator: allocator);
        var spawn = new MobSpawnDefinition(GeneratedMobs.GPoring, "aoi-map", 1, 5000, 0, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 0));
        var monsters = new MonsterRegistry([spawn], allocator.Allocate, new FixedCellSelector(25, 5), TimeProvider.System);
        var monsterActorId = monsters.AllInstances[0].ActorId;
        var clock = new Athena.Net.MapServer.Tests.Testing.ControllableTimeProvider();
        // Step 6 cutover: SendVisibleMonsterActorsAsync requires a non-null combatState too - see
        // this file's own sibling fixture above for the identical reasoning.
        var aoiEpoch = WorldSimulationEpoch.NewEpoch();
        var aoiCombatState = new MonsterCombatStateStore();
        aoiCombatState.Register(monsters.AllInstances[0].Map, aoiEpoch, monsters.AllInstances[0].ActorId, new WorldMonsterIncarnationId(monsters.AllInstances[0].IncarnationId.Value), monsters.AllInstances[0].Spawn.Mob.MaxHp);
        var aoiProjections = WorldMonsterProjectionTestHelper.SeedProjection(monsters.AllInstances[0].Map, aoiEpoch, aoiCombatState, monsters.AllInstances);
        await using var session = new MapClientSession(
            1, serverClient, connector, iroAuthenticated: true, mapName: "aoi-map", x: 0, y: 0,
            monsterProjections: aoiProjections, combatState: aoiCombatState, timeProvider: clock);
        var runTask = session.RunAsync(CancellationToken.None);

        await clientStream.WriteAsync(BuildMovementRequest(30, 0));
        var movement = new byte[12];
        await clientStream.ReadExactlyAsync(movement);
        Assert.Equal((short)0x0087, BinaryPrimitives.ReadInt16LittleEndian(movement));

        for (var x = 1; x <= 10; x++)
        {
            var generation = clock.RegistrationGeneration;
            await clock.AdvanceAsync(TimeSpan.FromMilliseconds(150));
            await clock.WaitForRegistrationAfterAsync(generation).WaitAsync(TimeSpan.FromSeconds(5));
        }
        await clientStream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var pingReply = new byte[2];
        await clientStream.ReadExactlyAsync(pingReply);
        Assert.Equal((short)0x0b1d, BinaryPrimitives.ReadInt16LittleEndian(pingReply));

        var generationAtBoundary = clock.RegistrationGeneration;
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(150));
        await clock.WaitForRegistrationAfterAsync(generationAtBoundary).WaitAsync(TimeSpan.FromSeconds(5));

        var sawMonsterActor = false;
        for (var i = 0; i < 3 && !sawMonsterActor; i++)
        {
            var packet = await ReadDynamicOrFixed(clientStream);
            var opcode = BinaryPrimitives.ReadInt16LittleEndian(packet);
            Assert.NotEqual((short)0x0091, opcode);
            if (opcode == (short)0x09ff && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(5)) == monsterActorId)
                sawMonsterActor = true;
        }
        Assert.True(sawMonsterActor);

        client.Close();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();
    }

    // Priority 2, dedup: once an actor has been announced, further movement passes while it remains
    // in range must never re-announce it (no duplicate 0x09FF for the same ActorId) - proves
    // RefreshVisibleWorldActorsAsync's per-pass call composes correctly with the existing
    // _visibleActorIds dedup set rather than fighting it.
    [Fact]
    public async Task WarpAlreadyAnnounced_StaysInRangeAcrossFurtherPasses_IsNeverAnnouncedTwice()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var connectTask = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var serverClient = await listener.AcceptTcpClientAsync();
        await connectTask;
        await using var clientStream = client.GetStream();
        var connector = new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf"));
        var warp = new WarpDefinition("aoi-warp", "aoi-map", 25, 5, 1, 1, "other-map", 1, 1, true, "test", 1);
        var registry = new WorldMapRegistry([warp]);
        var clock = new Athena.Net.MapServer.Tests.Testing.ControllableTimeProvider();
        // Start already within range (x=15, distance max(10,5)=10<=14) so the warp is announced at
        // click time itself - the earliest possible announcement - then walk several more cells
        // while remaining in range throughout.
        await using var session = new MapClientSession(
            1, serverClient, connector, iroAuthenticated: true, mapName: "aoi-map", x: 15, y: 0,
            worldMapRegistry: registry, timeProvider: clock);
        var runTask = session.RunAsync(CancellationToken.None);

        await clientStream.WriteAsync(BuildMovementRequest(20, 0));
        var movement = new byte[12];
        await clientStream.ReadExactlyAsync(movement);
        Assert.Equal((short)0x0087, BinaryPrimitives.ReadInt16LittleEndian(movement));
        var firstActor = await ReadUntilOpcode(clientStream, 0x09ff);
        Assert.Equal((short)0x09ff, BinaryPrimitives.ReadInt16LittleEndian(firstActor));

        // Drive several further movement passes while the warp remains in range - never a second
        // 0x09FF for it. Each cell boundary is drained via a ping round-trip; only a 0x0091 would be
        // a hard failure (out of scope here), and any 0x09FF observed at all is itself the failure
        // this test exists to catch.
        for (var step = 0; step < 4; step++)
        {
            var generation = clock.RegistrationGeneration;
            await clock.AdvanceAsync(TimeSpan.FromMilliseconds(150));
            await clock.WaitForRegistrationAfterAsync(generation).WaitAsync(TimeSpan.FromSeconds(5));

            await clientStream.WriteAsync(new byte[] { 0x1c, 0x0b });
            while (true)
            {
                var header = new byte[2];
                await clientStream.ReadExactlyAsync(header);
                var opcode = BinaryPrimitives.ReadInt16LittleEndian(header);
                if (opcode == 0x0b1d) break;
                Assert.NotEqual(0x09ff, opcode); // No duplicate announcement.
                var lengthBytes = new byte[2];
                await clientStream.ReadExactlyAsync(lengthBytes);
                var length = BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes);
                await clientStream.ReadExactlyAsync(new byte[length - 4]);
            }
        }

        client.Close();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();
    }

    // Priority 2, no-stale-old-map: once a 0x0091 map transition has actually happened during a
    // movement pass, no further warp/monster actor packet for the OLD map may follow from that same
    // pass - proves RefreshVisibleWorldActorsAsync runs BEFORE arrival execution, never after.
    // Route: walking directly onto a warp's own trigger cell (so IntersectsWarp=true and the warp
    // fires on arrival), with a SECOND, different warp elsewhere on the OLD map that would otherwise
    // be newly in range of the arrival cell - it must never be announced once the map has changed.
    [Fact]
    public async Task MapTransitionDuringAMovementPass_NeverFollowedByAStaleOldMapActorPacket()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var connectTask = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var serverClient = await listener.AcceptTcpClientAsync();
        await connectTask;
        await using var clientStream = client.GetStream();
        var connector = new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf"));
        // The triggering warp sits directly on the walked route at (5,0). A SECOND warp on the same
        // OLD map, far enough from click-time (0,0) to be out of range but within range of (5,0) -
        // the arrival cell - must never be announced once SendSameServerWarpAsync has already
        // switched _mapName to "other-map".
        var triggerWarp = new WarpDefinition("trigger-warp", "old-map", 5, 0, 0, 0, "other-map", 1, 1, true, "test", 1);
        var staleWarp = new WarpDefinition("stale-warp", "old-map", 10, 8, 1, 1, "yet-another-map", 1, 1, true, "test", 1);
        var registry = new WorldMapRegistry([triggerWarp, staleWarp]);
        await using var session = new MapClientSession(
            1, serverClient, connector, iroAuthenticated: true, mapName: "old-map", x: 0, y: 0,
            worldMapRegistry: registry);
        var runTask = session.RunAsync(CancellationToken.None);

        await clientStream.WriteAsync(BuildMovementRequest(5, 0));
        var movement = new byte[12];
        await clientStream.ReadExactlyAsync(movement);
        Assert.Equal((short)0x0087, BinaryPrimitives.ReadInt16LittleEndian(movement));

        // Drain every packet until (and including) the 0x0091 map-change - any 0x09FF observed
        // along the way must belong to the triggering warp itself (already-visible at click time is
        // not the case here; this route never comes within 14 of staleWarp's own (10,8) from (0,0),
        // distance max(10,8)=10, which IS within range - so staleWarp's own visibility at CLICK TIME
        // is legitimate and expected; the invariant under test is specifically that no FURTHER actor
        // packet for the OLD map follows the 0x0091 itself).
        while (true)
        {
            var header = new byte[4];
            await clientStream.ReadExactlyAsync(header);
            var opcode = BinaryPrimitives.ReadInt16LittleEndian(header);
            if (opcode == 0x0091)
            {
                await clientStream.ReadExactlyAsync(new byte[18]); // Rest of the fixed 22-byte packet.
                break;
            }
            var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
            await clientStream.ReadExactlyAsync(new byte[length - 4]);
        }
        Assert.Equal("other-map", session.CurrentMapName);

        // No further packet may reference the OLD map's stale warp - synchronize with a ping and
        // confirm nothing but the pong arrives.
        await clientStream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var pingReply = new byte[2];
        await clientStream.ReadExactlyAsync(pingReply);
        Assert.Equal((short)0x0b1d, BinaryPrimitives.ReadInt16LittleEndian(pingReply));

        client.Close();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();
    }

    private sealed class FixedCellSelector(ushort x, ushort y) : IMobSpawnCellSelector
    {
        public bool TrySelectCell(MobSpawnDefinition spawn, int index, out MobPosition position)
        {
            position = new MobPosition(x, y);
            return true;
        }
    }

    private static WorldEntityDefinition BuildTouchEntity(string map, ushort x, ushort y, ushort radius, out string actorName)
    {
        actorName = "TouchNpc#test";
        var actor = new WorldActorComponent(actorName, map, x, y, 0, 111);
        var script = new ScriptBehaviorDefinition("OnTouch", map, x, y, radius, radius, SourceParsed: true, RuntimeExecutable: true, RequiredCapabilities: [], NormalizedSource: "");
        return new WorldEntityDefinition(1, $"npc:{map}:{actorName}", "npc", actor, [], [script], new WorldSourceInfo("test", "unknown", "test", 1));
    }

    private sealed class NoOpScript : INpcScript
    {
        public Task ExecuteAsync(ScriptContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class WarpingScript(string map, ushort x, ushort y) : INpcScript
    {
        public Task ExecuteAsync(ScriptContext context, CancellationToken cancellationToken) => context.WarpAsync(map, x, y, cancellationToken);
    }

    private static async Task<byte[]> ReadDynamicOrFixed(Stream stream)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header);
        var opcode = BinaryPrimitives.ReadInt16LittleEndian(header);
        // 0x0091 (map change, 22 bytes) and 0x0087 (movement response, 12 bytes) are both fixed-
        // size packets with no embedded length prefix at bytes[2..4].
        if (opcode == 0x0091 || opcode == 0x0087)
        {
            var fixedLength = opcode == 0x0091 ? 22 : 12;
            var rest = new byte[fixedLength - 4];
            await stream.ReadExactlyAsync(rest);
            return [.. header, .. rest];
        }
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
        var body = new byte[length - 4];
        await stream.ReadExactlyAsync(body);
        return [.. header, .. body];
    }

    // Drains and discards packets until one with the given opcode is found (or a bounded number of
    // packets have been skipped) - used where an actor-visibility refresh (0x09FF, now correctly
    // unconditional regardless of route-trigger metadata) may legitimately appear ahead of an
    // expected fixed-shape packet like 0x0087/0x0091, in an order this project's own AOI/dedup
    // logic does not guarantee is fixed (e.g. zero, one, or more newly-visible actors).
    private static async Task<byte[]> ReadUntilOpcode(Stream stream, short expectedOpcode, int maxPacketsToSkip = 10)
    {
        for (var i = 0; i <= maxPacketsToSkip; i++)
        {
            var packet = await ReadDynamicOrFixed(stream);
            if (BinaryPrimitives.ReadInt16LittleEndian(packet) == expectedOpcode) return packet;
        }
        throw new InvalidOperationException($"Expected opcode 0x{expectedOpcode:X4} was not observed within {maxPacketsToSkip} packets.");
    }

    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await stream.ReadExactlyAsync(buffer, cts.Token);
        return buffer;
    }

    [Fact]
    public async Task MovementIntoTutorialDoor_SendsMoveThenMapChangeAndContinuesOnDestination()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var connectTask = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var serverClient = await listener.AcceptTcpClientAsync();
        await connectTask;
        await using var clientStream = client.GetStream();
        var connector = new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf"));
        var persistence = new RecordingPositionPersistence();
        await using var session = new MapClientSession(
            1,
            serverClient,
            connector,
            iroAuthenticated: true,
            mapName: "iz_int03",
            x: 22,
            y: 31,
            positionPersistence: persistence);
        var runTask = session.RunAsync(CancellationToken.None);

        // The requested target lies beyond the real door area. The direct grid route
        // first enters it at (26,30), so the client need not click the portal tile.
        await clientStream.WriteAsync(BuildMovementRequest(29, 29));

        var movement = new byte[12];
        await clientStream.ReadExactlyAsync(movement);
        Assert.Equal((short)0x0087, BinaryPrimitives.ReadInt16LittleEndian(movement));
        var movementCoordinates = DecodeMovement(movement.AsSpan(6, 6));
        Assert.Equal(((ushort)22, (ushort)31, (ushort)26, (ushort)30), movementCoordinates);

        // A visibility refresh (0x09FF, now correctly unconditional regardless of the pending
        // warp arrival) may legitimately appear here for any NPC/warp actor near the click-time
        // cell, before the eventual 0x0091 map-change - drain past it.
        var mapChange = await ReadUntilOpcode(clientStream, 0x0091);
        Assert.Equal((ushort)51, BinaryPrimitives.ReadUInt16LittleEndian(mapChange.AsSpan(18)));
        Assert.Equal((ushort)30, BinaryPrimitives.ReadUInt16LittleEndian(mapChange.AsSpan(20)));
        Assert.Equal("iz_int03", session.CurrentMapName);
        Assert.Equal((ushort)51, session.CurrentX);
        Assert.Equal((ushort)30, session.CurrentY);

        // SendSameServerWarpAsync writes the 0x0091 map-change packet BEFORE awaiting
        // PersistPositionIfDirtyAsync, so the client can legitimately observe the packet above
        // before the save has run - await the explicit completion signal (not the unsynchronized
        // Saves list, which SavePositionAsync may still be concurrently appending to) rather than
        // asserting on it immediately.
        var persisted = await persistence.Saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("iz_int03", persisted.MapName);
        Assert.Equal((ushort)51, persisted.X);
        Assert.Equal((ushort)30, persisted.Y);
        Assert.False(runTask.IsCompleted);

        client.Close();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();
    }

    // Live stock-iRO acceptance (PR #20) issue 3: prontera-walking.pcapng frame 3246 proves the
    // real field->Prontera door lands the client at (156,34), diverging from pinned
    // legacy/rathena/npc/re/warps/fields/prontera_fild.txt:105's own computed (156,26) - see
    // IroWireCompatibility's own doc comment. This end-to-end test proves the REAL generated
    // prt_fild08d warp trigger (WorldMapRegistry.Tutorial, not a hand-built fixture), when actually
    // walked into via the normal movement path, produces a 0x0091 map-change AND a persisted
    // position at the capture-verified (156,34) - never the pinned (156,26)
    // WorldMapRegistryTests.TravelCorridorWarps_MatchGeneratedPinnedSourceValues separately (and
    // correctly) asserts as the untouched GENERATED value.
    [Fact]
    public async Task MovementIntoPrtFild08dPronteraDoor_LandsAtCaptureVerified156_34_NeverPinned156_26()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var connectTask = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var serverClient = await listener.AcceptTcpClientAsync();
        await connectTask;
        await using var clientStream = client.GetStream();
        var connector = new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf"));
        var persistence = new RecordingPositionPersistence();
        // prt_fild08d,170,378,0 warp prtf004_d 3,2,prontera,156,26 (pinned source) - starting
        // adjacent to the door's own center so a short, direct movement request reaches it.
        await using var session = new MapClientSession(
            1,
            serverClient,
            connector,
            iroAuthenticated: true,
            mapName: "prt_fild08d",
            x: 170,
            y: 375,
            positionPersistence: persistence);
        var runTask = session.RunAsync(CancellationToken.None);

        await clientStream.WriteAsync(BuildMovementRequest(170, 378));

        var movement = new byte[12];
        await clientStream.ReadExactlyAsync(movement);
        Assert.Equal((short)0x0087, BinaryPrimitives.ReadInt16LittleEndian(movement));

        // A visibility refresh (0x09FF) may legitimately appear here now that it is unconditional
        // - drain past it to the eventual 0x0091 map-change.
        var mapChange = await ReadUntilOpcode(clientStream, 0x0091);
        Assert.Equal((ushort)156, BinaryPrimitives.ReadUInt16LittleEndian(mapChange.AsSpan(18)));
        Assert.Equal((ushort)34, BinaryPrimitives.ReadUInt16LittleEndian(mapChange.AsSpan(20))); // NEVER 26.
        Assert.Equal("prontera", session.CurrentMapName);
        Assert.Equal((ushort)156, session.CurrentX);
        Assert.Equal((ushort)34, session.CurrentY);

        var persisted = await persistence.Saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("prontera", persisted.MapName);
        Assert.Equal((ushort)156, persisted.X);
        Assert.Equal((ushort)34, persisted.Y); // Persisted destination must match the capture too.

        client.Close();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();
    }

    // Regression for requirement 7 of the mid-walk-retarget fix: a retarget that steers AWAY from
    // a warp cell the ORIGINAL route would have crossed must fully replace the pending arrival - no
    // stale warp may fire just because the OLD path (computed at click time) once intersected one.
    // Pinned unit_walktoxy_timer re-evaluates npc_touch_area_allnpc/warp checks fresh at every cell
    // it actually reaches (unit.cpp:684-699), never against a route that was abandoned mid-walk.
    [Fact]
    public async Task RetargetAwayFromADoor_MidWalk_NeverWarps_ReplacesThePendingArrival()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var connectTask = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var serverClient = await listener.AcceptTcpClientAsync();
        await connectTask;
        await using var clientStream = client.GetStream();
        var connector = new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf"));
        var persistence = new RecordingPositionPersistence();
        // ControllableTimeProvider (not World.FakeTimeProvider, which only overrides GetUtcNow) is
        // required here: RunMovementLoopAsync schedules its next per-cell wake via
        // Task.Delay(delay, TimeProvider, ...), which calls TimeProvider.CreateTimer - a provider
        // that doesn't override CreateTimer falls back to real wall-clock timers regardless of what
        // GetUtcNow() reports, so a plain FakeTimeProvider.Advance would leave the walk stuck after
        // only whatever cell(s) real background scheduling happened to race through.
        var clock = new Athena.Net.MapServer.Tests.Testing.ControllableTimeProvider();
        await using var session = new MapClientSession(
            1, serverClient, connector, iroAuthenticated: true, mapName: "iz_int03", x: 22, y: 31,
            positionPersistence: persistence, timeProvider: clock);
        var runTask = session.RunAsync(CancellationToken.None);

        // Same route toward the door as the sibling test above - click (29,29), whose direct grid
        // route crosses the door at (26,30) after 4 cells (default 150ms/cell = 600ms total to
        // reach the door cell).
        await clientStream.WriteAsync(BuildMovementRequest(29, 29));
        var movement = new byte[12];
        await clientStream.ReadExactlyAsync(movement);
        Assert.Equal(((ushort)22, (ushort)31, (ushort)26, (ushort)30), DecodeMovement(movement.AsSpan(6, 6)));

        // A visibility refresh (0x09FF, now correctly unconditional regardless of the pending
        // warp arrival) may follow the 0x0087 above for any NPC/warp actor near the click-time
        // cell - synchronize past it with a ping round-trip before proceeding, so it cannot
        // linger in the stream and desynchronize a later fixed-size read.
        await clientStream.WriteAsync(new byte[] { 0x1c, 0x0b });
        while (true)
        {
            var header = new byte[2];
            await clientStream.ReadExactlyAsync(header);
            if (BinaryPrimitives.ReadInt16LittleEndian(header) == 0x0b1d) break;
            var lengthBytes = new byte[2];
            await clientStream.ReadExactlyAsync(lengthBytes);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes);
            await clientStream.ReadExactlyAsync(new byte[length - 4]);
        }

        // Retarget mid-walk, before the door cell is reached, toward a destination that never
        // crosses it - almost straight down from the second path cell, away from the door entirely.
        // Drive the clock to that FIRST cell boundary deterministically: capture the registration
        // generation before advancing so we can prove the loop has already rearmed its next timer
        // (i.e. the previous cell's callback, and everything synchronous inside it - including
        // ConsumePendingRetarget - has fully run) before trusting anything that happened as a
        // result. AdvanceAsync itself only guarantees the due callback was invoked, NOT that the
        // async continuations chained after that invocation (the rest of ProcessDueMovementAsync,
        // and the loop's own re-registration of the following step's timer) have completed yet.
        var generationBeforeFirstBoundary = clock.RegistrationGeneration;
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(150)); // Exactly one cell - still far from the door.
        await clock.WaitForRegistrationAfterAsync(generationBeforeFirstBoundary).WaitAsync(TimeSpan.FromSeconds(5));

        // Now mid-walk (one cell in), record the retarget.
        await clientStream.WriteAsync(BuildMovementRequest(22, 40));

        // Synchronize on the retarget having actually been recorded on CharacterMovementState (see
        // MapClientSessionMovementRetargetTests.SyncAsync's own doc comment for why a bare
        // WriteAsync alone does not guarantee the packet was processed yet) before driving the clock
        // toward the NEXT boundary, where the retarget is expected to take effect.
        await clientStream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var retargetRecordedPing = new byte[2];
        await clientStream.ReadExactlyAsync(retargetRecordedPing);
        Assert.Equal((short)0x0b1d, BinaryPrimitives.ReadInt16LittleEndian(retargetRecordedPing));

        // Drive the clock to the SECOND cell boundary - the first one reached AFTER the retarget was
        // recorded, and per this fix's own contract (CharacterMovementState.AdvanceTo's early-stop
        // and ConsumePendingRetarget), exactly where the retarget must be applied: neither before
        // (the in-flight step's remaining time must be honored) nor deferred past it (no silently
        // continuing along the stale original path for extra cells). The step in flight when the
        // retarget was recorded is the ORIGINAL route's (23,31)->(24,30) - a DIAGONAL step
        // (150ms*14/10=210ms, per the "Movement retarget deferred" diagnostic's own
        // currentStepDueAt=360ms above: 150ms already elapsed + this 210ms step), not another plain
        // 150ms orthogonal step - advancing only 150ms here would fall short of that deadline.
        var generationBeforeRetargetBoundary = clock.RegistrationGeneration;
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(210));
        await clock.WaitForRegistrationAfterAsync(generationBeforeRetargetBoundary).WaitAsync(TimeSpan.FromSeconds(5));

        // The retarget must have applied exactly here: a fresh 0x0087 for the replacement path
        // appears now, sourced from the cell just reached - drain forward from the stream (bounded
        // by a ping round-trip, so this loop cannot hang if the response never arrives) until we
        // find it. Visibility-refresh packets for newly-visible NPC/warp actors near the replacement
        // route may legitimately interleave, but a 0x0091 map-change must never appear - that would
        // mean the original door's stale pending arrival survived the retarget.
        await clientStream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var sawRetargetResponse = false;
        while (true)
        {
            var header = new byte[2];
            await clientStream.ReadExactlyAsync(header);
            var opcode = BinaryPrimitives.ReadInt16LittleEndian(header);
            if (opcode == 0x0b1d) break; // The ping reply itself - stop draining.
            Assert.NotEqual(0x0091, opcode); // No stale map-change may ever appear.
            if (opcode == 0x0087)
            {
                sawRetargetResponse = true;
                await clientStream.ReadExactlyAsync(new byte[10]); // Rest of the fixed 12-byte packet.
            }
            else
            {
                // Every other packet type this path can emit (NPC/warp/monster actor entries) is
                // variable-length with its own length prefix as the next 2 bytes.
                var lengthBytes = new byte[2];
                await clientStream.ReadExactlyAsync(lengthBytes);
                var length = BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes);
                await clientStream.ReadExactlyAsync(new byte[length - 4]);
            }
        }
        Assert.True(sawRetargetResponse);

        // Drive the clock the rest of the way to the replacement path's own destination, one
        // registration-synchronized boundary at a time, so every intermediate cell (each of which
        // re-evaluates warp/OnTouch fresh, per requirement 7) is proven reached deterministically
        // rather than assumed via one large blind jump or a live (and racy - CurrentX/Y call
        // SyncPositionToNow, which mutates state outside _movementGate) poll of session state.
        //
        // The retarget is applied at (24,30) - the cell the ORIGINAL route's diagonal step actually
        // landed on - by recomputing GridLineTraversal from there to (22,40):
        // [(24,30),(24,31),(24,32),(23,33),(23,34),(23,35),(23,36),(23,37),(22,38),(22,39),(22,40)],
        // verified by hand via GridLineTraversal.Enumerate's own Bresenham stepping for this exact
        // (dx=-2,dy=10) pair: steps 1-2 orthogonal(150ms), step 3 diagonal(210ms), steps 4-7
        // orthogonal(150ms), step 8 diagonal(210ms), steps 9-10 orthogonal(150ms). None of these 10
        // steps were consumed by the boundary AdvanceAsync above (that call only reached the
        // boundary AT (24,30) itself, where StartWalk installs this replacement path fresh) - all 10
        // remain to drive here.
        // The LAST step is driven separately below: once it completes, IsMoving becomes false and
        // NextStepDueAt goes back to null, so RunMovementLoopAsync falls back to
        // _movementSignal.WaitAsync instead of another Task.Delay/CreateTimer - no further
        // registration bump ever comes for this walk, so waiting on one after that final advance
        // would hang forever.
        int[] intermediateStepMs = [150, 150, 210, 150, 150, 150, 150, 210, 150];
        foreach (var stepMs in intermediateStepMs)
        {
            var before = clock.RegistrationGeneration;
            await clock.AdvanceAsync(TimeSpan.FromMilliseconds(stepMs));
            await clock.WaitForRegistrationAfterAsync(before).WaitAsync(TimeSpan.FromSeconds(5));
        }

        // The replacement route has no warp/script arrival of its own (ResolveMovementTarget found
        // none along it), so reaching its final cell sends nothing further on the wire - per
        // ProcessDueMovementAsync above, both appliedRetarget and arrival are null for every one of
        // these ordinary intermediate/final crossings. Confirm that directly: a ping sent now must
        // get an immediate reply with no map-change (or anything else) ahead of it. The ping
        // round-trip itself is the synchronization here (bounded, unlike a registration wait that
        // would never resolve after the walk's last step).
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(150)); // Final step: reaches (22,40).
        await clientStream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var finalPing = new byte[2];
        await clientStream.ReadExactlyAsync(finalPing);
        Assert.Equal((short)0x0b1d, BinaryPrimitives.ReadInt16LittleEndian(finalPing));

        Assert.Equal("iz_int03", session.CurrentMapName); // Never warped.
        Assert.Equal((ushort)22, session.CurrentX);
        Assert.Equal((ushort)40, session.CurrentY); // Reached the REPLACEMENT destination.

        client.Close();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();
    }

    private sealed class RecordingPositionPersistence : ICharacterPositionPersistence
    {
        public TaskCompletionSource<(uint AccountId, uint CharId, string MapName, ushort X, ushort Y)> Saved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> SavePositionAsync(
            uint accountId,
            uint charId,
            string mapName,
            ushort x,
            ushort y,
            CancellationToken cancellationToken)
        {
            Saved.TrySetResult((accountId, charId, mapName, x, y));
            return Task.FromResult(true);
        }
    }

    private static (ushort FromX, ushort FromY, ushort ToX, ushort ToY) DecodeMovement(
        ReadOnlySpan<byte> coordinates)
    {
        var fromX = (ushort)((coordinates[0] << 2) | (coordinates[1] >> 6));
        var fromY = (ushort)(((coordinates[1] & 0x3f) << 4) | (coordinates[2] >> 4));
        var toX = (ushort)(((coordinates[2] & 0x0f) << 6) | (coordinates[3] >> 2));
        var toY = (ushort)(((coordinates[3] & 0x03) << 8) | coordinates[4]);
        return (fromX, fromY, toX, toY);
    }

    private static byte[] BuildMovementRequest(ushort x, ushort y)
    {
        var packet = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(packet, 0x035f);
        packet[2] = (byte)(x >> 2);
        packet[3] = (byte)((x << 6) | ((y >> 4) & 0x3f));
        packet[4] = (byte)(y << 4);
        packet[5] = 0xab;
        return packet;
    }
}
