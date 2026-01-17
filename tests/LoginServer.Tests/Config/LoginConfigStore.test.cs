using Athena.Net.LoginServer.Config;

namespace Athena.Net.LoginServer.Tests.Config;

public sealed class LoginConfigStoreTests
{
    [Fact]
    public void UpdateLoginCaseSensitive_UpdatesStoreValue()
    {
        // Arrange
        var config = new LoginConfig();
        var store = new LoginConfigStore(config, loginCaseSensitive: false);

        // Act
        store.UpdateLoginCaseSensitive(true);

        // Assert
        Assert.True(store.LoginCaseSensitive);
    }
}
