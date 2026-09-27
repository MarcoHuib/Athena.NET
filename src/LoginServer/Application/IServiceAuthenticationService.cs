namespace Athena.Net.LoginServer.Application;

/// <summary>
/// Outcome of an inter-server (CharServer) service-authentication attempt.
/// This is a separate security domain from player authentication: a
/// CharServer is a service identified by a non-secret ServiceId and a shared
/// ServiceToken, never an ASP.NET Core Identity human user and never a
/// database-backed account row.
/// </summary>
public enum ServiceAuthenticationOutcome
{
    /// <summary>Default/sentinel value used when service authentication was not attempted.</summary>
    NotApplicable,
    Success,

    /// <summary>No ServiceToken is configured anywhere (secret file or environment variable), or the configured value is not valid (not Base64, or decodes to fewer than 32 bytes). Fails closed.</summary>
    TokenNotConfigured,

    /// <summary>A proof was submitted without an outstanding challenge (none was ever issued, it was already consumed, or the connection is not in a state that accepts a proof).</summary>
    NoChallengeIssued,

    /// <summary>The outstanding challenge was issued too long ago.</summary>
    ChallengeExpired,

    /// <summary>The submitted proof does not match the expected HMAC-SHA256 value.</summary>
    InvalidProof,

    /// <summary>
    /// LcServiceHello arrived while the connection was not in the
    /// <see cref="ServiceAuthConnectionState.Unauthenticated"/> state (a
    /// challenge is already outstanding, the connection is already
    /// authenticated, or a prior invalid transition already failed it).
    /// </summary>
    InvalidHelloState,
}

/// <summary>
/// The inter-server service-authentication handshake's connection state, as
/// tracked per-TCP-connection (a fresh <see cref="IServiceAuthenticationService"/>
/// is created per connection - see ServiceComposition). Models exactly the
/// four legitimate phases plus a terminal failure state:
/// <code>
/// Unauthenticated --GenerateChallenge success--> ChallengeIssued
/// ChallengeIssued --VerifyProof success--> ProofVerified
/// ProofVerified --MarkAuthenticated success--> Authenticated
/// (any state) --invalid transition or failed proof--> Failed
/// </code>
/// <see cref="ProofVerified"/> exists specifically so <see cref="IServiceAuthenticationService.MarkAuthenticated"/>
/// can enforce, inside this component rather than merely by caller
/// discipline, that a connection can only ever reach <see cref="Authenticated"/>
/// immediately after - and only after - its own successful
/// <see cref="IServiceAuthenticationService.VerifyProof"/> call: it is
/// impossible to construct a call sequence that reaches <see cref="Authenticated"/>
/// from <see cref="Unauthenticated"/>, <see cref="ChallengeIssued"/>, or
/// <see cref="Failed"/>. Once <see cref="Authenticated"/> or <see cref="Failed"/>,
/// the state never goes backwards: a second LcServiceHello or LcServiceAuthProof
/// is always rejected, never silently reset to a fresh
/// Unauthenticated/ChallengeIssued state, and a repeated
/// <see cref="IServiceAuthenticationService.MarkAuthenticated"/> call once
/// already <see cref="Authenticated"/> is a safe no-op.
/// </summary>
public enum ServiceAuthConnectionState
{
    Unauthenticated,
    ChallengeIssued,
    ProofVerified,
    Authenticated,
    Failed,
}

public sealed record ServiceAuthenticationResult(ServiceAuthenticationOutcome Outcome, ServiceHelloInfo? Hello = null)
{
    public bool Success => Outcome == ServiceAuthenticationOutcome.Success;
}

