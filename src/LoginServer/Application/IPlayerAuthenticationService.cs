namespace Athena.Net.LoginServer.Application;

public enum PlayerAuthenticationFailureReason
{
    None,
    AccountNotFound,
    InvalidPassword,
    LockedOut,
    GameAccountMissing,
    AccountExpired,
    AccountBanned,
    AccountStateRestricted,
}

/// <summary>
/// Identity-independent authenticated game-account result. Never expose
/// IdentityUser, UserManager, EF entities, or provider-specific tokens to
/// ClientSession/the packet layer - only this projection.
/// </summary>
public sealed record AuthenticatedGameAccount(
    Guid IdentityUserId,
    Guid GameAccountId,
    uint RagnarokAccountId,
    string Sex,
    int GroupId,
    string WebAuthToken);

/// <summary>
/// Result of a player authentication attempt. <see cref="UnblockAtLocal"/> carries
/// a raw local timestamp (not pre-formatted) for a ban/lockout failure; formatting
/// it for the wire response is ClientSession's responsibility, matching how every
/// other legacy protocol string is built.
/// </summary>
public sealed record PlayerAuthenticationResult(
    bool Success,
    PlayerAuthenticationFailureReason FailureReason,
    uint ErrorCode,
    DateTime? UnblockAtLocal,
    AuthenticatedGameAccount? Account)
{
    public static PlayerAuthenticationResult Fail(PlayerAuthenticationFailureReason reason, uint errorCode, DateTime? unblockAtLocal = null) =>
        new(false, reason, errorCode, unblockAtLocal, null);

    public static PlayerAuthenticationResult Ok(AuthenticatedGameAccount account) =>
        new(true, PlayerAuthenticationFailureReason.None, 0, null, account);
}

/// <summary>
/// Application-level boundary between the iRO packet layer and player identity.
/// The stock Ragexe client must never know this exists: ClientSession supplies
/// the plain username/password it already parsed from the 0x0064-family request
/// and gets back either an <see cref="AuthenticatedGameAccount"/> or a failure
/// reason - never an IdentityUser, UserManager, EF entity, or provider-specific
/// token. This is the seam a future external identity provider (Entra External
/// ID, other federated providers) would implement instead of
/// <see cref="Db.Identity.AthenaIdentityUser"/>-backed Identity, without
/// ClientSession changing at all.
/// </summary>
public interface IPlayerAuthenticationService
{
    Task<PlayerAuthenticationResult> AuthenticateAsync(string userName, string password, string remoteIp, CancellationToken cancellationToken);
}
