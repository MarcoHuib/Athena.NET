using System.Linq;
using Athena.Net.CharServer.Config;
using Athena.Net.CharServer.Net;

namespace Athena.Net.CharServer.Tests.Net;

/// <summary>
/// Covers ServiceToken format/strength validation on the CharServer side -
/// mirrors Athena.Net.LoginServer.Application.CharServerServiceTokenProviderTests.
/// The token must be Base64-encoded and decode to at least 256 bits (32
/// bytes); a missing, malformed, or too-short value must all be treated
/// identically as "not configured".
/// </summary>
public sealed class CharServerServiceTokenProviderTests
{
    private const string EnvVar = CharServerServiceTokenProvider.EnvironmentVariableName;

    private static string ValidBase64Token(int lengthBytes = 32)
    {
        return Convert.ToBase64String(Enumerable.Range(0, lengthBytes).Select(i => (byte)i).ToArray());
    }

    [Fact]
    public void ExactlyMinimumLength_32Bytes_IsAccepted()
    {
        var secrets = new SecretConfig { CharServerServiceToken = ValidBase64Token(32) };

        var provider = new CharServerServiceTokenProvider(secrets);

        Assert.True(provider.IsConfigured);
        Assert.Equal(32, provider.TokenBytes!.Length);
    }

    [Fact]
    public void LongerThanMinimum_IsAccepted()
    {
        var secrets = new SecretConfig { CharServerServiceToken = ValidBase64Token(64) };

        var provider = new CharServerServiceTokenProvider(secrets);

        Assert.True(provider.IsConfigured);
        Assert.Equal(64, provider.TokenBytes!.Length);
    }

    [Fact]
    public void InvalidBase64_IsRejected()
    {
        var secrets = new SecretConfig { CharServerServiceToken = "not-valid-base64!!!" };

        var provider = new CharServerServiceTokenProvider(secrets);

        Assert.False(provider.IsConfigured);
        Assert.Null(provider.TokenBytes);
    }

    [Fact]
    public void DecodedTokenShorterThan32Bytes_IsRejected()
    {
        var secrets = new SecretConfig { CharServerServiceToken = ValidBase64Token(31) };

        var provider = new CharServerServiceTokenProvider(secrets);

        Assert.False(provider.IsConfigured);
        Assert.Null(provider.TokenBytes);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("password")]
    [InlineData("test")]
    public void WeakPlaintextValues_AreRejected(string weakValue)
    {
        var secrets = new SecretConfig { CharServerServiceToken = weakValue };

        var provider = new CharServerServiceTokenProvider(secrets);

        Assert.False(provider.IsConfigured);
    }

    [Fact]
    public void MissingToken_IsRejected()
    {
        var secrets = new SecretConfig { CharServerServiceToken = string.Empty };

        var provider = new CharServerServiceTokenProvider(secrets);

        Assert.False(provider.IsConfigured);
        Assert.Null(provider.TokenBytes);
    }

    [Fact]
    public void EnvironmentVariable_OverridesSecretConfigFile()
    {
        var envToken = ValidBase64Token(32);
        var fileToken = ValidBase64Token(48);
        Environment.SetEnvironmentVariable(EnvVar, envToken);
        try
        {
            var secrets = new SecretConfig { CharServerServiceToken = fileToken };

            var provider = new CharServerServiceTokenProvider(secrets);

            Assert.True(provider.IsConfigured);
            Assert.Equal(Convert.FromBase64String(envToken), provider.TokenBytes);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVar, null);
        }
    }
}
