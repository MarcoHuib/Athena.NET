using Athena.Net.LoginServer.Application;

namespace Athena.Net.LoginServer.Net;

/// <summary>
/// Maps a domain-level <see cref="PlayerAuthenticationResult"/> to the iRO
/// AC_REFUSE_LOGIN error code the stock client expects. This is deliberately
/// the only place that translation happens: IPlayerAuthenticationService and
/// its implementations return application/provider-level failure semantics
/// only, never a wire error code, so a future non-iRO caller (a website, a
/// future external identity provider) never has to know iRO wire codes exist.
/// </summary>
internal static class PlayerAuthenticationErrorCodeMapper
{
    /// <summary>
    /// Maps a failed <see cref="PlayerAuthenticationResult"/> to its
    /// AC_REFUSE_LOGIN error code. Must only be called when
    /// <see cref="PlayerAuthenticationResult.Success"/> is false.
    /// </summary>
    internal static uint ToErrorCode(PlayerAuthenticationResult result)
    {
        return result.FailureReason switch
        {
            PlayerAuthenticationFailureReason.AccountNotFound => 0,
            PlayerAuthenticationFailureReason.InvalidPassword => 1,
            PlayerAuthenticationFailureReason.GameAccountMissing => 0,
            PlayerAuthenticationFailureReason.AccountExpired => 2,
            PlayerAuthenticationFailureReason.AccountBanned => 6,
            PlayerAuthenticationFailureReason.LockedOut => 6,
            // The legacy account "state" column already stores rAthena/iRO's own
            // 1-based ban-reason codes (a domain concept that happens to be
            // numerically adjacent to, but distinct from, the 0-based
            // AC_REFUSE_LOGIN error code space) - subtracting 1 is the wire
            // mapping, not a domain computation, so it belongs here rather than
            // in the player authentication service.
            PlayerAuthenticationFailureReason.AccountStateRestricted =>
                (uint)Math.Max(0, (int)(result.AccountStateCode ?? 0) - 1),
            _ => 0,
        };
    }
}
