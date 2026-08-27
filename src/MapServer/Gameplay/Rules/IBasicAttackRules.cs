namespace Athena.Net.MapServer.Gameplay.Rules;

// Ruleset-agnostic basic (no-skill) melee attack contract. MonsterCombatCoordinator
// depends on this interface only - it never knows or asks which ruleset is active.
// The concrete implementation (currently RenewalBasicAttackRules) is selected once
// at MapServer startup by GameplayRulesFactory, per GameplayOptions.RuleSet.
public interface IBasicAttackRules
{
    BasicAttackDamageResult Calculate(BasicAttackContext context);
}
