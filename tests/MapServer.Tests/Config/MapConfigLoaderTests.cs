using System.Net;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Gameplay.Rules;

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
