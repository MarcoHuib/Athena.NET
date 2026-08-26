namespace Athena.Net.MapServer.Gameplay.Rules;

// The gameplay-mechanics family a MapServer process runs, selected once at startup
// (see GameplayOptions/GameplayRulesFactory) - not a per-request or per-character
// choice. Athena.NET currently targets the current official iRO client, which is
// RENEWAL-only; PreRenewal exists here so the configuration/domain model explicitly
// anticipates a second ruleset family, but it has NO implementation yet (see
// GameplayRulesFactory's own doc comment) - selecting it must fail startup clearly,
// never silently fall back to Renewal.
public enum RagnarokRuleSet
{
    Renewal,
    PreRenewal,
}
