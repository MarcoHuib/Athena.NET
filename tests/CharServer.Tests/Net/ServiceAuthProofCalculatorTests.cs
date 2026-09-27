using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Athena.Net.CharServer.Net;

namespace Athena.Net.CharServer.Tests.Net;

/// <summary>
/// CharServer's copy of the HMAC-SHA256 service authentication proof
/// calculator must stay byte-for-byte identical to LoginServer's
/// (Athena.Net.LoginServer.Application.ServiceAuthProofCalculator) - both
/// sides independently compute the same proof from the same shared
/// ServiceToken, nonce, and ServiceId. See ai/login-server.md, "Inter-server
/// service authentication".
/// </summary>
public sealed class ServiceAuthProofCalculatorTests
{
    [Fact]
    public void ComputeProof_IsDeterministic_ForSameInputs()
    {
        var token = Encoding.UTF8.GetBytes("shared-secret-token");
        var nonce = new byte[32];
        RandomNumberGenerator.Fill(nonce);

        var first = ServiceAuthProofCalculator.ComputeProof(token, "CharServer", nonce);
        var second = ServiceAuthProofCalculator.ComputeProof(token, "CharServer", nonce);

        Assert.Equal(first, second);
        Assert.Equal(32, first.Length);
    }

    [Fact]
    public void ComputeProof_DifferentServiceId_ProducesDifferentProof()
    {
        var token = Encoding.UTF8.GetBytes("shared-secret-token");
        var nonce = new byte[32];
        RandomNumberGenerator.Fill(nonce);

        var proofA = ServiceAuthProofCalculator.ComputeProof(token, "CharServerA", nonce);
        var proofB = ServiceAuthProofCalculator.ComputeProof(token, "CharServerB", nonce);

        Assert.NotEqual(proofA, proofB);
    }

    [Fact]
    public void ComputeProof_DifferentNonce_ProducesDifferentProof()
    {
        var token = Encoding.UTF8.GetBytes("shared-secret-token");
        var nonceA = new byte[32];
        var nonceB = new byte[32];
        RandomNumberGenerator.Fill(nonceA);
        RandomNumberGenerator.Fill(nonceB);

        var proofA = ServiceAuthProofCalculator.ComputeProof(token, "CharServer", nonceA);
        var proofB = ServiceAuthProofCalculator.ComputeProof(token, "CharServer", nonceB);

        Assert.NotEqual(proofA, proofB);
    }

    /// <summary>
    /// Fixed-vector parity check against a hand-computed expected value, so a
    /// change to either side's message construction (context string, field
    /// separator, byte ordering) that breaks interoperability with the other
    /// side's independent implementation is caught here rather than only at
    /// live handshake time.
    /// </summary>
    [Fact]
    public void ComputeProof_MatchesHandComputedVector()
    {
        var token = Encoding.UTF8.GetBytes("fixed-test-token");
        var nonce = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        const string serviceId = "CharServer";

        var contextBytes = Encoding.UTF8.GetBytes("Athena.NET/CharServer/Auth/v1");
        var serviceIdBytes = Encoding.UTF8.GetBytes(serviceId);
        var message = new byte[contextBytes.Length + 1 + serviceIdBytes.Length + 1 + nonce.Length];
        var offset = 0;
        contextBytes.CopyTo(message, offset);
        offset += contextBytes.Length;
        message[offset++] = 0x1F;
        serviceIdBytes.CopyTo(message, offset);
        offset += serviceIdBytes.Length;
        message[offset++] = 0x1F;
        nonce.CopyTo(message, offset);

        using var hmac = new HMACSHA256(token);
        var expected = hmac.ComputeHash(message);

        var actual = ServiceAuthProofCalculator.ComputeProof(token, serviceId, nonce);

        Assert.Equal(expected, actual);
    }
}
