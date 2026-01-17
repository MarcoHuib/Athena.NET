using Athena.Net.LoginServer.Config;

namespace Athena.Net.LoginServer.Tests.Config;

public sealed class LoginMessageStoreTests
{
    [Fact]
    public void Reload_UpdatesCatalog()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, "login_msg.conf");
        File.WriteAllText(filePath, "0: One\n");
        var store = new LoginMessageStore(LoginMessageLoader.Load(filePath), filePath);

        // Act
        File.WriteAllText(filePath, "0: Two\n");
        var reloaded = store.Reload();

        // Assert
        Assert.True(reloaded);
        Assert.True(store.Current.TryGet(0, out var msg));
        Assert.Equal("Two", msg);
    }
}