/// <summary>
/// Application-level boundary for authenticating an inter-server (CharServer)
/// connection via an HMAC-SHA256 challenge/response over the configured
/// ServiceToken. Owns the per-connection "has this socket proven possession
/// of the ServiceToken?" state so packet handlers can gate privileged
/// inter-server operations on it. The ServiceToken itself never travels over
/// the network - only a one-time proof does, and that proof cryptographically
/// binds the complete ServiceHello registration payload (see
/// <see cref="ServiceHelloInfo"/>/<see cref="ServiceAuthProofCalculator"/>),
/// not just the ServiceId.
/// </summary>
public interface IServiceAuthenticationService
{
    /// <summary>
    /// True only once <see cref="MarkAuthenticated"/> has been explicitly
    /// called by the caller after a successful <see cref="VerifyProof"/> and
    /// any remaining checks the caller wants to run. Never set as a side
    /// effect of <see cref="VerifyProof"/> itself.
    /// </summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// The ServiceId presented in the most recent accepted <see cref="GenerateChallenge"/>
    /// call, for logging/diagnostics only. Never used as an authorization
    /// credential by itself - the ServiceToken-derived proof is.
    /// </summary>
    string? ServiceId { get; }

    /// <summary>Current handshake connection state - see <see cref="ServiceAuthConnectionState"/>.</summary>
    ServiceAuthConnectionState State { get; }

    /// <summary>
    /// Generates a fresh, cryptographically random one-time nonce challenge
    /// for the given CharServer registration hello, binding every field of
    /// <paramref name="hello"/> (not just its ServiceId) into the proof that
    /// will later be expected. Only valid from <see cref="ServiceAuthConnectionState.Unauthenticated"/> -
    /// a second hello while a challenge is already outstanding, or after the
    /// connection has already authenticated or failed, is rejected (returns
    /// null) and does not replace/reset any existing challenge or state.
    /// </summary>
    byte[]? GenerateChallenge(ServiceHelloInfo hello);

    /// <summary>
    /// Verifies a submitted proof against the single outstanding challenge -
    /// recomputing the expected HMAC over the exact <see cref="ServiceHelloInfo"/>
    /// bound when the challenge was issued - using a fixed-time comparison.
    /// Only valid from <see cref="ServiceAuthConnectionState.ChallengeIssued"/>;
    /// the challenge is consumed (one-time use) regardless of outcome, so a
    /// repeated or replayed proof can never succeed against it again. On
    /// success, transitions <see cref="State"/> to
    /// <see cref="ServiceAuthConnectionState.ProofVerified"/> - the only state
    /// from which <see cref="MarkAuthenticated"/> can succeed - and the result
    /// includes the verified <see cref="ServiceHelloInfo"/> so the caller
    /// registers exactly the payload that was cryptographically bound - never
    /// a separately-tracked copy that could diverge from it. Never sets
    /// <see cref="IsAuthenticated"/> itself - the caller must still call
    /// <see cref="MarkAuthenticated"/> to grant authenticated status.
    /// </summary>
    ServiceAuthenticationResult VerifyProof(byte[] proof);

    /// <summary>
    /// Finalizes authentication, granting this connection service-authenticated
    /// status and transitioning <see cref="State"/> to
    /// <see cref="ServiceAuthConnectionState.Authenticated"/>. Only succeeds
    /// (returns <c>true</c>) when called from
    /// <see cref="ServiceAuthConnectionState.ProofVerified"/> - i.e.
    /// immediately after this connection's own <see cref="VerifyProof"/> call
    /// just succeeded, and only once for that verification. Every other call -
    /// from <see cref="ServiceAuthConnectionState.Unauthenticated"/>,
    /// <see cref="ServiceAuthConnectionState.ChallengeIssued"/> (a proof was
    /// never actually verified), <see cref="ServiceAuthConnectionState.Failed"/>,
    /// or a repeat call once already <see cref="ServiceAuthConnectionState.Authenticated"/> -
    /// returns <c>false</c> and leaves <see cref="IsAuthenticated"/>/<see cref="State"/>
    /// completely unchanged. This makes it impossible, from outside this
    /// component, to reach <see cref="ServiceAuthConnectionState.Authenticated"/>
    /// without an immediately-preceding successful <see cref="VerifyProof"/>
    /// call on the same instance.
    /// </summary>
    bool MarkAuthenticated();
}
