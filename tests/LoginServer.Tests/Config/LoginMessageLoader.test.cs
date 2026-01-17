using Athena.Net.LoginServer.Config;

namespace Athena.Net.LoginServer.Tests.Config;

public sealed class LoginMessageLoaderTests
{
    [Fact]
    public void Load_ReturnsCatalog_WithParsedMessages()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, "login_msg.conf");
        File.WriteAllText(filePath, "0: Unregistered ID.\n1: Incorrect Password.\n");

        // Act
        var catalog = LoginMessageLoader.Load(filePath);

        // Assert
        Assert.True(catalog.TryGet(0, out var msg0));
        Assert.Equal("Unregistered ID.", msg0);
        Assert.True(catalog.TryGet(1, out var msg1));
        Assert.Equal("Incorrect Password.", msg1);
    }
}
