using System.Linq;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.Tests.Net;

/// <summary>
/// Covers MapServer's client-side ServiceToken resolution/validation for the
/// MapServer &lt;-&gt; CharServer HMAC-SHA256 service authentication handshake
/// (see ai/map-server.md, "Inter-server service authentication"). Mirrors
/// CharServer's own CharServerServiceTokenProviderTests.cs for the
/// pre-existing, independent CharServer &lt;-&gt; LoginServer handshake.
/// Deterministic 32-byte test tokens only - never a real secret.
/// </summary>
public sealed class MapServerServiceTokenProviderTests
{
    private static readonly string ValidTokenBase64 = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

    [Fact]
    public void MissingToken_IsNotConfigured()
    {
        var provider = new MapServerServiceTokenProvider(new SecretConfig());

        Assert.False(provider.IsConfigured);
        Assert.Null(provider.TokenBytes);
    }

    [Fact]
    public void ValidToken_IsConfigured()
    {
        var provider = new MapServerServiceTokenProvider(new SecretConfig { MapServerServiceToken = ValidTokenBase64 });

        Assert.True(provider.IsConfigured);
        Assert.Equal(Convert.FromBase64String(ValidTokenBase64), provider.TokenBytes);
    }

    [Fact]
    public void InvalidBase64_IsNotConfigured()
    {
        var provider = new MapServerServiceTokenProvider(new SecretConfig { MapServerServiceToken = "not base64!!" });

        Assert.False(provider.IsConfigured);
        Assert.Null(provider.TokenBytes);
    }

    [Fact]
    public void TokenShorterThan32Bytes_IsNotConfigured()
    {
        var shortToken = Convert.ToBase64String(Enumerable.Range(0, 31).Select(i => (byte)i).ToArray());
        var provider = new MapServerServiceTokenProvider(new SecretConfig { MapServerServiceToken = shortToken });

        Assert.False(provider.IsConfigured);
        Assert.Null(provider.TokenBytes);
    }

    [Fact]
    public void ExactlyMinimumLength_IsConfigured()
    {
        var exactToken = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        var provider = new MapServerServiceTokenProvider(new SecretConfig { MapServerServiceToken = exactToken });

        Assert.True(provider.IsConfigured);
    }

    [Fact]
    public void EnvironmentVariable_TakesPriorityOverSecretFile()
    {
        var envVar = MapServerServiceTokenProvider.EnvironmentVariableName;
        var previous = Environment.GetEnvironmentVariable(envVar);
        var envToken = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(i + 5)).ToArray());
        try
        {
            Environment.SetEnvironmentVariable(envVar, envToken);
            var provider = new MapServerServiceTokenProvider(new SecretConfig { MapServerServiceToken = ValidTokenBase64 });

            Assert.True(provider.IsConfigured);
            Assert.Equal(Convert.FromBase64String(envToken), provider.TokenBytes);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, previous);
        }
    }

    [Fact]
    public void NoFallbackToCharServerOrLegacyCredentials()
    {
        // MapServer's ServiceToken must never fall back to anything else -
        // an empty SecretConfig (no MapServerServiceToken at all, and no
        // legacy UserId/Password concept exists on this type any more) must
        // always be "not configured", never silently treated as valid.
        var provider = new MapServerServiceTokenProvider(new SecretConfig());

        Assert.False(provider.IsConfigured);
    }
}
