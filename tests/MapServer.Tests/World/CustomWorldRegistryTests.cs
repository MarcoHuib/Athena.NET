using Athena.Net.MapServer.Customs.World;
using Athena.Net.MapServer.Customs.World.Izlude;
using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.GeneratedScripts;

namespace Athena.Net.MapServer.Tests.World;

// Proves the customs.enabled composition boundary (MapServerWorld.Build's customsEnabled
// parameter -> CustomWorldRegistry.Register) - see ai/map-server.md's "Handwritten custom world
// content" section. Generated content composition itself is covered elsewhere
// (MapServerWorldGameplayRulesTests, WorldMapRegistryFamilyTests); these tests only prove
// wiring/reuse, not the complete progression/heal engines.
public sealed class CustomWorldRegistryTests
{
    private static GameplayRuleServices RenewalRules() => new(new RenewalBasicAttackRules());

    [Fact]
    public void Build_CustomsDisabled_AthenaTestNpcIsAbsent()
    {
        var world = MapServerWorld.Build(RenewalRules(), customsEnabled: false);

        Assert.False(world.Maps.TryGetActor("custom:iz_int03:athena test npc", "iz_int03", out _));
    }

    [Fact]
    public void Build_CustomsEnabled_AthenaTestNpcIsRegistered()
    {
        var world = MapServerWorld.Build(RenewalRules(), customsEnabled: true);

        Assert.True(world.Maps.TryGetActor("custom:iz_int03:athena test npc", "iz_int03", out var actor));
        Assert.Equal("Athena Test NPC", actor.Name);
        Assert.True(world.Maps.TryGetInteraction(actor.ActorId, "iz_int03", out _, out var script));
        Assert.Equal("OnClick", script.Trigger);
    }

    [Fact]
    public void Build_CustomsEnabled_GeneratedWorldContentRemainsRegistered()
    {
        var world = MapServerWorld.Build(RenewalRules(), customsEnabled: true);

        // A representative generated NPC (Captain Carocc, generic int_land) must still be present
        // alongside the enabled custom content - customs are additive, never a replacement.
        Assert.True(world.Maps.TryGetActor("npc:int_land:captain carocc#intro_npc03", "int_land", out _));
    }

    [Fact]
    public void Register_CollidingWithGeneratedEntityId_ThrowsRatherThanSilentlyOverwriting()
    {
        var builder = new WorldRegistryBuilder();
        GeneratedScriptRegistry.Register(builder);

        // Reuses a real generated placement id (Captain Carocc, generic int_land) under a custom
        // definition - this must fail loudly rather than silently replace the generated entity.
        var conflictingDefinition = new NpcDefinition("custom:conflict", "Conflict Npc", [new("OnClick", static () => new NoOpScript())]);
        var conflictingPlacement = new NpcPlacement(
            "npc:int_land:captain carocc#intro_npc03", conflictingDefinition.DefinitionId, "Conflict Npc", "int_land", 1, 1, 0, 1, 0, 0);

        Assert.Throws<InvalidOperationException>(() => builder.AddNpc(conflictingDefinition, [conflictingPlacement]));
    }

    [Fact]
    public void IzludeCustomWorld_DoesNotCollideWithGeneratedIzIntFamilyContent()
    {
        // The Athena Test NPC's own placement must compose cleanly onto the real generated world
        // without needing explicitlyOverrideGenerated - i.e. it uses a genuinely distinct
        // PlacementId/DefinitionId and does not overlap any existing generated iz_int03 actor.
        var builder = new WorldRegistryBuilder();
        GeneratedScriptRegistry.Register(builder);

        var exception = Record.Exception(() => IzludeCustomWorld.Register(builder));

        Assert.Null(exception);
    }

    private sealed class NoOpScript : INpcScript
    {
        public Task ExecuteAsync(ScriptContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
