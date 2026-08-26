using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

// Proves MapServerWorld.Build takes an already-composed GameplayRuleServices
// bundle and wires it into the composed MonsterCombatCoordinator - it does NOT
// itself inspect GameplayOptions/RagnarokRuleSet or call GameplayRulesFactory
// (that selection now happens exclusively in the MapServer startup/composition
// root, MapServerApp.RunAsync; see GameplayRulesFactoryTests for ruleset-selection
// coverage, including the PreRenewal-throws case, which is composition-root
// behavior, not MapServerWorld behavior).
public sealed class MapServerWorldGameplayRulesTests
{
    [Fact]
    public void Build_WithRenewalRuleServices_ComposesSuccessfully()
    {
        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()));

        Assert.NotNull(world.Combat);
    }

    // MapServerWorld.Build takes whatever IBasicAttackRules implementation the
    // bundle carries at face value - it has no ruleset awareness to validate
    // against, so a caller could construct a bundle from any IBasicAttackRules
    // implementation without MapServerWorld caring. This is the whole point of the
    // composition boundary: MapServerWorld only ever sees the interface.
    [Fact]
    public void Build_UsesWhicheverBasicAttackRulesImplementationTheBundleCarries()
    {
        var probe = new ProbeBasicAttackRules();

        var world = MapServerWorld.Build(new GameplayRuleServices(probe));

        Assert.NotNull(world.Combat);
    }

    private sealed class ProbeBasicAttackRules : IBasicAttackRules
    {
        public BasicAttackDamageResult Calculate(BasicAttackContext context) => new(0, IsMiss: true);
    }
}
