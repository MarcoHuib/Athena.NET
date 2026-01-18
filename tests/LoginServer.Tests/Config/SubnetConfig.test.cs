using System.Net;
using Athena.Net.LoginServer.Config;

namespace Athena.Net.LoginServer.Tests.Config;

public sealed class SubnetConfigTests
{
    [Fact]
    public void TryGetCharIp_UsesMaskedMatch()
    {
        // Arrange
        var config = new SubnetConfig();
        var mask = ToUInt32(IPAddress.Parse("255.0.0.0"));
        var charIp = ToUInt32(IPAddress.Parse("127.0.0.1"));
        config.Add(new SubnetEntry(mask, charIp, 0));

        // Act
        var matched = config.TryGetCharIp(IPAddress.Parse("127.1.2.3"), out var resolved);

        // Assert
        Assert.True(matched);
        Assert.Equal(IPAddress.Parse("127.0.0.1"), resolved);
    }

    [Fact]
    public void TryGetCharIp_ReturnsFalseWhenNoMatch()
    {
        // Arrange
        var config = new SubnetConfig();
        var mask = ToUInt32(IPAddress.Parse("255.0.0.0"));
        var charIp = ToUInt32(IPAddress.Parse("10.0.0.1"));
        config.Add(new SubnetEntry(mask, charIp, 0));

        // Act
        var matched = config.TryGetCharIp(IPAddress.Parse("192.168.1.2"), out _);

        // Assert
        Assert.False(matched);
    }

    private static uint ToUInt32(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
    }
}
