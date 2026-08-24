using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.GeneratedScripts;

namespace Athena.Net.MapServer.Tests.World;

public sealed class WorldMapRegistryTests
{
    [Fact]
    public void DefaultWorld_IsTheIntentionalGeneratedVerticalSlice()
    {
        var registry = WorldMapRegistry.Tutorial;
        Assert.Equal(2, registry.StaticWarpCount);
        Assert.Equal(2, registry.EntityCount);
        Assert.Equal(0, registry.DynamicWarpActorCount);
        Assert.Equal(["npc:iz_int:wounded swordsman#intro_npc02_iz_int", "warp:int_land04:intro_to_izlude_d"], registry.EntitiesById.Keys.Order());
        Assert.DoesNotContain("dev:int_land04:athena_test_npc", registry.EntitiesById.Keys);
    }

    [Fact]
    public void GeneratedIzIntRoomWarps_MatchPinnedDefinitions()
    {
        Assert.True(WorldMapRegistry.Tutorial.TryFindWarp("iz_int", 26, 30, out var roomOut));
        Assert.Equal(("iz_int", (ushort)51, (ushort)30), (roomOut.DestinationMap, roomOut.DestinationX, roomOut.DestinationY));
        Assert.Equal(57, roomOut.SourceLine);
        Assert.True(WorldMapRegistry.Tutorial.TryFindWarp("iz_int", 48, 31, out var roomIn));
        Assert.Equal(("iz_int", (ushort)22, (ushort)30), (roomIn.DestinationMap, roomIn.DestinationX, roomIn.DestinationY));
        Assert.Equal(63, roomIn.SourceLine);
        Assert.False(WorldMapRegistry.Tutorial.TryFindWarp("iz_int03", 27, 30, out _));
    }

    [Fact]
    public void Route_SelectsFirstIntersectedWarpRatherThanRegistryOrder()
    {
        var later = new WarpDefinition("later", "test", 8, 5, 0, 0, "later", 1, 1, true, "test", 1);
        var earlier = new WarpDefinition("earlier", "test", 3, 5, 0, 0, "earlier", 1, 1, true, "test", 2);
        var registry = new WorldMapRegistry([later, earlier]);
        Assert.True(registry.TryFindFirstWarpAlongRoute("test", 0, 5, 10, 5, out var intersection));
        Assert.Same(earlier, intersection.Warp);
        Assert.Equal((ushort)3, intersection.X);
    }

    [Fact]
    public void IntroToIzludeDuplicate_IsGeneratedExecutableOnTouch()
    {
        var entity = WorldMapRegistry.Tutorial.EntitiesById["warp:int_land04:intro_to_izlude_d"];
        Assert.Equal(new WorldActorComponent("#intro_to_izlude_d", "int_land04", 49, 57, 0, 45), entity.Actor);
        Assert.True(WorldMapRegistry.Tutorial.TryFindFirstScriptTouchEnterAlongRoute("int_land04", 54, 64, 49, 57, out var entered));
        Assert.Same(entity, entered.Binding.Entity);
        Assert.Single(WorldMapRegistry.Tutorial.GetVisibleWarpActors("int_land04", 49, 57), actor => actor.Name == "#intro_to_izlude_d");
    }

    [Fact]
    public void WoundedSwordsman_IsGeneratedOrdinaryNpcAndDeveloperNpcIsAbsent()
    {
        var entity = WorldMapRegistry.Tutorial.EntitiesById["npc:iz_int:wounded swordsman#intro_npc02_iz_int"];
        Assert.Equal(new WorldActorComponent("Wounded Swordsman#intro_npc02_iz_int", "iz_int", 56, 32, 3, 688, 4), entity.Actor);
        Assert.DoesNotContain(WorldMapRegistry.Tutorial.GetVisibleWarpActors("int_land04", 50, 59), actor => actor.Name == "Athena Test NPC");
    }

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
}

file sealed class FixtureScript : INpcScript
{
    public Task ExecuteAsync(ScriptContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}
