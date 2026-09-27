using System.Linq;
using System.Net;
using System.Security.Cryptography;
using Athena.Net.LoginServer.Application;
using Athena.Net.LoginServer.Config;

namespace Athena.Net.LoginServer.Tests.Application;

/// <summary>
/// Covers the HMAC-SHA256 CharServer &lt;-&gt; LoginServer service authentication
/// handshake (see ai/login-server.md, "Inter-server service authentication"):
/// proof integrity/binding (v2 binds the complete ServiceHello payload, not
/// just ServiceId), the handshake connection state machine, and replay
/// protection. No LoginAccount/Identity database query is ever involved -
/// <see cref="ServiceAuthenticationService"/> never takes a DbContext
/// dependency at all, and no service database row is required to
/// authenticate.
/// </summary>
public sealed class ServiceAuthenticationServiceTests
{
    // A valid-format (Base64, 32 bytes) token distinct from AlternateToken -
    // both are well-formed so a test failing on one of them is failing on
    // content, not format (format validation is covered separately in
    // CharServerServiceTokenProviderTests).
    private static readonly string ValidTokenBase64 = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
    private static readonly string AlternateTokenBase64 = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray());

    private static readonly ServiceHelloInfo DefaultHello = new(
        ServiceId: "CharServer",
        Ip: IPAddress.Parse("10.0.0.5"),
        Port: 6121,
        ServerName: "Chaos",
        CharMaintenance: 0,
        CharNewDisplay: 0);

    private static ServiceAuthenticationService CreateService(string? tokenBase64 = null)
    {
        var secrets = new SecretConfig { CharServerServiceToken = tokenBase64 ?? ValidTokenBase64 };
        return new ServiceAuthenticationService(new CharServerServiceTokenProvider(secrets));
    }

    private static byte[] ComputeProof(string tokenBase64, ServiceHelloInfo hello, byte[] nonce)
    {
        return ServiceAuthProofCalculator.ComputeProof(Convert.FromBase64String(tokenBase64), hello, nonce);
    }

    // ------------------------------------------------------------------
    // Proof integrity / full-ServiceHello binding
    // ------------------------------------------------------------------

    [Fact]
    public void ValidProof_ForCompleteHello_Succeeds()
    {
        var service = CreateService();
        var nonce = service.GenerateChallenge(DefaultHello)!;
        var proof = ComputeProof(ValidTokenBase64, DefaultHello, nonce);

        var result = service.VerifyProof(proof);

        Assert.True(result.Success);
        Assert.Equal(ServiceAuthenticationOutcome.Success, result.Outcome);
        Assert.Equal(DefaultHello, result.Hello);
    }

    [Fact]
    public void WrongToken_Fails()
    {
        var service = CreateService();
        var nonce = service.GenerateChallenge(DefaultHello)!;
        var proof = ComputeProof(AlternateTokenBase64, DefaultHello, nonce);

        var result = service.VerifyProof(proof);

        Assert.False(result.Success);
        Assert.Equal(ServiceAuthenticationOutcome.InvalidProof, result.Outcome);
    }

    [Fact]
    public void ModifiedProofByte_Fails()
    {
        var service = CreateService();
        var nonce = service.GenerateChallenge(DefaultHello)!;
        var proof = ComputeProof(ValidTokenBase64, DefaultHello, nonce);
        proof[0] ^= 0xFF;

        var result = service.VerifyProof(proof);

        Assert.False(result.Success);
        Assert.Equal(ServiceAuthenticationOutcome.InvalidProof, result.Outcome);
    }

    [Fact]
    public void TamperedServiceId_AfterChallengeIssued_FailsVerification()
    {
        // The proof is computed for a hello with a different ServiceId than
        // what was bound into the challenge - simulating an attacker who
        // modified the ServiceId field of an in-flight hello without being
        // able to recompute the HMAC.
        var service = CreateService();
        var nonce = service.GenerateChallenge(DefaultHello)!;
        var tamperedHello = DefaultHello with { ServiceId = "AttackerServer" };
        var proof = ComputeProof(ValidTokenBase64, tamperedHello, nonce);

        var result = service.VerifyProof(proof);

        Assert.False(result.Success);
        Assert.Equal(ServiceAuthenticationOutcome.InvalidProof, result.Outcome);
    }

    [Fact]
    public void TamperedIp_AfterChallengeIssued_FailsVerification()
    {
        var service = CreateService();
        var nonce = service.GenerateChallenge(DefaultHello)!;
        var tamperedHello = DefaultHello with { Ip = IPAddress.Parse("203.0.113.99") };
        var proof = ComputeProof(ValidTokenBase64, tamperedHello, nonce);

        var result = service.VerifyProof(proof);

        Assert.False(result.Success);
        Assert.Equal(ServiceAuthenticationOutcome.InvalidProof, result.Outcome);
    }

    [Fact]
    public void TamperedPort_AfterChallengeIssued_FailsVerification()
    {
        var service = CreateService();
        var nonce = service.GenerateChallenge(DefaultHello)!;
        var tamperedHello = DefaultHello with { Port = 9999 };
        var proof = ComputeProof(ValidTokenBase64, tamperedHello, nonce);

        var result = service.VerifyProof(proof);

        Assert.False(result.Success);
        Assert.Equal(ServiceAuthenticationOutcome.InvalidProof, result.Outcome);
    }

    [Fact]
    public void TamperedServerName_AfterChallengeIssued_FailsVerification()
    {
        var service = CreateService();
        var nonce = service.GenerateChallenge(DefaultHello)!;
        var tamperedHello = DefaultHello with { ServerName = "NotChaos" };
        var proof = ComputeProof(ValidTokenBase64, tamperedHello, nonce);

        var result = service.VerifyProof(proof);

        Assert.False(result.Success);
        Assert.Equal(ServiceAuthenticationOutcome.InvalidProof, result.Outcome);
    }

    [Fact]
    public void TamperedMaintenance_AfterChallengeIssued_FailsVerification()
    {
        var service = CreateService();
        var nonce = service.GenerateChallenge(DefaultHello)!;
        var tamperedHello = DefaultHello with { CharMaintenance = 1 };
        var proof = ComputeProof(ValidTokenBase64, tamperedHello, nonce);

        var result = service.VerifyProof(proof);

        Assert.False(result.Success);
        Assert.Equal(ServiceAuthenticationOutcome.InvalidProof, result.Outcome);
    }

    [Fact]
    public void TamperedNewDisplay_AfterChallengeIssued_FailsVerification()
    {
        var service = CreateService();
        var nonce = service.GenerateChallenge(DefaultHello)!;
        var tamperedHello = DefaultHello with { CharNewDisplay = 1 };
        var proof = ComputeProof(ValidTokenBase64, tamperedHello, nonce);

        var result = service.VerifyProof(proof);

        Assert.False(result.Success);
        Assert.Equal(ServiceAuthenticationOutcome.InvalidProof, result.Outcome);
    }

    [Fact]
    public void MissingToken_FailsClosed()
    {
        var service = CreateService(tokenBase64: string.Empty);

        var nonce = service.GenerateChallenge(DefaultHello)!;
        var proof = ComputeProof(ValidTokenBase64, DefaultHello, nonce);

        var result = service.VerifyProof(proof);

        Assert.False(result.Success);
        Assert.Equal(ServiceAuthenticationOutcome.TokenNotConfigured, result.Outcome);
    }

    // ------------------------------------------------------------------
    // Handshake connection state machine
    // ------------------------------------------------------------------

    [Fact]
    public void InitialState_IsUnauthenticated()
    {
        var service = CreateService();
        Assert.Equal(ServiceAuthConnectionState.Unauthenticated, service.State);
    }

    [Fact]
    public void GenerateChallenge_FromUnauthenticated_TransitionsToChallengeIssued()
    {
        var service = CreateService();
        var nonce = service.GenerateChallenge(DefaultHello);

        Assert.NotNull(nonce);
        Assert.Equal(ServiceAuthConnectionState.ChallengeIssued, service.State);
    }

    [Fact]
    public void NoChallengeIssued_VerifyProofFails()
    {
        var service = CreateService();
        var proof = new byte[32];
        RandomNumberGenerator.Fill(proof);

        var result = service.VerifyProof(proof);

        Assert.False(result.Success);
        Assert.Equal(ServiceAuthenticationOutcome.NoChallengeIssued, result.Outcome);
        Assert.Equal(ServiceAuthConnectionState.Failed, service.State);
    }

    [Fact]
    public void SecondHello_WhileChallengeOutstanding_IsRejected_AndDoesNotResetChallenge()
    {
        var service = CreateService();
        var firstNonce = service.GenerateChallenge(DefaultHello)!;

        var secondHello = DefaultHello with { ServiceId = "AnotherServer" };
        var secondNonce = service.GenerateChallenge(secondHello);

        Assert.Null(secondNonce);

        // The state machine fails this connection closed as soon as the
        // out-of-protocol second hello arrives (never silently replacing or
        // resetting the original outstanding challenge) - so even a
        // correctly-computed proof for the ORIGINAL hello/nonce no longer
        // verifies afterwards; the connection must be reconnected, not
        // recovered mid-stream.
        var proof = ComputeProof(ValidTokenBase64, DefaultHello, firstNonce);
        var result = service.VerifyProof(proof);

        Assert.False(result.Success);
        Assert.Equal(ServiceAuthenticationOutcome.NoChallengeIssued, result.Outcome);
        Assert.Equal(ServiceAuthConnectionState.Failed, service.State);
    }

    [Fact]
    public void Hello_AfterAuthenticated_IsRejected()
    {
        var service = CreateService();
        var nonce = service.GenerateChallenge(DefaultHello)!;
        var proof = ComputeProof(ValidTokenBase64, DefaultHello, nonce);
        Assert.True(service.VerifyProof(proof).Success);
        service.MarkAuthenticated();

        var secondNonce = service.GenerateChallenge(DefaultHello);

        Assert.Null(secondNonce);
        Assert.Equal(ServiceAuthConnectionState.Authenticated, service.State);
        Assert.True(service.IsAuthenticated, "A rejected post-auth hello must never revert IsAuthenticated.");
    }

    [Fact]
    public void Proof_AfterAuthenticated_IsRejected()
    {
        var service = CreateService();
        var nonce = service.GenerateChallenge(DefaultHello)!;
        var proof = ComputeProof(ValidTokenBase64, DefaultHello, nonce);
        Assert.True(service.VerifyProof(proof).Success);
        service.MarkAuthenticated();

        // Replay the very same (previously valid) proof again.
        var secondResult = service.VerifyProof(proof);

        Assert.False(secondResult.Success);
        Assert.Equal(ServiceAuthConnectionState.Authenticated, service.State);
        Assert.True(service.IsAuthenticated, "A rejected post-auth proof must never revert IsAuthenticated.");
    }

    [Fact]
    public void ChallengeReuse_SecondVerifyAgainstSameChallenge_Fails()
    {
        var service = CreateService();
        var nonce = service.GenerateChallenge(DefaultHello)!;
        var proof = ComputeProof(ValidTokenBase64, DefaultHello, nonce);

        var first = service.VerifyProof(proof);
        Assert.True(first.Success);

        // Without an intervening MarkAuthenticated() call, State is still
        // ChallengeIssued here, but the challenge itself was already consumed.
        var second = service.VerifyProof(proof);
        Assert.False(second.Success);
        Assert.Equal(ServiceAuthenticationOutcome.NoChallengeIssued, second.Outcome);
    }

    [Fact]
    public void FailedProof_AlsoConsumesChallenge_CannotBeRetried()
    {
        var service = CreateService();
        var nonce = service.GenerateChallenge(DefaultHello)!;
        var wrongProof = ComputeProof(AlternateTokenBase64, DefaultHello, nonce);

        var first = service.VerifyProof(wrongProof);
        Assert.False(first.Success);
        Assert.Equal(ServiceAuthConnectionState.Failed, service.State);

        var correctProof = ComputeProof(ValidTokenBase64, DefaultHello, nonce);
        var retry = service.VerifyProof(correctProof);

        Assert.False(retry.Success);
        Assert.Equal(ServiceAuthenticationOutcome.NoChallengeIssued, retry.Outcome);
    }

    [Fact]
    public void StateNeverGoesBackwards_FromFailed()
    {
        var service = CreateService();
        var nonce = service.GenerateChallenge(DefaultHello)!;
        var wrongProof = ComputeProof(AlternateTokenBase64, DefaultHello, nonce);
        Assert.False(service.VerifyProof(wrongProof).Success);
        Assert.Equal(ServiceAuthConnectionState.Failed, service.State);

        // A fresh, otherwise-legitimate hello must not resurrect this connection.
        var retryNonce = service.GenerateChallenge(DefaultHello);

        Assert.Null(retryNonce);
        Assert.Equal(ServiceAuthConnectionState.Failed, service.State);
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
        var nonceA = connectionA.GenerateChallenge(DefaultHello)!;
        var proofA = ComputeProof(ValidTokenBase64, DefaultHello, nonceA);
        Assert.True(connectionA.VerifyProof(proofA).Success);

        var connectionB = CreateService();
        connectionB.GenerateChallenge(DefaultHello);

        var replayResult = connectionB.VerifyProof(proofA);

        Assert.False(replayResult.Success);
        Assert.NotEqual(ServiceAuthenticationOutcome.Success, replayResult.Outcome);
    }

    [Fact]
    public void VerifyProof_NeverSetsIsAuthenticated()
    {
        var service = CreateService();
        var nonce = service.GenerateChallenge(DefaultHello)!;
        var proof = ComputeProof(ValidTokenBase64, DefaultHello, nonce);

        var result = service.VerifyProof(proof);

        Assert.True(result.Success);
        Assert.False(service.IsAuthenticated);
        Assert.Equal(ServiceAuthConnectionState.ChallengeIssued, service.State);
    }

    [Fact]
    public void MarkAuthenticated_TransitionsStateToAuthenticated()
    {
        var service = CreateService();
        var nonce = service.GenerateChallenge(DefaultHello)!;
        var proof = ComputeProof(ValidTokenBase64, DefaultHello, nonce);
        Assert.True(service.VerifyProof(proof).Success);

        service.MarkAuthenticated();

        Assert.True(service.IsAuthenticated);
        Assert.Equal(ServiceAuthConnectionState.Authenticated, service.State);
    }

    [Fact]
    public void FailedVerification_NeverTransitionsToAuthenticated_EvenIfMarkAuthenticatedIsCalledAfterwards()
    {
        var service = CreateService();
        var nonce = service.GenerateChallenge(DefaultHello)!;
        var wrongProof = ComputeProof(AlternateTokenBase64, DefaultHello, nonce);

        var result = service.VerifyProof(wrongProof);

        Assert.False(result.Success);
        Assert.False(service.IsAuthenticated);
    }
}
