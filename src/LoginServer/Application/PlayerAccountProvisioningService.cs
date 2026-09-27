using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Db.Identity;
using Athena.Net.LoginServer.Net;

namespace Athena.Net.LoginServer.Application;

/// <summary>
/// Default <see cref="IPlayerAccountProvisioningService"/>: creates the
/// AthenaIdentityUser and AthenaGameAccount inside one database transaction so a
/// failure on either side rolls back the whole operation, and neither record is
/// ever left behind without the other.
/// <para>
/// This is the single choke point every account-creation entry point goes
/// through (scripts/create-player-account.sh's one-shot LoginServer mode, the
/// console `create:` command), so credential limits are validated exactly once
/// here rather than duplicated - or, worse, forgotten - in each caller. The
/// limits themselves come from what the stock client's fixed-width 0x0064
/// login packet can actually carry (see PacketConstants.NameLength), not
/// invented values: an account this service allowed to create with a longer
/// username/password could never actually log in.
/// </para>
/// </summary>
public sealed class PlayerAccountProvisioningService : IPlayerAccountProvisioningService
{
    /// <summary>
    /// The stock 0x0064 username/password fields are fixed-width, NUL-terminated
    /// PacketConstants.NameLength-byte buffers (see ai/iro-2026-wire.md), so the
    /// longest value that round-trips exactly is one byte short of the field
    /// width, leaving room for the terminator.
    /// </summary>
    private const int MaxCredentialLength = PacketConstants.NameLength - 1;

    private readonly UserManager<AthenaIdentityUser> _userManager;
    private readonly AthenaIdentityDbContext _db;
    private readonly IRagnarokAccountIdAllocator _idAllocator;
    private readonly LoginConfigStore _configStore;

    public PlayerAccountProvisioningService(UserManager<AthenaIdentityUser> userManager, AthenaIdentityDbContext db, IRagnarokAccountIdAllocator idAllocator, LoginConfigStore configStore)
    {
        _userManager = userManager;
        _db = db;
        _idAllocator = idAllocator;
        _configStore = configStore;
    }

    public async Task<ProvisionPlayerAccountResult> ProvisionAsync(string userName, string email, string password, char sex, CancellationToken cancellationToken)
    {
        var validationError = ValidateInput(userName, email, password, sex);
        if (validationError != null)
        {
            return ProvisionPlayerAccountResult.Fail(validationError);
        }

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

    /// <summary>
    /// Rejects credentials the stock client could never actually use to log in,
    /// before anything is written to the database. The minimums mirror the
    /// legacy acc_name_min_length/password_min_length configuration (still
    /// meaningful as an operator-tunable floor even though the auto-registration
    /// feature that originally read them is gone); the maximums come from the
    /// verified stock 0x0064 wire field width, not an invented limit.
    /// </summary>
    private string? ValidateInput(string userName, string email, string password, char sex)
    {
        var config = _configStore.Current;

        if (string.IsNullOrEmpty(userName) || userName.Length < config.AccountNameMinLength || userName.Length > MaxCredentialLength)
        {
            return $"Username must be between {config.AccountNameMinLength} and {MaxCredentialLength} characters.";
        }

        if (string.IsNullOrEmpty(password) || password.Length < config.PasswordMinLength || password.Length > MaxCredentialLength)
        {
            return $"Password must be between {config.PasswordMinLength} and {MaxCredentialLength} characters.";
        }

        var normalizedSex = char.ToUpperInvariant(sex);
        if (normalizedSex != 'M' && normalizedSex != 'F')
        {
            return "Sex must be M or F.";
        }

        if (!IsValidEmail(email))
        {
            return "Email is not a valid email address.";
        }

        return null;
    }

    private static bool IsValidEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        try
        {
            var address = new System.Net.Mail.MailAddress(email);
            return address.Address == email;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
