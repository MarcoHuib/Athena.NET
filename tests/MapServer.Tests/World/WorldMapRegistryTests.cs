using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class WorldMapRegistryTests
{
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
        Assert.Equal(9, WorldMapRegistry.Tutorial.EntityCount);
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
        Assert.Equal(9, WorldMapRegistry.Tutorial.EntityCount);
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
    public void IntroToIzlude_WorldEntityReplacesLegacyActorWithoutExecutableTrigger()
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
        Assert.False(script.RuntimeExecutable);
        Assert.Equal(9, WorldMapRegistry.Tutorial.EntityCount);
        Assert.Equal(122, WorldMapRegistry.Tutorial.DynamicWarpActorCount);
    }

    [Fact]
    public void DeveloperDialogueNpc_LoadsThroughNormalRegistryAndIsInteractable()
    {
        var entity = WorldMapRegistry.Tutorial.EntitiesById["dev:int_land04:athena_test_npc"];
        Assert.Equal("DeveloperTestNpc", entity.Kind);
        Assert.Equal(new WorldActorComponent("Athena Test NPC", "int_land04", 55, 63, 5, 873), entity.Actor);
        Assert.Empty(entity.Triggers);

        var actor = Assert.Single(
            WorldMapRegistry.Tutorial.GetVisibleWarpActors("int_land04", 50, 59),
            candidate => candidate.Name == "Athena Test NPC");
        Assert.Equal((ushort)873, actor.SpriteClass);
        Assert.True(WorldMapRegistry.Tutorial.TryGetInteraction(actor.ActorId, "int_land04", out var boundEntity, out var script));
        Assert.Same(entity, boundEntity);
        Assert.True(script.RuntimeExecutable);
        Assert.Collection(script.Instructions!,
            instruction => Assert.Equal(new MessageInstruction("Quest test."), instruction),
            instruction =>
            {
                var select = Assert.IsType<SelectInstruction>(instruction);
                Assert.Equal(["Start test quest", "Check test quest", "Complete test quest"], select.Options.Select(option => option.Text));
                Assert.IsType<SetQuestInstruction>(select.Options[0].Instructions[0]);
                Assert.IsType<IfQuestStateInstruction>(select.Options[1].Instructions[0]);
                Assert.IsType<CompleteQuestInstruction>(select.Options[2].Instructions[0]);
            });
    }
}
