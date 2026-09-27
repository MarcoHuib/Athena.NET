using Athena.Net.CharServer.Net;

namespace Athena.Net.CharServer.Tests.Net;

/// <summary>
/// Covers pre-handshake validation of CharServer's ServiceId/ServerName
/// registration fields - the fix for the wire-vs-HMAC-input divergence bug:
/// values that could not be represented losslessly in the fixed-width
/// LcServiceHello ASCII fields (too long, non-ASCII, or containing an
/// embedded NUL) must be rejected before service authentication ever
/// attempts to use them, rather than silently truncated/replaced on the
/// wire while the HMAC proof used the original, different value.
/// </summary>
public sealed class ServiceHelloFieldValidatorTests
{
    // PacketConstants.NameLength = 24, one byte reserved for the wire
    // field's NUL terminator -> 23 usable characters.
    private const int MaxServiceIdLength = 23;

    // PacketConstants.ServerNameLength = 20, one byte reserved for the NUL
    // terminator -> 19 usable characters.
    private const int MaxServerNameLength = 19;

    [Fact]
    public void ValidAsciiServiceId_AtMaximumSupportedLength_IsAccepted()
    {
        var serviceId = new string('A', MaxServiceIdLength);

        var accepted = ServiceHelloFieldValidator.TryValidate(serviceId, "Chaos", out var fields, out var error);

        Assert.True(accepted, error);
        Assert.Equal(serviceId, fields.ServiceId);
    }

    [Fact]
    public void ServiceIdOneCharacterTooLong_IsRejectedBeforeAuth()
    {
        var serviceId = new string('A', MaxServiceIdLength + 1);

        var accepted = ServiceHelloFieldValidator.TryValidate(serviceId, "Chaos", out _, out var error);

        Assert.False(accepted);
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("Chär")]
    [InlineData("café")]
    [InlineData("日本語")]
    public void NonAsciiServiceId_IsRejected(string serviceId)
    {
        var accepted = ServiceHelloFieldValidator.TryValidate(serviceId, "Chaos", out _, out var error);

        Assert.False(accepted);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void ValidServerName_AtMaximumSupportedLength_IsAccepted()
    {
        var serverName = new string('B', MaxServerNameLength);

        var accepted = ServiceHelloFieldValidator.TryValidate("CharServer", serverName, out var fields, out var error);

        Assert.True(accepted, error);
        Assert.Equal(serverName, fields.ServerName);
    }

    [Fact]
    public void ServerNameOneCharacterTooLong_IsRejected()
    {
        var serverName = new string('B', MaxServerNameLength + 1);

        var accepted = ServiceHelloFieldValidator.TryValidate("CharServer", serverName, out _, out var error);

        Assert.False(accepted);
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("Chäos")]
    [InlineData("Wörld")]
    public void NonAsciiServerName_IsRejected(string serverName)
    {
        var accepted = ServiceHelloFieldValidator.TryValidate("CharServer", serverName, out _, out var error);

        Assert.False(accepted);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void EmptyServiceId_IsRejected()
    {
        var accepted = ServiceHelloFieldValidator.TryValidate(string.Empty, "Chaos", out _, out var error);

        Assert.False(accepted);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void EmptyServerName_IsRejected()
    {
        var accepted = ServiceHelloFieldValidator.TryValidate("CharServer", string.Empty, out _, out var error);

        Assert.False(accepted);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void NullServiceId_IsRejected()
    {
        var accepted = ServiceHelloFieldValidator.TryValidate(null, "Chaos", out _, out var error);

        Assert.False(accepted);
        Assert.NotEmpty(error);
    }

    /// <summary>
    /// An embedded NUL byte would still ASCII-encode without complaint and
    /// fit within the field width, but the receiving side's NUL-terminated
    /// read would decode a shorter string than the sender's own (non-NUL-aware)
    /// proof computation used - exactly the kind of silent divergence this
    /// validator exists to prevent. Control characters in general (not just
    /// NUL) are rejected by the same printable-ASCII-only rule.
    /// </summary>
    [Fact]
    public void ServiceIdWithEmbeddedNul_IsRejected()
    {
        var serviceId = "Char\0Server";

        var accepted = ServiceHelloFieldValidator.TryValidate(serviceId, "Chaos", out _, out var error);

        Assert.False(accepted);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void ServerNameWithEmbeddedNul_IsRejected()
    {
        var serverName = "Cha\0os";

        var accepted = ServiceHelloFieldValidator.TryValidate("CharServer", serverName, out _, out var error);

        Assert.False(accepted);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void ServiceIdWithControlCharacter_IsRejected()
    {
        var serviceId = "Char\tServer";

        var accepted = ServiceHelloFieldValidator.TryValidate(serviceId, "Chaos", out _, out var error);

        Assert.False(accepted);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void ValidPrintableAsciiWithSymbols_IsAccepted()
    {
        var accepted = ServiceHelloFieldValidator.TryValidate("Char-Server_1", "Chaos! (EU)", out var fields, out var error);

        Assert.True(accepted, error);
        Assert.Equal("Char-Server_1", fields.ServiceId);
        Assert.Equal("Chaos! (EU)", fields.ServerName);
    }
}
