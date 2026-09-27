using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Athena.Net.LoginServer.Db.Identity;

namespace Athena.Net.LoginServer.Application;

/// <summary>
/// Default <see cref="IPlayerIdentityAccountService"/>: goes through
/// <see cref="UserManager{TUser}"/> for the actual Identity mutation instead of
/// writing Email/NormalizedEmail/EmailConfirmed directly, so normalization and
/// validation always match whatever ASP.NET Core Identity is actually
/// configured to do - the packet layer never has to reproduce that logic.
/// Creates its own DI scope per call, mirroring
/// <see cref="IdentityPlayerAuthenticationService"/>: CharServer's connection
/// is long-lived, so nothing here may be tied to that connection's lifetime.
/// </summary>
public sealed class PlayerIdentityAccountService : IPlayerIdentityAccountService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public PlayerIdentityAccountService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<ChangeEmailResult> ChangeEmailAsync(uint ragnarokAccountId, string currentEmail, string newEmail, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AthenaIdentityDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AthenaIdentityUser>>();

        var account = await db.GameAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.RagnarokAccountId == ragnarokAccountId, cancellationToken);
        if (account == null)
        {
            return ChangeEmailResult.Fail(ChangeEmailFailureReason.GameAccountNotFound);
        }

        var user = await userManager.FindByIdAsync(account.IdentityUserId.ToString());
        if (user == null || !string.Equals(user.Email, currentEmail, StringComparison.OrdinalIgnoreCase))
        {
            return ChangeEmailResult.Fail(ChangeEmailFailureReason.CurrentEmailMismatch);
        }

        // SetEmailAsync updates Email, resets EmailConfirmed, and - via the same
        // UpdateUserAsync path every other Identity mutation goes through -
        // recomputes NormalizedEmail with the configured ILookupNormalizer and
        // runs the configured IUserValidators (format/uniqueness when
        // RequireUniqueEmail is set), then persists. None of that needs to be
        // reimplemented here.
        var result = await userManager.SetEmailAsync(user, newEmail);
        return result.Succeeded
            ? ChangeEmailResult.Ok()
            : ChangeEmailResult.Fail(ChangeEmailFailureReason.RejectedByIdentity);
    }
}
