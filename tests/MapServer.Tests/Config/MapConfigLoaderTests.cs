using System.Net;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Gameplay.Rates;

namespace Athena.Net.MapServer.Tests.Config;

public sealed class MapConfigLoaderTests
{
    [Fact]
    public void Load_ReturnsDefaults_WhenConfigMissing()
    {
        var tempDir = CreateTempDir();
        var path = Path.Combine(tempDir, "missing.conf");

        var config = MapConfigLoader.Load(path);

        Assert.Equal(IPAddress.Loopback, config.CharIp);
        Assert.Equal(6121, config.CharPort);
        Assert.Equal(IPAddress.Loopback, config.MapIp);
        Assert.Equal(5121, config.MapPort);
        Assert.Equal(RagnarokRuleSet.Renewal, config.GameplayRuleSet);
    }

    [Fact]
    public void Load_ConfigPresentWithoutGameplayRuleSetKey_DefaultsToRenewal()
    {
        var tempDir = CreateTempDir();
        var path = Path.Combine(tempDir, "map_athena.conf");
        File.WriteAllText(path, "char_ip: 10.0.0.5\n");

        var config = MapConfigLoader.Load(path);

        Assert.Equal(RagnarokRuleSet.Renewal, config.GameplayRuleSet);
        Assert.Equal(100u, config.GameplayRates.BaseExpRate);
        Assert.Equal(100u, config.GameplayRates.JobExpRate);
        Assert.Equal(100u, config.GameplayRates.ItemDropRate);
        Assert.Null(config.GameplayRates.QuestBaseExpRate);
        Assert.Null(config.GameplayRates.QuestJobExpRate);
        Assert.Null(config.GameplayRates.MvpBaseExpRate);
        Assert.Null(config.GameplayRates.MvpJobExpRate);
        Assert.Null(config.GameplayRates.CardDropRate);
        Assert.Null(config.GameplayRates.MvpItemDropRate);
    }

    [Fact]
    public void Load_ParsesGlobalRatesAndLeavesOverridesUnset()
    {
        var path = Path.Combine(CreateTempDir(), "map_athena.conf");
        File.WriteAllText(path, "base_exp_rate: 500\njob_exp_rate: 500\nitem_drop_rate: 200\n");

        var rates = MapConfigLoader.Load(path).GameplayRates;
        Assert.Equal(500u, rates.BaseExpRate);
        Assert.Equal(500u, rates.JobExpRate);
        Assert.Equal(200u, rates.ItemDropRate);
        // Overrides are null (inherit), never an independent default of 100.
        Assert.Null(rates.QuestBaseExpRate);
        Assert.Null(rates.QuestJobExpRate);
        Assert.Null(rates.MvpBaseExpRate);
        Assert.Null(rates.MvpJobExpRate);
        Assert.Null(rates.CardDropRate);
        Assert.Null(rates.BossItemDropRate);
        Assert.Null(rates.MvpItemDropRate);
        Assert.Null(rates.QuestItemDropRate);
    }

    [Fact]
    public void Load_ParsesExplicitOverridesAndItemCategoryRates()
    {
        var path = Path.Combine(CreateTempDir(), "map_athena.conf");
        File.WriteAllText(path,
            "base_exp_rate: 500\njob_exp_rate: 200\nquest_base_exp_rate: 1000\nquest_job_exp_rate: 1000\nmvp_base_exp_rate: 300\nmvp_job_exp_rate: 300\n" +
            "item_drop_rate: 200\ncard_drop_rate: 100\nboss_item_drop_rate: 150\nmvp_item_drop_rate: 500\nquest_item_drop_rate: 10000\n" +
            "item_rate_common: 250\nitem_rate_common_boss: 300\nitem_rate_common_mvp: 400\nitem_rate_mvp: 700\n");

        var rates = MapConfigLoader.Load(path).GameplayRates;
        Assert.Equal(500u, rates.BaseExpRate);
        Assert.Equal(200u, rates.JobExpRate);
        Assert.Equal(1000u, rates.QuestBaseExpRate);
        Assert.Equal(1000u, rates.QuestJobExpRate);
        Assert.Equal(300u, rates.MvpBaseExpRate);
        Assert.Equal(300u, rates.MvpJobExpRate);
        Assert.Equal(200u, rates.ItemDropRate);
        Assert.Equal(100u, rates.CardDropRate);
        Assert.Equal(150u, rates.BossItemDropRate);
        Assert.Equal(500u, rates.MvpItemDropRate);
        Assert.Equal(10000u, rates.QuestItemDropRate);
        Assert.Equal(250u, rates.ItemRateCommon);
        Assert.Equal(300u, rates.ItemRateCommonBoss);
        Assert.Equal(400u, rates.ItemRateCommonMvp);
        Assert.Equal(700u, rates.ItemRateMvp);
        // Unconfigured category override stays null (inherit).
        Assert.Null(rates.ItemRateCard);
    }

