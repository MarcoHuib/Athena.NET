using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Athena.Net.LoginServer.Db.Identity;

namespace Athena.Net.LoginServer.Application;

/// <summary>
/// Default <see cref="IPlayerAccountProvisioningService"/>: creates the
/// AthenaIdentityUser and AthenaGameAccount inside one database transaction so a
/// failure on either side rolls back the whole operation, and neither record is
/// ever left behind without the other.
/// </summary>
public sealed class PlayerAccountProvisioningService : IPlayerAccountProvisioningService
{
    private readonly UserManager<AthenaIdentityUser> _userManager;
    private readonly AthenaIdentityDbContext _db;

    public PlayerAccountProvisioningService(UserManager<AthenaIdentityUser> userManager, AthenaIdentityDbContext db)
    {
        _userManager = userManager;
        _db = db;
    }

    public async Task<ProvisionPlayerAccountResult> ProvisionAsync(string userName, string email, string password, char sex, CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var user = new AthenaIdentityUser
        {
            Id = Guid.NewGuid(),
            UserName = userName,
            Email = email,
        };

        var createResult = await _userManager.CreateAsync(user, password);
        if (!createResult.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ProvisionPlayerAccountResult.Fail(string.Join("; ", createResult.Errors.Select(e => e.Description)));
        }

        try
        {
            var ragnarokAccountId = await AllocateRagnarokAccountIdAsync(cancellationToken);
            var gameAccount = new AthenaGameAccount
            {
                Id = Guid.NewGuid(),
                IdentityUserId = user.Id,
                RagnarokAccountId = ragnarokAccountId,
                Sex = char.ToUpperInvariant(sex).ToString(),
            };

            _db.GameAccounts.Add(gameAccount);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return ProvisionPlayerAccountResult.Ok(user.Id, gameAccount.Id, gameAccount.RagnarokAccountId);
        }
        catch (DbUpdateException ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ProvisionPlayerAccountResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Allocates the next legacy uint32 compatibility id. Mirrors the existing
    /// login table's IDENTITY(2000000,1) starting range so stock-protocol account
    /// ids stay in the same numeric space regardless of which table produced them.
    /// </summary>
    private async Task<uint> AllocateRagnarokAccountIdAsync(CancellationToken cancellationToken)
    {
        var max = await _db.GameAccounts
            .Select(a => (uint?)a.RagnarokAccountId)
            .MaxAsync(cancellationToken);

        return (max ?? 1_999_999u) + 1;
    }
}
