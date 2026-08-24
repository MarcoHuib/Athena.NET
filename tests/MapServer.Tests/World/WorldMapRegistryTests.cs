using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.GeneratedScripts;

namespace Athena.Net.MapServer.Tests.World;

public sealed class WorldMapRegistryTests
{
    [Fact]
    public void CustomScriptRegistration_RejectsDuplicateUnlessOverrideIsExplicit()
    {
        var generated = Assert.Single(GeneratedScriptRegistry.Entities, entity => entity.Id == "npc:iz_int:wounded swordsman#intro_npc02_iz_int");
        var registration = new GeneratedScriptRegistration(generated, "OnClick", static () => new FixtureScript());
        var builder = new NpcScriptRegistryBuilder().AddGenerated([registration]);
        Assert.Throws<InvalidOperationException>(() => builder.AddCustom(registration));
        var registry = builder.AddCustom(registration, explicitlyOverrideGenerated: true).Build();
        Assert.True(registry.TryCreate(generated.Id, "OnClick", out var script));
        Assert.IsType<FixtureScript>(script);
    }

    [Theory]
    [InlineData("iz_int01")]
    [InlineData("iz_int02")]
    [InlineData("iz_int03")]
    [InlineData("iz_int04")]
    public void Tutorial_ForwardDoorMatchesAllRealInstanceVariants(string mapName)
    {
        var found = WorldMapRegistry.Tutorial.TryFindWarp(mapName, 26, 30, out var warp);

        Assert.True(found);
        Assert.Equal(mapName, warp.DestinationMap);
        Assert.Equal((ushort)51, warp.DestinationX);
        Assert.Equal((ushort)30, warp.DestinationY);
    }

    [Fact]
    public void Tutorial_MatchesInclusiveRectangularArea()
    {
        Assert.True(WorldMapRegistry.Tutorial.TryFindWarp("iz_int01", 26, 29, out _));
        Assert.True(WorldMapRegistry.Tutorial.TryFindWarp("iz_int01", 28, 31, out _));
    }

    [Theory]
    [InlineData(26, 30)]
    [InlineData(28, 30)]
    [InlineData(27, 29)]
    [InlineData(27, 31)]
    public void Tutorial_MatchesEveryBoundary(ushort x, ushort y)
    {
        Assert.True(WorldMapRegistry.Tutorial.TryFindWarp("iz_int01", x, y, out _));
    }

    [Theory]
    [InlineData(25, 30)]
    [InlineData(29, 30)]
    [InlineData(27, 28)]
    [InlineData(27, 32)]
    public void Tutorial_RejectsOneTileOutsideEveryBoundary(ushort x, ushort y)
    {
        Assert.False(WorldMapRegistry.Tutorial.TryFindWarp("iz_int01", x, y, out _));
    }

    [Fact]
    public void Tutorial_TileOutsideAreaDoesNotMatch()
    {
        Assert.False(WorldMapRegistry.Tutorial.TryFindWarp("iz_int01", 25, 30, out _));
    }

    [Fact]
    public void Tutorial_SameCoordinateOnAnotherMapDoesNotMatch()
    {
        Assert.False(WorldMapRegistry.Tutorial.TryFindWarp("prontera", 26, 30, out _));
    }

    [Fact]
    public void Tutorial_ExactTargetOnWarpIntersects()
    {
        var found = WorldMapRegistry.Tutorial.TryFindFirstWarpAlongRoute(
            "iz_int01", 29, 30, 28, 30, out var intersection);

        Assert.True(found);
        Assert.Equal((ushort)28, intersection.X);
        Assert.Equal((ushort)30, intersection.Y);
    }

    [Fact]
    public void Tutorial_TargetBeyondWarpStillIntersectsFirstWarpCell()
    {
        var found = WorldMapRegistry.Tutorial.TryFindFirstWarpAlongRoute(
            "iz_int01", 22, 31, 29, 29, out var intersection);

        Assert.True(found);
        Assert.Equal((ushort)26, intersection.X);
        Assert.Equal((ushort)30, intersection.Y);
    }

