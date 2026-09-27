using System.Security.Cryptography;
using System.Text;
using System.Net;
using Athena.Net.CharServer.Net;

namespace Athena.Net.CharServer.Tests.Net;

/// <summary>
/// End-to-end proof that the ServiceId/ServerName value CharServer binds
/// into the HMAC-SHA256 service-auth proof is exactly the same value
/// LoginServer will decode off the wire (see
/// ServiceHelloFieldValidatorTests for the input-rejection side of this
/// fix, and ai/login-server.md, "Inter-server service authentication").
/// Uses <see cref="LoginServerConnector"/>'s own internal
/// WriteFixedString/ReadFixedString wire-encoding functions directly (not a
/// re-implementation), so a future change to that encoding that
/// reintroduces the divergence would fail these tests.
/// </summary>
public sealed class ServiceHelloWireCanonicalizationTests
{
    private static readonly byte[] Token = Encoding.UTF8.GetBytes("shared-secret-token-at-least-32-bytes!!");
    private static readonly IPAddress DefaultIp = IPAddress.Parse("10.0.0.5");

    [Theory]
    [InlineData("CharServer")]
    [InlineData("Char-Server_1")]
    public void ValidatedServiceId_WireRoundTrip_IsLossless(string serviceId)
    {
        Assert.True(ServiceHelloFieldValidator.TryValidate(serviceId, "Chaos", out var fields, out var error), error);

        var buffer = new byte[PacketConstants.NameLength];
        LoginServerConnector.WriteFixedString(buffer, fields.ServiceId);
        var decoded = LoginServerConnector.ReadFixedString(buffer);

        Assert.Equal(fields.ServiceId, decoded);
    }

    [Fact]
    public void ValidatedServiceId_AtMaximumLength_WireRoundTrip_IsLossless()
    {
        // Exactly 23 characters: fills every byte except the field's last
        // (NUL) byte - the boundary case where WriteFixedString clears
        // exactly one trailing byte and ReadFixedString's NUL search must
        // still find it.
        var serviceId = new string('A', PacketConstants.NameLength - 1);
        Assert.True(ServiceHelloFieldValidator.TryValidate(serviceId, "Chaos", out var fields, out var error), error);

        var buffer = new byte[PacketConstants.NameLength];
        LoginServerConnector.WriteFixedString(buffer, fields.ServiceId);
        var decoded = LoginServerConnector.ReadFixedString(buffer);

        Assert.Equal(serviceId, decoded);
    }

    [Theory]
    [InlineData("Chaos")]
    [InlineData("Chaos! (EU)")]
    public void ValidatedServerName_WireRoundTrip_IsLossless(string serverName)
    {
        Assert.True(ServiceHelloFieldValidator.TryValidate("CharServer", serverName, out var fields, out var error), error);

        var buffer = new byte[PacketConstants.ServerNameLength];
        LoginServerConnector.WriteFixedString(buffer, fields.ServerName);
        var decoded = LoginServerConnector.ReadFixedString(buffer);

        Assert.Equal(fields.ServerName, decoded);
    }

    /// <summary>
    /// Simulates the whole path: CharServer validates its config, writes
    /// LcServiceHello via the real wire-encoding function, LoginServer
    /// decodes those exact bytes via the real wire-decoding function, and
    /// both sides compute the HMAC-SHA256 proof - CharServer from its
    /// pre-wire canonical values, "LoginServer" from what it actually
    /// decoded. With the fix, these must be byte-for-byte identical.
    /// </summary>
    [Fact]
    public void CanonicalHelloValues_UsedForWireSerializationAndHmac_ProduceMatchingProof()
    {
        Assert.True(ServiceHelloFieldValidator.TryValidate("MyCharServer1", "MyServerName!", out var fields, out var error), error);

        var serviceIdBuffer = new byte[PacketConstants.NameLength];
        LoginServerConnector.WriteFixedString(serviceIdBuffer, fields.ServiceId);
        var decodedServiceId = LoginServerConnector.ReadFixedString(serviceIdBuffer);

        var serverNameBuffer = new byte[PacketConstants.ServerNameLength];
        LoginServerConnector.WriteFixedString(serverNameBuffer, fields.ServerName);
        var decodedServerName = LoginServerConnector.ReadFixedString(serverNameBuffer);

        var nonce = new byte[32];
        RandomNumberGenerator.Fill(nonce);

        // CharServer's own proof, computed from the canonical (pre-wire) values.
        var senderProof = ServiceAuthProofCalculator.ComputeProof(
            Token, fields.ServiceId, DefaultIp, 6121, fields.ServerName, 0, 0, nonce);

        // The proof LoginServer would independently recompute from what it
        // actually decoded off the wire.
        var receiverProof = ServiceAuthProofCalculator.ComputeProof(
            Token, decodedServiceId, DefaultIp, 6121, decodedServerName, 0, 0, nonce);

        Assert.Equal(senderProof, receiverProof);
    }

    /// <summary>
    /// Tampering with the ServiceId bytes actually on the wire (as an active
    /// attacker who cannot compute the HMAC might attempt) must invalidate
    /// the proof - the receiver recomputes from what it decodes, which no
    /// longer matches what the sender signed.
    /// </summary>
    [Fact]
    public void ChangingOnWireServiceIdBytes_InvalidatesTheProof()
    {
        Assert.True(ServiceHelloFieldValidator.TryValidate("CharServer", "Chaos", out var fields, out var error), error);

        var serviceIdBuffer = new byte[PacketConstants.NameLength];
        LoginServerConnector.WriteFixedString(serviceIdBuffer, fields.ServiceId);

        var nonce = new byte[32];
        RandomNumberGenerator.Fill(nonce);

        var originalProof = ServiceAuthProofCalculator.ComputeProof(
            Token, fields.ServiceId, DefaultIp, 6121, fields.ServerName, 0, 0, nonce);

        // Tamper with a byte on the wire before it would be "received".
        serviceIdBuffer[0] = (byte)'X';
        var tamperedServiceId = LoginServerConnector.ReadFixedString(serviceIdBuffer);
        Assert.NotEqual(fields.ServiceId, tamperedServiceId);

        var tamperedProof = ServiceAuthProofCalculator.ComputeProof(
            Token, tamperedServiceId, DefaultIp, 6121, fields.ServerName, 0, 0, nonce);

        Assert.NotEqual(originalProof, tamperedProof);
    }

    [Fact]
    public void ChangingOnWireServerNameBytes_InvalidatesTheProof()
    {
        Assert.True(ServiceHelloFieldValidator.TryValidate("CharServer", "Chaos", out var fields, out var error), error);

        var serverNameBuffer = new byte[PacketConstants.ServerNameLength];
        LoginServerConnector.WriteFixedString(serverNameBuffer, fields.ServerName);

        var nonce = new byte[32];
        RandomNumberGenerator.Fill(nonce);

        var originalProof = ServiceAuthProofCalculator.ComputeProof(
            Token, fields.ServiceId, DefaultIp, 6121, fields.ServerName, 0, 0, nonce);

        serverNameBuffer[0] = (byte)'X';
        var tamperedServerName = LoginServerConnector.ReadFixedString(serverNameBuffer);
        Assert.NotEqual(fields.ServerName, tamperedServerName);

        var tamperedProof = ServiceAuthProofCalculator.ComputeProof(
            Token, fields.ServiceId, DefaultIp, 6121, tamperedServerName, 0, 0, nonce);

        Assert.NotEqual(originalProof, tamperedProof);
    }
}
