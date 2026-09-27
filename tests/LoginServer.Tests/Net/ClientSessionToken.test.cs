using System.Reflection;
using Athena.Net.LoginServer.Application;

namespace Athena.Net.LoginServer.Tests.Net;

/// <summary>
/// WebAuthToken generation now lives solely in IdentityPlayerAuthenticationService
/// (ClientSession's own copy, and the legacy LoginAccount-based retry helper it
/// called, were dead code - service accounts never have web auth tokens - and
/// were removed once the disable path was fixed to target AthenaGameAccount).
/// </summary>
public sealed class ClientSessionTokenTests
{
    [Fact]
    public void GenerateWebAuthToken_ReturnsHexToken()
    {
        // Arrange
        var method = typeof(IdentityPlayerAuthenticationService).GetMethod("GenerateWebAuthToken", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        // Act
        var token = (string)method!.Invoke(null, Array.Empty<object>())!;

        // Assert
        Assert.Equal(16, token.Length);
        Assert.Matches("^[0-9a-f]+$", token);
    }
}
