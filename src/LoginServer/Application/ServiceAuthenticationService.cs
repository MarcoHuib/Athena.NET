using System.Security.Cryptography;
using Athena.Net.LoginServer.Net;

namespace Athena.Net.LoginServer.Application;

/// <summary>
/// Default per-connection <see cref="IServiceAuthenticationService"/>: HMAC-SHA256
/// challenge/response over the configured CharServer ServiceToken, binding
/// the complete ServiceHello registration payload into the proof (see
/// <see cref="ServiceAuthProofCalculator"/>). A fresh instance is created per
/// TCP connection (see ServiceComposition), so the connection state, the
/// one-outstanding-challenge state, and the IsAuthenticated flag are
/// naturally scoped to a single connection - one connection's issued
/// nonce/proof can never authenticate a different connection.
/// <para>
/// Enforces the handshake state machine (<see cref="ServiceAuthConnectionState"/>):
/// a hello is only accepted from <c>Unauthenticated</c>, a proof only from
/// <c>ChallengeIssued</c>, and <see cref="MarkAuthenticated"/> only succeeds
/// from <c>ProofVerified</c> - the state <see cref="VerifyProof"/> transitions
/// to on success. This means <see cref="MarkAuthenticated"/> cannot be misused
/// to reach <c>Authenticated</c> without an immediately-preceding successful
/// <see cref="VerifyProof"/> call on this same instance: calling it from
/// <c>Unauthenticated</c>, <c>ChallengeIssued</c>, <c>Failed</c>, or again
/// after already <c>Authenticated</c> all safely no-op (return <c>false</c>,
/// no state change). Any other invalid attempt - a second hello while a
/// challenge is outstanding, a hello or proof after authentication, or a
/// proof with no outstanding challenge - fails closed and moves the state to
/// the terminal <c>Failed</c> state (never silently resets to a fresh
/// challenge, and never goes backwards from <c>Authenticated</c>).
/// </para>
/// </summary>
public sealed class ServiceAuthenticationService : IServiceAuthenticationService
{
    private static readonly TimeSpan ChallengeTimeout = TimeSpan.FromSeconds(30);

    private readonly CharServerServiceTokenProvider _tokenProvider;
    private PendingChallenge? _pendingChallenge;

    public ServiceAuthenticationService(CharServerServiceTokenProvider tokenProvider)
    {
        _tokenProvider = tokenProvider;
    }

    public bool IsAuthenticated { get; private set; }

    public string? ServiceId { get; private set; }

    public ServiceAuthConnectionState State { get; private set; } = ServiceAuthConnectionState.Unauthenticated;

    public byte[]? GenerateChallenge(ServiceHelloInfo hello)
    {
        if (State == ServiceAuthConnectionState.Authenticated)
        {
            // State must never go backwards from Authenticated: reject this
            // hello outright without touching State/IsAuthenticated. The
            // caller closes the connection regardless - the already-granted
            // trust for THIS connection is simply never revoked by it.
            return null;
        }

        if (State != ServiceAuthConnectionState.Unauthenticated)
        {
            // A second hello while a challenge is outstanding (ChallengeIssued),
            // or one arriving on an already-failed connection, must never
            // silently replace/reset existing state - reject and fail this
            // connection closed rather than issuing (or re-issuing) a challenge.
            State = ServiceAuthConnectionState.Failed;
            return null;
        }

        ServiceId = hello.ServiceId;
        var nonce = RandomNumberGenerator.GetBytes(PacketConstants.ServiceNonceLength);
        _pendingChallenge = new PendingChallenge(hello, nonce, DateTime.UtcNow);
        State = ServiceAuthConnectionState.ChallengeIssued;
        return nonce;
    }

    public ServiceAuthenticationResult VerifyProof(byte[] proof)
    {
        if (State == ServiceAuthConnectionState.Authenticated)
        {
            // Same rule as GenerateChallenge above: never revoke an
            // already-authenticated connection's state just because a
            // (replayed, or otherwise unexpected) proof arrives afterwards.
            return new ServiceAuthenticationResult(ServiceAuthenticationOutcome.NoChallengeIssued);
        }

        if (State != ServiceAuthConnectionState.ChallengeIssued)
        {
            // A proof with no outstanding challenge - never issued, already
            // consumed, already verified once (ProofVerified) and submitted
            // again, or arriving on an already-failed connection - is always
            // rejected - never re-checked against a stale/previous challenge.
            State = ServiceAuthConnectionState.Failed;
            return new ServiceAuthenticationResult(ServiceAuthenticationOutcome.NoChallengeIssued);
        }

        // Consumed unconditionally, before any check below runs: a challenge
        // can be satisfied at most once, whether the proof that consumes it is
        // right or wrong.
        var challenge = _pendingChallenge;
        _pendingChallenge = null;

        if (!_tokenProvider.IsConfigured)
        {
            State = ServiceAuthConnectionState.Failed;
            return new ServiceAuthenticationResult(ServiceAuthenticationOutcome.TokenNotConfigured);
        }

        if (challenge == null)
        {
            State = ServiceAuthConnectionState.Failed;
            return new ServiceAuthenticationResult(ServiceAuthenticationOutcome.NoChallengeIssued);
        }

        if (DateTime.UtcNow - challenge.IssuedAt > ChallengeTimeout)
        {
            State = ServiceAuthConnectionState.Failed;
            return new ServiceAuthenticationResult(ServiceAuthenticationOutcome.ChallengeExpired);
        }

        var expected = ServiceAuthProofCalculator.ComputeProof(_tokenProvider.TokenBytes!, challenge.Hello, challenge.Nonce);

        if (expected.Length != proof.Length || !CryptographicOperations.FixedTimeEquals(expected, proof))
        {
            State = ServiceAuthConnectionState.Failed;
            return new ServiceAuthenticationResult(ServiceAuthenticationOutcome.InvalidProof);
        }

        // Verified: moves to ProofVerified, the only state from which
        // MarkAuthenticated can succeed - still not trusted until that
        // explicit finalization call.
        State = ServiceAuthConnectionState.ProofVerified;
        return new ServiceAuthenticationResult(ServiceAuthenticationOutcome.Success, challenge.Hello);
    }

    public bool MarkAuthenticated()
    {
        if (State != ServiceAuthConnectionState.ProofVerified)
        {
            // Nothing to finalize: either no proof was ever successfully
            // verified on this connection (Unauthenticated/ChallengeIssued/Failed),
            // or it already was and this is a redundant repeat call
            // (Authenticated) - either way, a safe no-op rather than a side
            // effect. This is the enforcement point that makes it impossible
            // to reach Authenticated without an immediately-preceding
            // successful VerifyProof call on this same instance.
            return false;
        }

        IsAuthenticated = true;
        State = ServiceAuthConnectionState.Authenticated;
        return true;
    }

    private sealed record PendingChallenge(ServiceHelloInfo Hello, byte[] Nonce, DateTime IssuedAt);
}