    [Theory]
    [InlineData("base_exp_rate: -1")]
    [InlineData("base_exp_rate: 2147483648")]
    [InlineData("item_rate_card: 1000001")]
    [InlineData("quest_base_exp_rate: nope")]
    [InlineData("item_drop_rate: -1")]
    public void Load_InvalidRateFailsLoudly(string line)
    {
        var path = Path.Combine(CreateTempDir(), "map_athena.conf");
        File.WriteAllText(path, line + "\n");
        Assert.Throws<InvalidOperationException>(() => MapConfigLoader.Load(path));
    }

    [Fact]
    public void Load_ParsesExplicitGameplayRuleSet_Renewal()
    {
        var tempDir = CreateTempDir();
        var path = Path.Combine(tempDir, "map_athena.conf");
        File.WriteAllText(path, "gameplay_ruleset: Renewal\n");

        var config = MapConfigLoader.Load(path);

        Assert.Equal(RagnarokRuleSet.Renewal, config.GameplayRuleSet);
    }

    [Fact]
    public void Load_ParsesExplicitGameplayRuleSet_PreRenewal()
    {
        var tempDir = CreateTempDir();
        var path = Path.Combine(tempDir, "map_athena.conf");
        File.WriteAllText(path, "gameplay_ruleset: PreRenewal\n");

        var config = MapConfigLoader.Load(path);

        Assert.Equal(RagnarokRuleSet.PreRenewal, config.GameplayRuleSet);
    }

    // Unlike every other key in this loader, an explicitly present but unrecognized
    // gameplay_ruleset value must FAIL config loading, not silently fall back to
    // the default - a typo'd value must never quietly run as Renewal (masking the
    // mistake) or as some other unintended enum value.
    [Fact]
    public void Load_UnrecognizedGameplayRuleSetValue_Throws()
    {
        var tempDir = CreateTempDir();
        var path = Path.Combine(tempDir, "map_athena.conf");
        File.WriteAllText(path, "gameplay_ruleset: SomeFutureRuleSet\n");

        var ex = Assert.Throws<InvalidOperationException>(() => MapConfigLoader.Load(path));
        Assert.Contains("SomeFutureRuleSet", ex.Message);
    }

    [Fact]
    public void Load_EmptyGameplayRuleSetValue_Throws()
    {
        var tempDir = CreateTempDir();
        var path = Path.Combine(tempDir, "map_athena.conf");
        File.WriteAllText(path, "gameplay_ruleset: \n");

        Assert.Throws<InvalidOperationException>(() => MapConfigLoader.Load(path));
    }

    [Fact]
    public void Load_ParsesExplicitValues()
    {
        var tempDir = CreateTempDir();
        var path = Path.Combine(tempDir, "map_athena.conf");
        File.WriteAllText(path, "char_ip: 10.0.0.5\nmap_ip: 1.2.3.4\nchar_port: 6122\nmap_port: 5122\nconsole: yes\n");

        var config = MapConfigLoader.Load(path);

        Assert.Equal(IPAddress.Parse("10.0.0.5"), config.CharIp);
        Assert.Equal(IPAddress.Parse("1.2.3.4"), config.MapIp);
        Assert.Equal(6122, config.CharPort);
        Assert.Equal(5122, config.MapPort);
        Assert.True(config.ConsoleEnabled);
    }

    [Fact]
    public void Load_ResolvesImports()
    {
        var tempDir = CreateTempDir();
        var basePath = Path.Combine(tempDir, "map_athena.conf");
        var importPath = Path.Combine(tempDir, "extra.conf");

        File.WriteAllText(importPath, "map_ip: 9.9.9.9\nmap_port: 5000\n");
        File.WriteAllText(basePath, "import: extra.conf\nchar_ip: 10.0.0.8\n");

        var config = MapConfigLoader.Load(basePath);

        Assert.Equal(IPAddress.Parse("10.0.0.8"), config.CharIp);
        Assert.Equal(IPAddress.Parse("9.9.9.9"), config.MapIp);
        Assert.Equal(5000, config.MapPort);
    }

