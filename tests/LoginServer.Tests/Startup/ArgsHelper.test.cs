using Athena.Net.LoginServer.Startup;

namespace Athena.Net.LoginServer.Tests.Startup;

public sealed class ArgsHelperTests
{
    [Fact]
    public void GetValue_ReturnsMatchingValue_WhenKeyExists()
    {
        // Arrange
        var args = new[] { "--login-config", "conf/custom.conf", "--other", "value" };

        // Act
        var result = ArgsHelper.GetValue(args, "--login-config");

        // Assert
        Assert.Equal("conf/custom.conf", result);
    }

    [Fact]
    public void HasFlag_ReturnsTrue_WhenFlagPresent()
    {
        // Arrange
        var args = new[] { "--self-test", "--auto-migrate" };

        // Act
        var result = ArgsHelper.HasFlag(args, "--self-test");

        // Assert
        Assert.True(result);
    }
}
