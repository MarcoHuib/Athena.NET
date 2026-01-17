using Athena.Net.LoginServer.Config;

namespace Athena.Net.LoginServer.Tests.Config;

public sealed class InterConfigLoaderTests
{
    [Fact]
    public void Load_ParsesCaseSensitivity_AndCodepage()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, "inter_athena.conf");
        File.WriteAllText(filePath, "login_server_id: user\nlogin_server_db: db\nlogin_server_pw: pass\nlogin_codepage: utf8mb4\nlogin_case_sensitive: yes\nlogin_db_provider: mysql\n");

        // Act
        var config = InterConfigLoader.Load(filePath);

        // Assert
        Assert.Equal("utf8mb4", config.LoginDbCodepage);
        Assert.True(config.LoginCaseSensitive);
        Assert.Contains("CharSet=utf8mb4", config.LoginDbConnectionString, StringComparison.OrdinalIgnoreCase);
    }
}