    [Fact]
    public void Load_NoCollisionArtifactLines_ReturnsEmptyList()
    {
        var tempDir = CreateTempDir();
        var path = Path.Combine(tempDir, "map_athena.conf");
        File.WriteAllText(path, "char_ip: 10.0.0.5\n");

        var config = MapConfigLoader.Load(path);

        Assert.Empty(config.CollisionArtifacts);
    }

    [Fact]
    public void Load_ParsesOneCollisionArtifactLine_WithMultipleAliases()
    {
        var tempDir = CreateTempDir();
        var path = Path.Combine(tempDir, "map_athena.conf");
        File.WriteAllText(path, "map_collision_artifact: /local/int_land.athmap|int_land,int_land01,int_land02,int_land03,int_land04\n");

        var config = MapConfigLoader.Load(path);

        var artifact = Assert.Single(config.CollisionArtifacts);
        Assert.Equal("/local/int_land.athmap", artifact.Path);
        Assert.Equal(["int_land", "int_land01", "int_land02", "int_land03", "int_land04"], artifact.Maps);
    }

    [Fact]
    public void Load_ParsesMultipleCollisionArtifactLines()
    {
        var tempDir = CreateTempDir();
        var path = Path.Combine(tempDir, "map_athena.conf");
        File.WriteAllText(path,
            "map_collision_artifact: /local/int_land.athmap|int_land\n" +
            "map_collision_artifact: /local/iz_int.athmap|iz_int,iz_int03\n");

        var config = MapConfigLoader.Load(path);

        Assert.Equal(2, config.CollisionArtifacts.Count);
        Assert.Equal("/local/int_land.athmap", config.CollisionArtifacts[0].Path);
        Assert.Equal("/local/iz_int.athmap", config.CollisionArtifacts[1].Path);
    }

    [Fact]
    public void Load_CollisionArtifactLineMissingPipe_Throws()
    {
        var tempDir = CreateTempDir();
        var path = Path.Combine(tempDir, "map_athena.conf");
        File.WriteAllText(path, "map_collision_artifact: /local/int_land.athmap\n");

        Assert.Throws<InvalidOperationException>(() => MapConfigLoader.Load(path));
    }

    [Fact]
    public void Load_CollisionArtifactLineWithNoMaps_Throws()
    {
        var tempDir = CreateTempDir();
        var path = Path.Combine(tempDir, "map_athena.conf");
        File.WriteAllText(path, "map_collision_artifact: /local/int_land.athmap|\n");

        Assert.Throws<InvalidOperationException>(() => MapConfigLoader.Load(path));
    }

    [Fact]
    public void Load_NoMapCachePathConfigured_DefaultsToNull()
    {
        var tempDir = CreateTempDir();
        var path = Path.Combine(tempDir, "map_athena.conf");
        File.WriteAllText(path, "char_ip: 10.0.0.5\n");

        var config = MapConfigLoader.Load(path);

        Assert.Null(config.MapCachePath);
    }

    [Fact]
    public void Load_ParsesMapCachePath()
    {
        var tempDir = CreateTempDir();
        var path = Path.Combine(tempDir, "map_athena.conf");
        File.WriteAllText(path, "map_cache_path: legacy/rathena/db/map_cache.dat\n");

        var config = MapConfigLoader.Load(path);

        Assert.Equal("legacy/rathena/db/map_cache.dat", config.MapCachePath);
    }

    [Fact]
    public void Load_BothMapCachePathAndCollisionArtifactConfigured_ThrowsClearly()
    {
        var tempDir = CreateTempDir();
        var path = Path.Combine(tempDir, "map_athena.conf");
        File.WriteAllText(path,
            "map_cache_path: legacy/rathena/db/map_cache.dat\n" +
            "map_collision_artifact: /local/int_land.athmap|int_land\n");

        var ex = Assert.Throws<InvalidOperationException>(() => MapConfigLoader.Load(path));
        Assert.Contains("map_cache_path", ex.Message);
        Assert.Contains("map_collision_artifact", ex.Message);
    }

