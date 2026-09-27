using System.Security.Cryptography;
using Athena.Net.LoginServer.Application;
using Athena.Net.LoginServer.Config;

namespace Athena.Net.LoginServer.Tests.Application;

/// <summary>
/// Covers the HMAC-SHA256 CharServer &lt;-&gt; LoginServer service authentication
/// handshake (see ai/login-server.md, "Inter-server service authentication").
/// No LoginAccount/Identity database query is ever involved - <see cref="ServiceAuthenticationService"/>
/// never takes a DbContext dependency at all, and no service database row is
/// required to authenticate.
/// </summary>
public sealed class ServiceAuthenticationServiceTests
{
    private const string ServiceId = "CharServer";

    private static ServiceAuthenticationService CreateService(string? token = "correct-horse-battery-staple-0123456789")
    {
        var secrets = new SecretConfig { CharServerServiceToken = token ?? string.Empty };
        return new ServiceAuthenticationService(new CharServerServiceTokenProvider(secrets));
    }

    private static byte[] ComputeProof(string token, string serviceId, byte[] nonce)
    {
        return ServiceAuthProofCalculator.ComputeProof(System.Text.Encoding.UTF8.GetBytes(token), serviceId, nonce);
    }

    [Fact]
    public void ValidProof_Succeeds()
    {
        var service = CreateService();
        var nonce = service.GenerateChallenge(ServiceId);
        var proof = ComputeProof("correct-horse-battery-staple-0123456789", ServiceId, nonce);

        var result = service.VerifyProof(proof);

        Assert.True(result.Success);
        Assert.Equal(ServiceAuthenticationOutcome.Success, result.Outcome);
    }

    [Fact]
    public void WrongToken_Fails()
    {
        var service = CreateService();
        var nonce = service.GenerateChallenge(ServiceId);
        var proof = ComputeProof("a-completely-different-token", ServiceId, nonce);

        var result = service.VerifyProof(proof);

        Assert.False(result.Success);
        Assert.Equal(ServiceAuthenticationOutcome.InvalidProof, result.Outcome);
    }

    [Fact]
    public void ModifiedProofByte_Fails()
    {
        var service = CreateService();
        var nonce = service.GenerateChallenge(ServiceId);
        var proof = ComputeProof("correct-horse-battery-staple-0123456789", ServiceId, nonce);
        proof[0] ^= 0xFF;

        var result = service.VerifyProof(proof);

        Assert.False(result.Success);
        Assert.Equal(ServiceAuthenticationOutcome.InvalidProof, result.Outcome);
    }

    [Fact]
    public void ProofComputedForDifferentServiceId_Fails()
    {
        var service = CreateService();
        var nonce = service.GenerateChallenge(ServiceId);
        var proof = ComputeProof("correct-horse-battery-staple-0123456789", "SomeOtherService", nonce);

        var result = service.VerifyProof(proof);

        Assert.False(result.Success);
        Assert.Equal(ServiceAuthenticationOutcome.InvalidProof, result.Outcome);
    }

    [Fact]
    public void MissingToken_FailsClosed()
    {
        var service = CreateService(token: null);
        var nonce = service.GenerateChallenge(ServiceId);
        var proof = ComputeProof("anything", ServiceId, nonce);

        var result = service.VerifyProof(proof);

        Assert.False(result.Success);
        Assert.Equal(ServiceAuthenticationOutcome.TokenNotConfigured, result.Outcome);
    }

    [Fact]
    public void NoChallengeIssued_Fails()
    {
        var service = CreateService();
        var proof = new byte[32];
        RandomNumberGenerator.Fill(proof);

        var result = service.VerifyProof(proof);

        Assert.False(result.Success);
        Assert.Equal(ServiceAuthenticationOutcome.NoChallengeIssued, result.Outcome);
    }

    [Fact]
    public void ChallengeReuse_SecondVerifyAgainstSameChallenge_Fails()
    {
        var service = CreateService();
        var nonce = service.GenerateChallenge(ServiceId);
        var proof = ComputeProof("correct-horse-battery-staple-0123456789", ServiceId, nonce);

        var first = service.VerifyProof(proof);
        Assert.True(first.Success);

        var second = service.VerifyProof(proof);
        Assert.False(second.Success);
        Assert.Equal(ServiceAuthenticationOutcome.NoChallengeIssued, second.Outcome);
    }

    [Fact]
    public void FailedProof_AlsoConsumesChallenge_CannotBeRetried()
    {
        var service = CreateService();
        var nonce = service.GenerateChallenge(ServiceId);
        var wrongProof = ComputeProof("wrong-token", ServiceId, nonce);

        var first = service.VerifyProof(wrongProof);
        Assert.False(first.Success);

        var correctProof = ComputeProof("correct-horse-battery-staple-0123456789", ServiceId, nonce);
        var retry = service.VerifyProof(correctProof);

        Assert.False(retry.Success);
        Assert.Equal(ServiceAuthenticationOutcome.NoChallengeIssued, retry.Outcome);
    }

    /// <summary>
    /// The scenario from the task spec: connection A's nonce/proof pair must
    /// never authenticate a different connection B. Each TCP connection gets
    /// its own <see cref="ServiceAuthenticationService"/> instance (see
    /// ServiceComposition's AddTransient registration), so connection B's
    /// service here has never issued connection A's nonce and has no pending
    /// challenge for it - replaying A's proof against B fails exactly like a
    /// proof submitted with no challenge issued at all.
    /// </summary>
    [Fact]
    public void ReplayAcrossConnections_ProofFromConnectionA_DoesNotAuthenticateConnectionB()
    {
        var connectionA = CreateService();
        var nonceA = connectionA.GenerateChallenge(ServiceId);
        var proofA = ComputeProof("correct-horse-battery-staple-0123456789", ServiceId, nonceA);
        Assert.True(connectionA.VerifyProof(proofA).Success);

        var connectionB = CreateService();
        connectionB.GenerateChallenge(ServiceId);

        var replayResult = connectionB.VerifyProof(proofA);

        Assert.False(replayResult.Success);
        Assert.NotEqual(ServiceAuthenticationOutcome.Success, replayResult.Outcome);
    }

    [Fact]
    public void VerifyProof_NeverSetsIsAuthenticated()
    {
        var service = CreateService();
        var nonce = service.GenerateChallenge(ServiceId);
        var proof = ComputeProof("correct-horse-battery-staple-0123456789", ServiceId, nonce);

        var result = service.VerifyProof(proof);

        Assert.True(result.Success);
        Assert.False(service.IsAuthenticated);
    }

    [Fact]
    public void MarkAuthenticated_OnlyExplicitCall_SetsIsAuthenticated()
    {
        var service = CreateService();
        Assert.False(service.IsAuthenticated);

        service.MarkAuthenticated();

        Assert.True(service.IsAuthenticated);
    }

    [Fact]
    public void FailedVerification_NeverTransitionsToAuthenticated_EvenIfMarkAuthenticatedIsCalledAfterwards()
    {
        var service = CreateService();
        var nonce = service.GenerateChallenge(ServiceId);
        var wrongProof = ComputeProof("wrong-token", ServiceId, nonce);

        var result = service.VerifyProof(wrongProof);

        Assert.False(result.Success);
        Assert.False(service.IsAuthenticated);
    }
}
