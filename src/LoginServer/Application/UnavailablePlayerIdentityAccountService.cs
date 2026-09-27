namespace Athena.Net.LoginServer.Application;

/// <summary>
/// Fallback <see cref="IPlayerIdentityAccountService"/> used when no real
/// implementation is wired in (e.g. the Identity database is unreachable, or a
/// test fixture does not care about email-change behavior). Every call safely
/// fails rather than the server crashing or silently doing nothing dangerous.
/// </summary>
public sealed class UnavailablePlayerIdentityAccountService : IPlayerIdentityAccountService
{
    public Task<ChangeEmailResult> ChangeEmailAsync(uint ragnarokAccountId, string currentEmail, string newEmail, CancellationToken cancellationToken) =>
        Task.FromResult(ChangeEmailResult.Fail(ChangeEmailFailureReason.GameAccountNotFound));
}
