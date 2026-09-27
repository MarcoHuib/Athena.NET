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
    private readonly IRagnarokAccountIdAllocator _idAllocator;

    public PlayerAccountProvisioningService(UserManager<AthenaIdentityUser> userManager, AthenaIdentityDbContext db, IRagnarokAccountIdAllocator idAllocator)
    {
        _userManager = userManager;
        _db = db;
        _idAllocator = idAllocator;
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
            var ragnarokAccountId = await _idAllocator.AllocateAsync(_db, cancellationToken);
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
}
