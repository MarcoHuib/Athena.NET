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

    /// <summary>No ServiceToken is configured anywhere (secret file or environment variable). Fails closed.</summary>
    TokenNotConfigured,

    /// <summary>A proof was submitted without an outstanding challenge (none was ever issued, or it was already consumed).</summary>
    NoChallengeIssued,

    /// <summary>The outstanding challenge was issued too long ago.</summary>
    ChallengeExpired,

    /// <summary>The submitted proof does not match the expected HMAC-SHA256 value.</summary>
    InvalidProof,
}

public sealed record ServiceAuthenticationResult(ServiceAuthenticationOutcome Outcome)
{
    public bool Success => Outcome == ServiceAuthenticationOutcome.Success;
}

/// <summary>
/// Application-level boundary for authenticating an inter-server (CharServer)
/// connection via an HMAC-SHA256 challenge/response over the configured
/// ServiceToken. Owns the per-connection "has this socket proven possession
/// of the ServiceToken?" state so packet handlers can gate privileged
/// inter-server operations on it. The ServiceToken itself never travels over
/// the network - only a one-time proof derived from it does.
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
    /// The ServiceId presented in the most recent <see cref="GenerateChallenge"/>
    /// call, for logging/diagnostics only. Never used as an authorization
    /// credential by itself - the ServiceToken-derived proof is.
    /// </summary>
    string? ServiceId { get; }

    /// <summary>
    /// Generates a fresh, cryptographically random one-time nonce challenge
    /// for the given (non-secret) ServiceId. Only one challenge may be
    /// outstanding at a time; calling this again discards any previous
    /// unconsumed challenge (so it can never later be satisfied).
    /// </summary>
    byte[] GenerateChallenge(string serviceId);

    /// <summary>
    /// Verifies a submitted proof against the single outstanding challenge (if
    /// any) using a fixed-time comparison. The challenge is consumed
    /// (one-time use) regardless of outcome, so a repeated or replayed proof
    /// can never succeed against it again. Never sets <see cref="IsAuthenticated"/> -
    /// the caller decides when every check for a successful CharServer login
    /// has passed.
    /// </summary>
    ServiceAuthenticationResult VerifyProof(byte[] proof);

    /// <summary>
    /// Grants this connection service-authenticated status. Must only be
    /// called once the caller has completed every check for a successful
    /// CharServer login - never merely because <see cref="VerifyProof"/> succeeded.
    /// </summary>
    void MarkAuthenticated();
}