    [Fact]
    public void SecretConfig_ApplyTo_PreservesGameplayRuleSet()
    {
        var secrets = new SecretConfig();
        var config = new MapConfig { GameplayRuleSet = RagnarokRuleSet.PreRenewal };

        var merged = secrets.ApplyTo(config);

        Assert.Equal(RagnarokRuleSet.PreRenewal, merged.GameplayRuleSet);
    }

    [Fact]
    public void SecretConfig_ApplyTo_PreservesSameImmutableRatePolicy()
    {
        var rates = new GameplayRateOptions { BaseExpRate = 500 };
        var merged = new SecretConfig().ApplyTo(new MapConfig { GameplayRates = rates });
        Assert.Same(rates, merged.GameplayRates);
    }

    // Acceptance criterion for Correction 7: SecretConfig only knows about
    // UserId/Password. MapConfig is a record, so ApplyTo clones every other
    // property via `with` - adding a brand-new non-secret MapConfig property
    // (simulated here by round-tripping GameplayRates itself, which is exactly
    // the kind of gameplay-only property that must never require touching
    // SecretConfig again) flows through untouched with no SecretConfig change.
    [Fact]
    public void SecretConfig_ApplyTo_RoundTripsGameplayRatesWithoutSecretConfigChanges()
    {
        var rates = new GameplayRateOptions { BaseExpRate = 500, JobExpRate = 500, ItemDropRate = 200 };
        var config = new MapConfig { GameplayRates = rates, UserId = "u", Password = "p" };

        var merged = new SecretConfig { UserId = "override" }.ApplyTo(config);

        Assert.Same(rates, merged.GameplayRates);
        Assert.Equal("override", merged.UserId);
        Assert.Equal("p", merged.Password);
    }

    // SecretConfig.ApplyTo must touch ONLY UserId/Password - every other property
    // is passed through unchanged (asserted via reference/structural equality of
    // the whole MapConfig record modulo the two secret fields).
    [Fact]
    public void SecretConfig_ApplyTo_OnlyChangesUserIdAndPassword()
    {
        var config = new MapConfig { UserId = "original", Password = "secret" };
        var merged = new SecretConfig().ApplyTo(config);
        Assert.Equal(config with { }, merged);
    }

    // Regression test: SecretConfig.ApplyTo previously reconstructed a brand-new MapConfig without
    // copying CollisionArtifacts/MapCachePath at all, so a correctly-parsed map_cache_path was
    // silently DISCARDED by the merge step immediately after loading - MapServerApp.RunAsync always
    // saw MapCachePath=null regardless of what the config file said, making it impossible for the
    // real MapServer executable to ever select the real collision-backed selector. This was found
    // by an end-to-end Docker run producing "Generated monster spawns are configured but no real
    // map collision source is loaded" despite an explicit map_cache_path line in the loaded config.
    [Fact]
    public void SecretConfig_ApplyTo_PreservesMapCachePath()
    {
        var secrets = new SecretConfig();
        var config = new MapConfig { MapCachePath = "legacy/rathena/db/map_cache.dat" };

        var merged = secrets.ApplyTo(config);

        Assert.Equal("legacy/rathena/db/map_cache.dat", merged.MapCachePath);
    }

    [Fact]
    public void SecretConfig_ApplyTo_PreservesCollisionArtifacts()
    {
        var secrets = new SecretConfig();
        var artifact = new MapCollisionArtifactConfig("/local/int_land.athmap", ["int_land"]);
        var config = new MapConfig { CollisionArtifacts = [artifact] };

        var merged = secrets.ApplyTo(config);

        Assert.Same(artifact, Assert.Single(merged.CollisionArtifacts));
    }

    [Fact]
    public void SecretConfig_AppliesCredentials_WhenPresent()
    {
        var tempDir = CreateTempDir();
        var secretsPath = Path.Combine(tempDir, "secret.json");
        File.WriteAllText(secretsPath, "{\"MapServer\":{\"UserId\":\"srv_user\",\"Password\":\"srv_pass\"}}");

        var secrets = SecretConfig.Load(secretsPath);
        var config = new MapConfig { UserId = string.Empty, Password = string.Empty };
        var merged = secrets.ApplyTo(config);

        Assert.Equal("srv_user", merged.UserId);
        Assert.Equal("srv_pass", merged.Password);
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "athena-map-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
