using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.GeneratedScripts;

namespace Athena.Net.MapServer.Tests.World;

public sealed class WorldRegistryBuilderTests
{
    private static NpcPlacement Placement(string id, string definitionId, string map, string name = "Test Npc") =>
        new(id, definitionId, name, map, 10, 10, 0, 45, 1, 1);

    [Fact]
    public void AddNpc_WithFivePlacements_ProducesFiveIndependentWorldActorsSharingOneFactory()
    {
        var definition = new NpcDefinition("npcdef:test:shared", "Shared Npc", [new("OnClick", static () => new FixtureScript())]);
        var placements = Enumerable.Range(1, 5).Select(index => Placement($"npc:map{index}:shared npc", definition.DefinitionId, $"map{index}", "Shared Npc")).ToArray();

        var result = new WorldRegistryBuilder().AddNpc(definition, placements).Build();
        var registry = new WorldMapRegistry([], result.Entities, scripts: result.Scripts);

        Assert.Equal(5, result.Entities.Count);
        var actorIds = new HashSet<uint>();
        foreach (var placement in placements)
        {
            Assert.True(registry.TryGetActor(placement.PlacementId, placement.Map, out var actor));
            Assert.True(actorIds.Add(actor.ActorId));
            Assert.True(result.Scripts.TryCreate(placement.PlacementId, "OnClick", out var script));
            Assert.IsType<FixtureScript>(script);
        }
        Assert.Equal(5, actorIds.Count);
    }

    [Fact]
    public void AddNpc_DefinitionWithNoTriggers_ProducesActorOnlyEntitiesPresentInBuiltWorld()
    {
        var definition = new NpcDefinition("npcdef:test:actor-only", "Actor Only Npc", []);
        var placements = new[] { Placement("npc:map1:actor only npc", definition.DefinitionId, "map1", "Actor Only Npc") };

        var result = new WorldRegistryBuilder().AddNpc(definition, placements).Build();
        var registry = new WorldMapRegistry([], result.Entities, scripts: result.Scripts);

        Assert.Single(result.Entities);
        Assert.True(registry.TryGetActor(placements[0].PlacementId, "map1", out var actor));
        Assert.False(result.Scripts.TryCreate(placements[0].PlacementId, "OnClick", out _));
        Assert.False(registry.TryGetInteraction(actor.ActorId, "map1", out _, out _));
    }

    [Fact]
    public void AddNpc_DuplicateDefinitionId_Throws()
    {
        var definition = new NpcDefinition("npcdef:test:mismatch", "Mismatch Npc", []);
        var badPlacement = Placement("npc:map1:mismatch npc", "npcdef:test:other", "map1");
        Assert.Throws<InvalidOperationException>(() => new WorldRegistryBuilder().AddNpc(definition, [badPlacement]));
    }

    [Fact]
    public void AddNpc_EmptyPlacements_Throws()
    {
        var definition = new NpcDefinition("npcdef:test:empty", "Empty Npc", []);
        Assert.Throws<InvalidOperationException>(() => new WorldRegistryBuilder().AddNpc(definition, []));
    }

    [Fact]
    public void AddNpc_ConflictingPlacementId_ThrowsUnlessExplicitOverride()
    {
        var definition = new NpcDefinition("npcdef:test:conflict", "Conflict Npc", [new("OnClick", static () => new FixtureScript())]);
        var placement = Placement("npc:map1:conflict npc", definition.DefinitionId, "map1", "Conflict Npc");
        var builder = new WorldRegistryBuilder().AddNpc(definition, [placement]);

        Assert.Throws<InvalidOperationException>(() => builder.AddNpc(definition, [placement]));

        var overridden = builder.AddNpc(definition, [placement], explicitlyOverrideGenerated: true).Build();
        Assert.True(overridden.Scripts.TryCreate(placement.PlacementId, "OnClick", out var script));
        Assert.IsType<FixtureScript>(script);
    }

    [Fact]
    public void AddNpc_CustomHandWrittenDefinition_UsesIdenticalApiAsGeneratedContent()
    {
        var definition = new NpcDefinition("mynpc", "My NPC", [new("OnClick", static () => new FixtureScript())]);
        var placement = new NpcPlacement("npc:prontera:my npc", definition.DefinitionId, "My NPC", "prontera", 100, 100, 4, 123, 1, 1);

        var result = new WorldRegistryBuilder().AddNpc(definition, [placement]).Build();

        Assert.Single(result.Entities);
        Assert.True(result.Scripts.TryCreate(placement.PlacementId, "OnClick", out var script));
        Assert.IsType<FixtureScript>(script);
    }

    private static WarpTriggerPlacement WarpPlacement(string id, string definitionId, string map, string name) =>
        new(id, definitionId, name, map, 56, 15, 0, 1, 1);

    [Fact]
    public void AddWarpTrigger_WithFourPlacements_ProducesFourIndependentWorldActorsSharingOneFactory()
    {
        var definition = new WarpTriggerDefinition("warpdef:test:ship_out", "#ship_out", new("OnTouch", static () => new FixtureScript()));
        var placements = Enumerable.Range(1, 4).Select(index => WarpPlacement($"warp:map{index}:ship_out{index}", definition.DefinitionId, $"map{index}", $"#ship_out{index}")).ToArray();

        var result = new WorldRegistryBuilder().AddWarpTrigger(definition, placements).Build();
        var registry = new WorldMapRegistry([], result.Entities, scripts: result.Scripts);

        Assert.Equal(4, result.Entities.Count);
        Assert.All(result.Entities, entity => Assert.Equal("Warp", entity.Kind));
        var actorIds = new HashSet<uint>();
        foreach (var placement in placements)
        {
            Assert.True(registry.TryGetActor(placement.PlacementId, placement.Map, out var actor));
            Assert.Equal((ushort)45, actor.SpriteClass);
            Assert.True(actorIds.Add(actor.ActorId));
            Assert.True(result.Scripts.TryCreate(placement.PlacementId, "OnTouch", out var script));
            Assert.IsType<FixtureScript>(script);
        }
        Assert.Equal(4, actorIds.Count);
    }

    [Fact]
    public void AddWarpTrigger_DuplicateDefinitionId_Throws()
    {
        var definition = new WarpTriggerDefinition("warpdef:test:mismatch", "#mismatch", new("OnTouch", static () => new FixtureScript()));
        var badPlacement = WarpPlacement("warp:map1:mismatch", "warpdef:test:other", "map1", "#mismatch");
        Assert.Throws<InvalidOperationException>(() => new WorldRegistryBuilder().AddWarpTrigger(definition, [badPlacement]));
    }

    [Fact]
    public void AddWarpTrigger_EmptyPlacements_Throws()
    {
        var definition = new WarpTriggerDefinition("warpdef:test:empty", "#empty", new("OnTouch", static () => new FixtureScript()));
        Assert.Throws<InvalidOperationException>(() => new WorldRegistryBuilder().AddWarpTrigger(definition, []));
    }
}

file sealed class FixtureScript : INpcScript
{
    public Task ExecuteAsync(ScriptContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}
