using System.Reflection;
using Athena.Net.LoginServer.Net;

namespace Athena.Net.LoginServer.Tests.Net;

public sealed class ClientSessionTokenTests
{
    [Fact]
    public void GenerateWebAuthToken_ReturnsHexToken()
    {
        // Arrange
        var method = typeof(ClientSession).GetMethod("GenerateWebAuthToken", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        // Act
        var token = (string)method!.Invoke(null, Array.Empty<object>())!;

        // Assert
        Assert.Equal(16, token.Length);
        Assert.Matches("^[0-9a-f]+$", token);
    }
}
