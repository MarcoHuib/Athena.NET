namespace Athena.Net.MapServer.Gameplay.Rules;

// The composed bundle of ruleset-agnostic rule interfaces MapServerWorld.Build
// receives from the MapServer startup/composition root (MapServerApp.RunAsync,
// via GameplayRulesFactory.Create). MapServerWorld itself never inspects
// GameplayOptions/RagnarokRuleSet or calls GameplayRulesFactory - by the time this
// bundle reaches it, ruleset selection has already happened. A small record like
// this (rather than one giant IGameRules interface) lets future independently
// scoped rule interfaces (e.g. IAspdRules, IExperienceRules) be added as additional
// properties here without forcing every consumer to depend on all of them.
public sealed record GameplayRuleServices(IBasicAttackRules BasicAttackRules);
