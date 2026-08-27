using Athena.Net.MapServer.Gameplay.Rules.Renewal;

namespace Athena.Net.MapServer.Gameplay.Rules;

// The one place MapServer decides which ruleset's gameplay implementations to
// register, called once from the MapServer startup/composition root
// (MapServerApp.RunAsync - this codebase has no Microsoft.Extensions.
// DependencyInjection container, so "registration" here means constructing and
// returning the concrete instances). The resulting GameplayRuleServices bundle is
// then handed to MapServerWorld.Build, which stays entirely unaware of
// GameplayOptions/RagnarokRuleSet/this factory - every consumer downstream
// (MonsterCombatCoordinator, MapClientSession) depends only on IBasicAttackRules
// and never asks which ruleset is active.
public static class GameplayRulesFactory
{
    // PreRenewal is a real enum member so configuration/domain code can express the
    // intent explicitly (RagnarokRuleSet's own doc comment), but it has NO
    // implementation in this codebase yet (see Gameplay/Rules/PreRenewal/README.md)
    // - selecting it must fail startup loudly, never silently fall back to Renewal
    // and never return a fake/stub rules object.
    public static GameplayRuleServices Create(GameplayOptions options) => options.RuleSet switch
    {
        RagnarokRuleSet.Renewal => new GameplayRuleServices(new RenewalBasicAttackRules()),
        RagnarokRuleSet.PreRenewal => throw new NotSupportedException("Pre-Renewal gameplay rules are not implemented."),
        _ => throw new NotSupportedException($"Unknown gameplay rule set '{options.RuleSet}'."),
    };
}
