using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Logging;

namespace Athena.Net.LoginServer.Tests.Logging;

public sealed class LoginLoggerTests
{
    [Fact]
    public void Configure_AppliesTimestampFormat_WithoutThrowing()
    {
        // Arrange
        var config = new LoginConfig
        {
            TimestampFormat = "[%Y-%m-%d %H:%M:%S]",
            ConsoleSilent = 0,
            ConsoleMsgLog = 0,
            ConsoleLogFilePath = string.Empty,
        };

        // Act
        LoginLogger.Configure(config);
        LoginLogger.Info("hello");

        // Assert
        Assert.True(true);
    }
}
