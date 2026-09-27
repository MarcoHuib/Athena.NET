using Athena.Net.LoginServer.Config;

namespace Athena.Net.LoginServer.Tests.Config;

public sealed class InterConfigLoaderTests
{
    [Fact]
    public void Load_ParsesCaseSensitivity_AndBuildsSqlServerConnectionString()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, "inter_athena.conf");
        File.WriteAllText(filePath, "login_server_id: user\nlogin_server_db: db\nlogin_server_pw: pass\nlogin_case_sensitive: yes\n");

        // Act
        var config = InterConfigLoader.Load(filePath);

        // Assert
        Assert.True(config.LoginCaseSensitive);
        Assert.Equal("sqlserver", config.LoginDbProvider);
        Assert.Contains("Encrypt=True", config.LoginDbConnectionString, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("User ID=user", config.LoginDbConnectionString, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CharSet=", config.LoginDbConnectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_NormalizesMssqlProviderAlias_ToSqlServer()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, "inter_athena.conf");
        File.WriteAllText(filePath, "login_server_id: user\nlogin_server_db: db\nlogin_server_pw: pass\nlogin_db_provider: mssql\n");

        // Act
        var config = InterConfigLoader.Load(filePath);

        // Assert
        Assert.Equal("sqlserver", config.LoginDbProvider);
    }
}
