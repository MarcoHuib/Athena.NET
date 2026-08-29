using System.Net;
using System.Text;
using Athena.Net.Launcher.Core;

namespace Athena.Net.Launcher.Core.Tests;

public sealed class ConfigurationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void InvalidPortsAreRejected(int port) => Assert.Throws<InvalidOperationException>(() => new LauncherOptions { AthenaHost = "server.example", LoginTargetPort = port }.Validate());

    [Fact]
    public void DuplicateCharacterAndMapEndpointsAreRejected() => Assert.Throws<InvalidOperationException>(() => new LauncherOptions
    {
        AthenaHost = "server.example",
        CharacterListenAddress = "198.18.0.1", MapListenAddress = "198.18.0.1", CharacterListenPort = 4500, MapListenPort = 4500,
    }.Validate());

    [Theory]
    [InlineData("", "6800")]
    [InlineData("login.example", "0")]
    [InlineData("bad host", "6800")]
    public void InvalidLoginEndpointIsRejected(string host, string port)
    {
        var xml = Encoding.UTF8.GetBytes($"<clientinfo><connection><address>{host}</address><port>{port}</port></connection></clientinfo>");
        Assert.Throws<InvalidOperationException>(() => RagnarokClientConfigurationReader.Parse(xml, "fixture"));
    }

    [Fact]
    public async Task ConfigurationReaderPrefersEffectiveSclientinfo()
    {
        var data = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["data\\clientinfo.xml"] = Xml("old.example", 6800),
            ["data\\sclientinfo.xml"] = Xml("new.example", 6801),
        };
        var reader = new RagnarokClientConfigurationReader(new FakeSourceFactory(data), new NullLog());
        var endpoint = await reader.ReadAsync(Fakes.Installation, CancellationToken.None);
        Assert.Equal(new RagnarokLoginEndpoint("new.example", 6801), endpoint);
    }

    private static byte[] Xml(string host, int port) => Encoding.UTF8.GetBytes($"<clientinfo><connection><address>{host}</address><port>{port}</port></connection></clientinfo>");

    private sealed class FakeSourceFactory(Dictionary<string, byte[]> data) : IClientDataSourceFactory
    {
        public IClientDataSource Open(RagnarokInstallation installation) => new FakeSource(data);
    }
    private sealed class FakeSource(Dictionary<string, byte[]> data) : IClientDataSource
    {
        public bool TryRead(string relativePath, out byte[] bytes, out string source) { source = relativePath; return data.TryGetValue(relativePath, out bytes!); }
        public void Dispose() { }
    }
}

internal static class Fakes
{
    public static readonly RagnarokInstallation Installation = new("C:\\RO", "C:\\RO\\Ragexe.exe", "C:\\RO\\EasyAntiCheat.exe", "C:\\RO\\Ragnarok.exe", "C:\\RO\\data.ini");
}

internal sealed class NullLog : ILauncherLog
{
    public void Information(string eventName, string message, IReadOnlyDictionary<string, object?>? properties = null) { }
    public void Error(string eventName, Exception exception, string message) { }
}
