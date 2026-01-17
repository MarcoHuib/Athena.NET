using Athena.Net.LoginServer.Config;

namespace Athena.Net.LoginServer.Tests.Config;

public sealed class LoginConfigLoaderTests
{
    [Fact]
    public void Load_ParsesLegacyAliases_AndTimestampFormat()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, "login_athena.conf");
        File.WriteAllText(filePath, string.Join('\n', new[]
        {
            "ipban_enable: yes",
            "ipban_dynamic_pass_failure_ban: yes",
            "ipban_dynamic_pass_failure_ban_interval: 7",
            "ipban_dynamic_pass_failure_ban_limit: 5",
            "ipban_dynamic_pass_failure_ban_duration: 12",
            "dnsbl_servers: bl.example.test",
            "timestamp_format: [%Y-%m-%d %H:%M:%S]",
            "console_msg_log: 7",
            "console_silent: 16",
            "console_log_filepath: ./log/custom.log",
        }));

        // Act
        var config = LoginConfigLoader.Load(filePath);

        // Assert
        Assert.True(config.IpBanEnabled);
        Assert.True(config.DynamicPassFailureBan);
        Assert.Equal(7, config.DynamicPassFailureBanIntervalMinutes);
        Assert.Equal(5, config.DynamicPassFailureBanLimit);
        Assert.Equal(12, config.DynamicPassFailureBanDurationMinutes);
        Assert.Equal("bl.example.test", config.DnsblServers);
        Assert.Equal("[%Y-%m-%d %H:%M:%S]", config.TimestampFormat);
        Assert.Equal(7, config.ConsoleMsgLog);
        Assert.Equal(16, config.ConsoleSilent);
        Assert.Equal("./log/custom.log", config.ConsoleLogFilePath);
    }

    [Fact]
    public void Load_ResolvesImportLines_InOrder()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));
        var importDir = Path.Combine(tempDir, "import");
        Directory.CreateDirectory(importDir);
        var importPath = Path.Combine(importDir, "login_conf.txt");
        File.WriteAllText(importPath, "usercount_high: 777\n");

        var filePath = Path.Combine(tempDir, "login_athena.conf");
        File.WriteAllText(filePath, string.Join('\n', new[]
        {
            "usercount_high: 1000",
            "import: import/login_conf.txt",
        }));

        // Act
        var config = LoginConfigLoader.Load(filePath);

        // Assert
        Assert.Equal(777, config.UsercountHigh);
    }
}
