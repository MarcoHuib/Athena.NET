using Athena.Net.LoginServer.Db.Entities;

namespace Athena.Net.LoginServer.Application;

/// <summary>
/// Outcome of verifying a player's supplied credentials against a resolved account.
/// This is the seam that will be replaced by ASP.NET Core Identity password
/// verification/lockout handling; callers must not depend on the legacy
/// UserPass/MD5 storage this currently wraps.
/// </summary>
public enum PlayerCredentialOutcome
{
    Success,
    SexRestricted,
    InvalidPassword,
}

/// <summary>
/// Application-level boundary between the iRO packet layer and player credential
/// verification. ClientSession must depend on this abstraction rather than on
/// EF entities, password storage, or a specific credential provider directly.
/// </summary>
public interface IPlayerAuthenticationService
{
    /// <summary>
    /// Verifies a raw password/credential against the given account. Used for both
    /// player and service-account login paths.
    /// </summary>
    bool VerifyPassword(LoginAccount account, string suppliedPassword, int passwordEnc, byte[]? md5Key);

    /// <summary>
    /// Verifies player-specific login eligibility (credential plus player-only
    /// restrictions, such as service accounts never being usable for player login).
    /// </summary>
    PlayerCredentialOutcome VerifyCredentials(LoginAccount account, string suppliedPassword, int passwordEnc, byte[]? md5Key);
}
