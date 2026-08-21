using Athena.Net.CharServer.Config;

namespace Athena.Net.CharServer.Tests.Config;

public sealed class CharConfigPinTests
{
    [Theory]
    [InlineData("yes", true)]
    [InlineData("no", false)]
    public void Load_ParsesPincodeEnabled(string value, bool expected)
    {
        var path = WriteConfig($"pincode_enabled: {value}\n");
        try
        {
            Assert.Equal(expected, CharConfigLoader.Load(path).PincodeEnabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_LaterImportOverridesMainPincodeSetting()
    {
        var directory = Directory.CreateTempSubdirectory("athena-char-config-");
        var mainPath = Path.Combine(directory.FullName, "char_athena.conf");
        var importPath = Path.Combine(directory.FullName, "char_conf.txt");
        try
        {
            File.WriteAllText(mainPath, "pincode_enabled: yes\nimport: char_conf.txt\n");
            File.WriteAllText(importPath, "pincode_enabled: no\n");

            Assert.False(CharConfigLoader.Load(mainPath).PincodeEnabled);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void SecretMerge_PreservesParsedPinConfiguration()
    {
        var parsed = new CharConfig
        {
            PincodeEnabled = false,
            PincodeForce = false,
        };

        var merged = new SecretConfig().ApplyTo(parsed);

        Assert.False(merged.PincodeEnabled);
        Assert.False(merged.PincodeForce);
    }

    private static string WriteConfig(string contents)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, contents);
        return path;
    }
}
