using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;

namespace Athena.Net.MapServer.Tests.Gameplay.Rules;

public sealed class GameplayRulesFactoryTests
{
    [Fact]
    public void Create_DefaultOptions_ResolvesToRenewalRuleSet()
    {
        var options = new GameplayOptions();
        Assert.Equal(RagnarokRuleSet.Renewal, options.RuleSet);
    }

    [Fact]
    public void Create_Renewal_RegistersRenewalBasicAttackRules()
    {
        var services = GameplayRulesFactory.Create(new GameplayOptions { RuleSet = RagnarokRuleSet.Renewal });

        Assert.IsType<RenewalBasicAttackRules>(services.BasicAttackRules);
    }

    [Fact]
    public void Create_PreRenewal_ThrowsNotSupported_DoesNotFallBackToRenewal()
    {
        var ex = Assert.Throws<NotSupportedException>(() => GameplayRulesFactory.Create(new GameplayOptions { RuleSet = RagnarokRuleSet.PreRenewal }));

        Assert.Contains("Pre-Renewal", ex.Message);
        Assert.DoesNotContain("Renewal rules", ex.Message); // Guard against an accidental silent-fallback message.
    }
}
