using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

// Proves MapServerWorld.Build - the manual composition root MapServerApp.RunAsync
// calls (this codebase has no Microsoft.Extensions.DependencyInjection container) -
// actually threads GameplayOptions through GameplayRulesFactory into the composed
// MonsterCombatCoordinator, and that an unsupported ruleset fails composition
// outright rather than silently building a Renewal-backed world.
public sealed class MapServerWorldGameplayRulesTests
{
    [Fact]
    public void Build_DefaultGameplayOptions_ComposesSuccessfully()
    {
        var world = MapServerWorld.Build();

        Assert.NotNull(world.Combat);
    }

    [Fact]
    public void Build_ExplicitRenewalOptions_ComposesSuccessfully()
    {
        var world = MapServerWorld.Build(gameplayOptions: new GameplayOptions { RuleSet = RagnarokRuleSet.Renewal });

        Assert.NotNull(world.Combat);
    }

    [Fact]
    public void Build_PreRenewalOptions_ThrowsNotSupported_DoesNotComposeAWorld()
    {
        Assert.Throws<NotSupportedException>(() =>
            MapServerWorld.Build(gameplayOptions: new GameplayOptions { RuleSet = RagnarokRuleSet.PreRenewal }));
    }
}
