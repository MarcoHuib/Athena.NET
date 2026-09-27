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
    bool IsAuthenticated { get; }

    /// <summary>
    /// Verifies a raw password/credential against a legacy service-account row's
    /// UserPass storage (plaintext or the rAthena MD5 challenge scheme).
    /// </summary>
    bool VerifyPassword(LoginAccount account, string suppliedPassword, int passwordEnc, byte[]? md5Key);

    ServiceAuthenticationResult Authenticate(LoginAccount? account, bool passwordMatches);
}
