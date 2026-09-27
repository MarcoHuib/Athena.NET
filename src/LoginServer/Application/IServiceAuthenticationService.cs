using Athena.Net.LoginServer.Db.Entities;

namespace Athena.Net.LoginServer.Application;

/// <summary>
/// Outcome of an inter-server (CharServer) service-login attempt. This is a
/// separate security domain from player authentication: a service account is
/// never represented as an ASP.NET Core Identity human user.
/// </summary>
public enum ServiceAuthenticationOutcome
{
    /// <summary>Default/sentinel value used when service authentication was not attempted.</summary>
    NotApplicable,
    Success,
    AccountNotFound,
    InvalidCredential,
    NotAuthorized,
}

public sealed record ServiceAuthenticationResult(ServiceAuthenticationOutcome Outcome)
{
    public bool Success => Outcome == ServiceAuthenticationOutcome.Success;
}

/// <summary>
/// Application-level boundary for authenticating an inter-server (CharServer)
/// connection. Service accounts remain on the legacy login table/UserPass
/// storage forever - they are never migrated to ASP.NET Core Identity, since
/// player and service authentication are separate security domains. Owns the
/// per-connection "has this socket proven it is an authenticated service?"
/// state so packet handlers can gate privileged inter-server operations on it.
/// </summary>
public interface IServiceAuthenticationService
{
    /// <summary>
    /// True only once <see cref="MarkAuthenticated"/> has been called. This must
    /// never become true merely because <see cref="Authenticate"/> classified the
    /// credentials as belonging to a valid service account: the caller still has
    /// to run its own expiration/ban/state checks and reach a final successful
    /// CharServer login before the connection is trusted with
    /// service-only packets.
    /// </summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// Verifies a raw password/credential against a legacy service-account row's
    /// UserPass storage (plaintext or the rAthena MD5 challenge scheme).
    /// </summary>
    bool VerifyPassword(LoginAccount account, string suppliedPassword, int passwordEnc, byte[]? md5Key);

    /// <summary>
    /// Classifies a login attempt (credential validity + reserved-account-range
    /// check) without granting authenticated status. The caller is responsible
    /// for running any remaining checks (expiration, ban, account state, ...)
    /// before calling <see cref="MarkAuthenticated"/>.
    /// </summary>
    ServiceAuthenticationResult Authenticate(LoginAccount? account, bool passwordMatches);

    /// <summary>
    /// Grants this connection service-authenticated status. Must only be called
    /// once the caller has completed every check for a successful CharServer
    /// login - never merely because <see cref="Authenticate"/> succeeded.
    /// </summary>
    void MarkAuthenticated();
}
