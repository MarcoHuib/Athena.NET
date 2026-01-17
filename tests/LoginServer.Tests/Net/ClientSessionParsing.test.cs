using System.Reflection;

namespace Athena.Net.LoginServer.Tests.Net;

public sealed class ClientSessionParsingTests
{
    [Theory]
    [InlineData("test_M", "test", 'M')]
    [InlineData("test_f", "test", 'F')]
    [InlineData("test_X", null, null)]
    public void ParseAutoRegister_ReturnsExpected(string input, string? expectedId, char? expectedSex)
    {
        // Arrange
        var type = Type.GetType("Athena.Net.LoginServer.Net.ClientSession, LoginServer");
        var method = type?.GetMethod("ParseAutoRegister", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        // Act
        var result = (ValueTuple<string?, char?>)method!.Invoke(null, new object[] { input })!;

        // Assert
        Assert.Equal(expectedId, result.Item1);
        Assert.Equal(expectedSex, result.Item2);
    }
}
