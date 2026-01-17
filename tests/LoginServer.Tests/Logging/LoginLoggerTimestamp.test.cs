using System.Text;
using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Logging;

namespace Athena.Net.LoginServer.Tests.Logging;

public sealed class LoginLoggerTimestampTests
{
    [Fact]
    public void Info_PrefixesTimestamp_WhenFormatProvided()
    {
        // Arrange
        var writer = new StringWriter(new StringBuilder());
        var originalOut = Console.Out;
        Console.SetOut(writer);

        var config = new LoginConfig
        {
            TimestampFormat = "[%Y]",
            ConsoleSilent = 0,
            ConsoleMsgLog = 0,
            ConsoleLogFilePath = string.Empty,
        };
        LoginLogger.Configure(config);

        // Act
        LoginLogger.Info("hello");
        Console.Out.Flush();

        // Assert
        var output = writer.ToString().Trim();
        Assert.Contains("hello", output, StringComparison.Ordinal);
        Assert.StartsWith("[", output, StringComparison.Ordinal);

        // Cleanup
        Console.SetOut(originalOut);
    }
}
