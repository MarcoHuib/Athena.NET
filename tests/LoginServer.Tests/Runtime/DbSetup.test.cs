using System.Reflection;
using Athena.Net.LoginServer.Runtime;

namespace Athena.Net.LoginServer.Tests.Runtime;

public sealed class DbSetupTests
{
    [Fact]
    public void ApplyMySqlCodepage_AppendsCharset_WhenMissing()
    {
        // Arrange
        var method = typeof(DbSetup).GetMethod("ApplyMySqlCodepage", BindingFlags.NonPublic | BindingFlags.Static);
        var baseConnection = "Server=127.0.0.1;Port=3306;Database=db;User=user;Password=pass;SslMode=None;";

        // Act
        var result = (string)method!.Invoke(null, new object[] { "mysql", baseConnection, "utf8mb4" })!;

        // Assert
        Assert.Contains("CharSet=utf8mb4", result, StringComparison.OrdinalIgnoreCase);
    }
}
