using System.Buffers.Binary;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Athena.Net.CharServer.Net;

namespace Athena.Net.CharServer.Tests.Net;

/// <summary>
/// CharServer's copy of the HMAC-SHA256 service authentication proof
/// calculator must stay byte-for-byte identical to LoginServer's
/// (Athena.Net.LoginServer.Application.ServiceAuthProofCalculator) - both
/// sides independently compute the same proof from the same shared
/// ServiceToken and the complete ServiceHello registration payload (v2:
/// ServiceId, IP, port, server name, maintenance, new-display - not just
/// ServiceId). See ai/login-server.md, "Inter-server service authentication".
/// </summary>
public sealed class ServiceAuthProofCalculatorTests
{
    private static readonly byte[] Token = Encoding.UTF8.GetBytes("shared-secret-token-at-least-32-bytes!!");
    private static readonly IPAddress DefaultIp = IPAddress.Parse("10.0.0.5");

    private static byte[] ComputeDefaultProof(byte[] nonce, string serviceId = "CharServer", IPAddress? ip = null, ushort port = 6121, string serverName = "Chaos", ushort maintenance = 0, ushort newDisplay = 0)
    {
        return ServiceAuthProofCalculator.ComputeProof(Token, serviceId, ip ?? DefaultIp, port, serverName, maintenance, newDisplay, nonce);
    }

    [Fact]
    public void ComputeProof_IsDeterministic_ForSameInputs()
    {
        var nonce = new byte[32];
        RandomNumberGenerator.Fill(nonce);

        var first = ComputeDefaultProof(nonce);
        var second = ComputeDefaultProof(nonce);

        Assert.Equal(first, second);
        Assert.Equal(32, first.Length);
    }

    [Fact]
    public void ComputeProof_DifferentServiceId_ProducesDifferentProof()
    {
        var nonce = new byte[32];
        RandomNumberGenerator.Fill(nonce);

        var proofA = ComputeDefaultProof(nonce, serviceId: "CharServerA");
        var proofB = ComputeDefaultProof(nonce, serviceId: "CharServerB");

        Assert.NotEqual(proofA, proofB);
    }

    [Fact]
    public void ComputeProof_DifferentIp_ProducesDifferentProof()
    {
        var nonce = new byte[32];
        RandomNumberGenerator.Fill(nonce);

        var proofA = ComputeDefaultProof(nonce, ip: IPAddress.Parse("10.0.0.5"));
        var proofB = ComputeDefaultProof(nonce, ip: IPAddress.Parse("10.0.0.6"));

        Assert.NotEqual(proofA, proofB);
    }

    [Fact]
    public void ComputeProof_DifferentPort_ProducesDifferentProof()
    {
        var nonce = new byte[32];
        RandomNumberGenerator.Fill(nonce);

        var proofA = ComputeDefaultProof(nonce, port: 6121);
        var proofB = ComputeDefaultProof(nonce, port: 6122);

        Assert.NotEqual(proofA, proofB);
    }

    [Fact]
    public void ComputeProof_DifferentServerName_ProducesDifferentProof()
    {
        var nonce = new byte[32];
        RandomNumberGenerator.Fill(nonce);

        var proofA = ComputeDefaultProof(nonce, serverName: "Chaos");
        var proofB = ComputeDefaultProof(nonce, serverName: "NotChaos");

        Assert.NotEqual(proofA, proofB);
    }

    [Fact]
    public void ComputeProof_DifferentMaintenance_ProducesDifferentProof()
    {
        var nonce = new byte[32];
        RandomNumberGenerator.Fill(nonce);

        var proofA = ComputeDefaultProof(nonce, maintenance: 0);
        var proofB = ComputeDefaultProof(nonce, maintenance: 1);

        Assert.NotEqual(proofA, proofB);
    }

    [Fact]
    public void ComputeProof_DifferentNewDisplay_ProducesDifferentProof()
    {
        var nonce = new byte[32];
        RandomNumberGenerator.Fill(nonce);

        var proofA = ComputeDefaultProof(nonce, newDisplay: 0);
        var proofB = ComputeDefaultProof(nonce, newDisplay: 1);

        Assert.NotEqual(proofA, proofB);
    }

    [Fact]
    public void ComputeProof_DifferentNonce_ProducesDifferentProof()
    {
        var nonceA = new byte[32];
        var nonceB = new byte[32];
        RandomNumberGenerator.Fill(nonceA);
        RandomNumberGenerator.Fill(nonceB);

        var proofA = ComputeDefaultProof(nonceA);
        var proofB = ComputeDefaultProof(nonceB);

        Assert.NotEqual(proofA, proofB);
    }

    /// <summary>
    /// Fixed-vector parity check against a hand-computed expected value, so a
    /// change to either side's message construction (context string, field
    /// separator, length-prefix, byte ordering) that breaks interoperability
    /// with the other side's independent implementation is caught here
    /// rather than only at live handshake time.
    /// </summary>
    [Fact]
    public void ComputeProof_MatchesHandComputedVector()
    {
        var token = Encoding.UTF8.GetBytes("fixed-test-token");
        var nonce = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        const string serviceId = "CharServer";
        const string serverName = "Chaos";
        var ip = IPAddress.Parse("10.0.0.5");
        const ushort port = 6121;
        const ushort maintenance = 0;
        const ushort newDisplay = 0;

        var contextBytes = Encoding.UTF8.GetBytes("Athena.NET/CharServer/Auth/v2");
        var serviceIdBytes = Encoding.UTF8.GetBytes(serviceId);
        var serverNameBytes = Encoding.UTF8.GetBytes(serverName);
        var ipBytes = ip.GetAddressBytes();

        var message = new byte[contextBytes.Length + 1
            + 1 + serviceIdBytes.Length + 1
            + ipBytes.Length + 2
            + 1 + serverNameBytes.Length + 1
            + 2 + 2
            + nonce.Length];
        var offset = 0;

        contextBytes.CopyTo(message, offset);
        offset += contextBytes.Length;
        message[offset++] = 0x1F;

        message[offset++] = (byte)serviceIdBytes.Length;
        serviceIdBytes.CopyTo(message, offset);
        offset += serviceIdBytes.Length;
        message[offset++] = 0x1F;

        ipBytes.CopyTo(message, offset);
        offset += ipBytes.Length;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(offset, 2), port);
        offset += 2;

        message[offset++] = (byte)serverNameBytes.Length;
        serverNameBytes.CopyTo(message, offset);
        offset += serverNameBytes.Length;
        message[offset++] = 0x1F;

        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(offset, 2), maintenance);
        offset += 2;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(offset, 2), newDisplay);
        offset += 2;

        nonce.CopyTo(message, offset);

        using var hmac = new HMACSHA256(token);
        var expected = hmac.ComputeHash(message);

        var actual = ServiceAuthProofCalculator.ComputeProof(token, serviceId, ip, port, serverName, maintenance, newDisplay, nonce);

        Assert.Equal(expected, actual);
    }
}
