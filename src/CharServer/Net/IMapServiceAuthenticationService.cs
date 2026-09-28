namespace Athena.Net.CharServer.Net;

/// <summary>
/// Outcome of an inter-server (MapServer) service-authentication attempt.
/// This is a completely separate security domain from CharServer's own
/// authentication to LoginServer (<see cref="ServiceAuthenticationOutcome"/>
/// exists only under Athena.Net.LoginServer.Application on that side) - a
/// MapServer is a service identified by a non-secret ServiceId and its own
/// shared MapServer ServiceToken, never a database-backed account row.
/// </summary>
public enum MapServiceAuthenticationOutcome
{
    /// <summary>Default/sentinel value used when service authentication was not attempted.</summary>
    NotApplicable,
    Success,

    /// <summary>No MapServer ServiceToken is configured anywhere (secret file or environment variable), or the configured value is not valid (not Base64, or decodes to fewer than 32 bytes). Fails closed.</summary>
    TokenNotConfigured,

    /// <summary>A proof was submitted without an outstanding challenge (none was ever issued, it was already consumed, or the connection is not in a state that accepts a proof).</summary>
    NoChallengeIssued,

    /// <summary>The outstanding challenge was issued too long ago.</summary>
    ChallengeExpired,

    /// <summary>The submitted proof does not match the expected HMAC-SHA256 value.</summary>
    InvalidProof,

    /// <summary>
    /// MapServiceHello arrived while the connection was not in the
    /// <see cref="MapServiceAuthConnectionState.Unauthenticated"/> state (a
    /// challenge is already outstanding, the connection is already
    /// authenticated, or a prior invalid transition already failed it).
    /// </summary>
    InvalidHelloState,
}

/// <summary>
/// The MapServer inter-server service-authentication handshake's connection
/// state, tracked per-TCP-connection (a fresh
/// <see cref="IMapServiceAuthenticationService"/> is created per connection).
/// Models exactly the four legitimate phases plus a terminal failure state -
/// structurally identical to LoginServer's
/// <c>Athena.Net.LoginServer.Application.ServiceAuthConnectionState</c>:
/// <code>
/// Unauthenticated --GenerateChallenge success--> ChallengeIssued
/// ChallengeIssued --VerifyProof success--> ProofVerified
/// ProofVerified --MarkAuthenticated success--> Authenticated
/// (any state) --invalid transition or failed proof--> Failed
/// </code>
/// <see cref="ProofVerified"/> exists specifically so
/// <see cref="IMapServiceAuthenticationService.MarkAuthenticated"/> can
/// enforce, inside this component rather than merely by caller discipline,
/// that a connection can only ever reach <see cref="Authenticated"/>
/// immediately after - and only after - its own successful
/// <see cref="IMapServiceAuthenticationService.VerifyProof"/> call. Once
/// <see cref="Authenticated"/> or <see cref="Failed"/>, the state never goes
/// backwards.
/// </summary>
public enum MapServiceAuthConnectionState
{
    Unauthenticated,
    ChallengeIssued,
    ProofVerified,
    Authenticated,
    Failed,
}

public sealed record MapServiceAuthenticationResult(MapServiceAuthenticationOutcome Outcome, MapServiceHelloInfo? Hello = null)
{
    public bool Success => Outcome == MapServiceAuthenticationOutcome.Success;
}

/// <summary>
/// Application-level boundary for authenticating an inter-server
/// (MapServer) connection via an HMAC-SHA256 challenge/response over the
/// configured MapServer ServiceToken. Owns the per-connection "has this
/// socket proven possession of the MapServer ServiceToken?" state so packet
/// handlers can gate privileged inter-server operations (map registration,
/// character auth requests, position/gameplay persistence, etc.) on it. The
/// ServiceToken itself never travels over the network - only a one-time
/// proof does, and that proof cryptographically binds the complete
/// MapServiceHello registration payload (see
/// <see cref="MapServiceHelloInfo"/>/<see cref="MapServiceAuthProofCalculator"/>),
/// not just the ServiceId.
/// </summary>
public interface IMapServiceAuthenticationService
{
    /// <summary>
    /// True only once <see cref="MarkAuthenticated"/> has been explicitly
    /// called by the caller after a successful <see cref="VerifyProof"/>.
    /// Never set as a side effect of <see cref="VerifyProof"/> itself.
    /// </summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// The ServiceId presented in the most recent accepted <see cref="GenerateChallenge"/>
    /// call, for logging/diagnostics only. Never used as an authorization
    /// credential by itself - the ServiceToken-derived proof is.
    /// </summary>
    string? ServiceId { get; }

    /// <summary>Current handshake connection state - see <see cref="MapServiceAuthConnectionState"/>.</summary>
    MapServiceAuthConnectionState State { get; }

    /// <summary>
    /// Generates a fresh, cryptographically random one-time nonce challenge
    /// for the given MapServer registration hello, binding every field of
    /// <paramref name="hello"/> (not just its ServiceId) into the proof that
    /// will later be expected. Only valid from
    /// <see cref="MapServiceAuthConnectionState.Unauthenticated"/> - a second
    /// hello while a challenge is already outstanding, or after the
    /// connection has already authenticated or failed, is rejected (returns
    /// null) and does not replace/reset any existing challenge or state.
    /// </summary>
    byte[]? GenerateChallenge(MapServiceHelloInfo hello);

    /// <summary>
    /// Verifies a submitted proof against the single outstanding challenge -
    /// recomputing the expected HMAC over the exact <see cref="MapServiceHelloInfo"/>
    /// bound when the challenge was issued - using a fixed-time comparison.
    /// Only valid from <see cref="MapServiceAuthConnectionState.ChallengeIssued"/>;
    /// the challenge is consumed (one-time use) regardless of outcome, so a
    /// repeated or replayed proof can never succeed against it again. On
    /// success, transitions <see cref="State"/> to
    /// <see cref="MapServiceAuthConnectionState.ProofVerified"/> - the only
    /// state from which <see cref="MarkAuthenticated"/> can succeed.
    /// </summary>
    MapServiceAuthenticationResult VerifyProof(byte[] proof);

    /// <summary>
    /// Finalizes authentication, granting this connection service-authenticated
    /// status and transitioning <see cref="State"/> to
    /// <see cref="MapServiceAuthConnectionState.Authenticated"/>. Only
    /// succeeds (returns <c>true</c>) when called from
    /// <see cref="MapServiceAuthConnectionState.ProofVerified"/>.
    /// </summary>
    bool MarkAuthenticated();
}
