namespace Athena.Net.MapServer.Gameplay.Rules;

// MapConfig-sourced (see MapConfigLoader's "gameplay_ruleset" key, matching this
// project's existing rAthena-style key:value .conf convention rather than
// introducing a Microsoft.Extensions.Configuration/appsettings.json dependency this
// codebase does not otherwise have). Read once at MapServer startup and handed to
// GameplayRulesFactory.Create - nothing downstream re-reads MapConfig for ruleset
// decisions.
public sealed class GameplayOptions
{
    public RagnarokRuleSet RuleSet { get; init; } = RagnarokRuleSet.Renewal;
}
