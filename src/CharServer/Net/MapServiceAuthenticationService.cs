using System.Security.Cryptography;

namespace Athena.Net.CharServer.Net;

/// <summary>
/// Default per-connection <see cref="IMapServiceAuthenticationService"/>:
/// HMAC-SHA256 challenge/response over the configured MapServer
/// ServiceToken, binding the complete MapServiceHello registration payload
/// into the proof (see <see cref="MapServiceAuthProofCalculator"/>). A fresh
/// instance is created per TCP connection (see <see cref="MapServerSession"/>),
/// so the connection state, the one-outstanding-challenge state, and the
/// IsAuthenticated flag are naturally scoped to a single connection - one
/// connection's issued nonce/proof can never authenticate a different
/// connection. Structurally identical to LoginServer's
/// <c>Athena.Net.LoginServer.Application.ServiceAuthenticationService</c>
/// (used for the pre-existing, independent CharServer &lt;-&gt; LoginServer
/// handshake), reusing the same state-machine discipline for this new,
/// separate MapServer &lt;-&gt; CharServer trust boundary.
/// <para>
/// Enforces the handshake state machine (<see cref="MapServiceAuthConnectionState"/>):
/// a hello is only accepted from <c>Unauthenticated</c>, a proof only from
/// <c>ChallengeIssued</c>, and <see cref="MarkAuthenticated"/> only succeeds
/// from <c>ProofVerified</c>. Any other invalid attempt fails closed and
/// moves the state to the terminal <c>Failed</c> state (never silently
/// resets to a fresh challenge, and never goes backwards from
/// <c>Authenticated</c>).
/// </para>
/// </summary>
public sealed class MapServiceAuthenticationService : IMapServiceAuthenticationService
{
    private static readonly TimeSpan ChallengeTimeout = TimeSpan.FromSeconds(30);

    private readonly MapServerServiceTokenProvider _tokenProvider;
    private PendingChallenge? _pendingChallenge;

    public MapServiceAuthenticationService(MapServerServiceTokenProvider tokenProvider)
    {
        _tokenProvider = tokenProvider;
    }

    public bool IsAuthenticated { get; private set; }

    public string? ServiceId { get; private set; }

    public MapServiceAuthConnectionState State { get; private set; } = MapServiceAuthConnectionState.Unauthenticated;

    public byte[]? GenerateChallenge(MapServiceHelloInfo hello)
    {
        if (State == MapServiceAuthConnectionState.Authenticated)
        {
            return null;
        }

        if (State != MapServiceAuthConnectionState.Unauthenticated)
        {
            State = MapServiceAuthConnectionState.Failed;
            return null;
        }

        ServiceId = hello.ServiceId;
        var nonce = RandomNumberGenerator.GetBytes(PacketConstants.ServiceNonceLength);
        _pendingChallenge = new PendingChallenge(hello, nonce, DateTime.UtcNow);
        State = MapServiceAuthConnectionState.ChallengeIssued;
        return nonce;
    }

    public MapServiceAuthenticationResult VerifyProof(byte[] proof)
    {
        if (State == MapServiceAuthConnectionState.Authenticated)
        {
            return new MapServiceAuthenticationResult(MapServiceAuthenticationOutcome.NoChallengeIssued);
        }

        if (State != MapServiceAuthConnectionState.ChallengeIssued)
        {
            State = MapServiceAuthConnectionState.Failed;
            return new MapServiceAuthenticationResult(MapServiceAuthenticationOutcome.NoChallengeIssued);
        }

        // Consumed unconditionally, before any check below runs: a challenge
        // can be satisfied at most once, whether the proof that consumes it
        // is right or wrong.
        var challenge = _pendingChallenge;
        _pendingChallenge = null;

        if (!_tokenProvider.IsConfigured)
        {
            State = MapServiceAuthConnectionState.Failed;
            return new MapServiceAuthenticationResult(MapServiceAuthenticationOutcome.TokenNotConfigured);
        }

        if (challenge == null)
        {
            State = MapServiceAuthConnectionState.Failed;
            return new MapServiceAuthenticationResult(MapServiceAuthenticationOutcome.NoChallengeIssued);
        }

        if (DateTime.UtcNow - challenge.IssuedAt > ChallengeTimeout)
        {
            State = MapServiceAuthConnectionState.Failed;
            return new MapServiceAuthenticationResult(MapServiceAuthenticationOutcome.ChallengeExpired);
        }

        var expected = MapServiceAuthProofCalculator.ComputeProof(_tokenProvider.TokenBytes!, challenge.Hello, challenge.Nonce);

        if (expected.Length != proof.Length || !CryptographicOperations.FixedTimeEquals(expected, proof))
        {
            State = MapServiceAuthConnectionState.Failed;
            return new MapServiceAuthenticationResult(MapServiceAuthenticationOutcome.InvalidProof);
        }

        State = MapServiceAuthConnectionState.ProofVerified;
        return new MapServiceAuthenticationResult(MapServiceAuthenticationOutcome.Success, challenge.Hello);
    }

    public bool MarkAuthenticated()
    {
        if (State != MapServiceAuthConnectionState.ProofVerified)
        {
            return false;
        }

        IsAuthenticated = true;
        State = MapServiceAuthConnectionState.Authenticated;
        return true;
    }

    private sealed record PendingChallenge(MapServiceHelloInfo Hello, byte[] Nonce, DateTime IssuedAt);
}
