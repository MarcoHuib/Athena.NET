namespace Athena.Net.LoginServer.Application;

public sealed record ProvisionPlayerAccountResult(
    bool Success,
    string? ErrorMessage,
    Guid IdentityUserId,
    Guid GameAccountId,
    uint RagnarokAccountId)
{
    public static ProvisionPlayerAccountResult Fail(string errorMessage) =>
        new(false, errorMessage, Guid.Empty, Guid.Empty, 0);

    public static ProvisionPlayerAccountResult Ok(Guid identityUserId, Guid gameAccountId, uint ragnarokAccountId) =>
        new(true, null, identityUserId, gameAccountId, ragnarokAccountId);
}

/// <summary>
/// Centralized creation of a new Athena player account: one
/// AthenaIdentityUser and its strictly-1:1 AthenaGameAccount, created as a single
/// consistent (transactional) operation. This is the only supported way to create
/// a new player account; there is no direct-SQL or ClientSession-local account
/// creation path once this replaces the legacy auto-register flow.
/// </summary>
public interface IPlayerAccountProvisioningService
{
    /// <param name="userName">The Ragnarok login username (Identity UserName).</param>
    /// <param name="email">A separate account email, for future website/account login.</param>
    /// <param name="password">Plaintext password; hashed by ASP.NET Core Identity.</param>
    /// <param name="sex">'M' or 'F'.</param>
    Task<ProvisionPlayerAccountResult> ProvisionAsync(string userName, string email, string password, char sex, CancellationToken cancellationToken);
}
