using Athena.Net.LoginServer.Config;

namespace Athena.Net.LoginServer.Tests.Config;

public sealed class SubnetConfigLoaderTests
{
    [Fact]
    public void Load_ParsesSubnetEntries()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, "subnet_athena.conf");
        File.WriteAllText(filePath, "subnet: 255.255.0.0:10.0.0.1:10.0.0.2\n");

        // Act
        var config = SubnetConfigLoader.Load(filePath);

        // Assert
        Assert.Single(config.Entries);
        var entry = config.Entries[0];
        Assert.Equal(0xFFFF0000u, entry.Mask);
        Assert.Equal(0x0A000001u, entry.CharIp);
        Assert.Equal(0x0A000002u, entry.MapIp);
    }

    [Fact]
    public void Load_ResolvesImportLines()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        var importPath = Path.Combine(tempDir, "import.conf");
        File.WriteAllText(importPath, "subnet: 255.0.0.0:127.0.0.1:127.0.0.1\n");

        var filePath = Path.Combine(tempDir, "subnet_athena.conf");
        File.WriteAllText(filePath, "import: import.conf\n");

        // Act
        var config = SubnetConfigLoader.Load(filePath);

        // Assert
        Assert.Single(config.Entries);
    }
}
