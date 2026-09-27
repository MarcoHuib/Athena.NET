namespace Athena.Net.LoginServer.Application;

/// <summary>
/// Fallback <see cref="IPlayerAuthenticationService"/> used when no real player
/// identity provider is configured (e.g. the Identity database is unreachable at
/// startup, mirroring how LoginDb's own Func&lt;LoginDbContext?&gt; gracefully
/// returns null instead of crashing). Every login attempt safely fails rather
/// than the server bypassing authentication or throwing.
/// </summary>
public sealed class UnavailablePlayerAuthenticationService : IPlayerAuthenticationService
{
    public Task<PlayerAuthenticationResult> AuthenticateAsync(string userName, string password, string remoteIp, CancellationToken cancellationToken) =>
        Task.FromResult(PlayerAuthenticationResult.Fail(PlayerAuthenticationFailureReason.AccountNotFound, 0));
}