    [Fact]
    public void Tutorial_RouteBesideWarpDoesNotIntersect()
    {
        Assert.False(WorldMapRegistry.Tutorial.TryFindFirstWarpAlongRoute(
            "iz_int01", 22, 28, 32, 28, out _));
    }

    [Fact]
    public void Route_SelectsFirstIntersectedWarpRatherThanRegistryOrder()
    {
        var later = new WarpDefinition("later", "test", 8, 5, 0, 0, "later", 1, 1, true, "test", 1);
        var earlier = new WarpDefinition("earlier", "test", 3, 5, 0, 0, "earlier", 1, 1, true, "test", 2);
        var registry = new WorldMapRegistry(new[] { later, earlier });

        var found = registry.TryFindFirstWarpAlongRoute("test", 0, 5, 10, 5, out var intersection);

        Assert.True(found);
        Assert.Same(earlier, intersection.Warp);
        Assert.Equal((ushort)3, intersection.X);
    }

    [Fact]
    public void EntityOverlay_WinsAndDoesNotDuplicateLegacyTriggerOrActor()
    {
        var root = Path.Combine(Path.GetTempPath(), $"athena-overlay-{Guid.NewGuid():N}");
        var entities = Path.Combine(root, "entities", "test");
        Directory.CreateDirectory(entities);
        try
        {
            File.WriteAllText(Path.Combine(entities, "door.json"), """
                {"SchemaVersion":1,"Id":"warp:test:door","Kind":"Warp","Actor":{"Name":"#door","Map":"test","X":5,"Y":5,"Direction":0,"Class":45},"Triggers":[{"Type":"OnTouch","Map":"test","X":5,"Y":5,"RadiusX":0,"RadiusY":0,"Actions":[{"Type":"Warp","Map":"new","X":9,"Y":9}]}],"Source":{"Repository":"test","Commit":"x","File":"test.txt","Line":1}}
                """);
            File.WriteAllText(Path.Combine(root, "warps.json"), """
                {"StaticWarps":[{"Name":"#door","SourceMap":"test","CenterX":5,"CenterY":5,"RadiusX":0,"RadiusY":0,"DestinationMap":"old","DestinationX":1,"DestinationY":1,"HasWarpActor":true,"SourceFile":"old.txt","SourceLine":1}],"DynamicWarps":[]}
                """);
            var registry = WorldMapRegistry.Load(Path.Combine(root, "entities"), Path.Combine(root, "warps.json"));
            Assert.True(registry.TryFindWarp("test", 5, 5, out var warp));
            Assert.Equal("new", warp.DestinationMap);
            Assert.Single(registry.GetVisibleWarpActors("test", 5, 5), actor => actor.Name == "#door");
            Assert.Equal(1, registry.StaticWarpCount);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Tutorial_ShipOut03_LoadsOrderedSavePointThenWarpActions()
    {
        Assert.True(WorldMapRegistry.Tutorial.TryFindWarp("iz_int03", 56, 15, out var warp));
        Assert.Collection(
            warp.OrderedActions,
            action => Assert.Equal(new SetSavePointAction("int_land03", 77, 101), action),
            action => Assert.Equal(new WarpAction("int_land03", 85, 107), action));
    }

    [Fact]
    public void Tutorial_BaseMapEntitiesOverrideLegacyAndShipOutIsExecutable()
    {
        Assert.True(WorldMapRegistry.Tutorial.TryFindWarp("iz_int", 27, 30, out var roomOut));
        Assert.Equal(("iz_int", (ushort)51, (ushort)30), (roomOut.DestinationMap, roomOut.DestinationX, roomOut.DestinationY));
        Assert.True(WorldMapRegistry.Tutorial.TryFindWarp("iz_int", 47, 30, out var roomIn));
        Assert.Equal(("iz_int", (ushort)22, (ushort)30), (roomIn.DestinationMap, roomIn.DestinationX, roomIn.DestinationY));
        Assert.True(WorldMapRegistry.Tutorial.TryFindWarp("iz_int", 56, 15, out var ship));
        Assert.Collection(
            ship.OrderedActions,
            action => Assert.Equal(new SetSavePointAction("int_land", 77, 101), action),
            action => Assert.Equal(new WarpAction("int_land", 85, 107), action));
        Assert.Single(WorldMapRegistry.Tutorial.GetVisibleWarpActors("iz_int", 56, 15), actor => actor.Name == "#ship_out");
    }

    [Fact]
    public void Tutorial_ShipOut04_ReplacesLegacyActorAndIsExecutable()
    {
        Assert.True(WorldMapRegistry.Tutorial.TryFindWarp("iz_int04", 56, 15, out var warp));
        Assert.Collection(
            warp.OrderedActions,
            action => Assert.Equal(new SetSavePointAction("int_land04", 77, 101), action),
            action => Assert.Equal(new WarpAction("int_land04", 85, 107), action));
        Assert.Single(
            WorldMapRegistry.Tutorial.GetVisibleWarpActors("iz_int04", 56, 15),
            actor => actor.Name == "#ship_out04");
    }

    [Fact]
    public void IntroToIzlude_WorldEntityReplacesLegacyActorWithExecutableOnTouchScript()
    {
        var actor = Assert.Single(
            WorldMapRegistry.Tutorial.GetVisibleWarpActors("int_land", 49, 57),
            candidate => candidate.Name == "#intro_to_izlude");

        Assert.Equal(((ushort)49, (ushort)57, (byte)2, (byte)2),
            (actor.X, actor.Y, actor.RadiusX, actor.RadiusY));
        Assert.False(WorldMapRegistry.Tutorial.TryFindWarp("int_land", 49, 57, out _));
        var entity = WorldMapRegistry.Tutorial.EntitiesById["warp:int_land:intro_to_izlude"];
        Assert.Empty(entity.Triggers);
        var script = Assert.Single(entity.Scripts);
        Assert.True(script.SourceParsed);
        Assert.True(script.RuntimeExecutable);
        Assert.True(WorldMapRegistry.Tutorial.TryFindFirstScriptTouchEnterAlongRoute("int_land", 54, 64, 49, 57, out var entered));
        Assert.Same(entity, entered.Binding.Entity);
    }

    [Fact]
    public void DeveloperDialogueNpc_IsAbsentFromDefaultRegistry()
    {
        Assert.DoesNotContain("dev:int_land04:athena_test_npc", WorldMapRegistry.Tutorial.EntitiesById.Keys);
        Assert.DoesNotContain(WorldMapRegistry.Tutorial.GetVisibleWarpActors("int_land04", 50, 59), actor => actor.Name == "Athena Test NPC");
    }

    [Fact]
    public void IntroToIzludeDuplicate_IsExecutableOnTouchAndReplacesLegacyActor()
    {
        var entity = WorldMapRegistry.Tutorial.EntitiesById["warp:int_land04:intro_to_izlude_d"];
        Assert.Equal(new WorldActorComponent("#intro_to_izlude_d", "int_land04", 49, 57, 0, 45), entity.Actor);
        var script = Assert.Single(entity.Scripts);
        Assert.True(script.RuntimeExecutable);
        Assert.Equal("#intro_to_izlude", script.BaseNpcName);
        Assert.True(WorldMapRegistry.Tutorial.TryFindFirstScriptTouchEnterAlongRoute("int_land04", 54, 64, 49, 57, out var entered));
        Assert.Same(entity, entered.Binding.Entity);
        Assert.False(WorldMapRegistry.Tutorial.TryFindFirstScriptTouchEnterAlongRoute("int_land04", 50, 58, 49, 57, out _));
        Assert.Single(WorldMapRegistry.Tutorial.GetVisibleWarpActors("int_land04", 49, 57), actor => actor.Name == "#intro_to_izlude_d");
    }
}

file sealed class FixtureScript : INpcScript
{
    public Task ExecuteAsync(ScriptContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}
