using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Db.Identity;

namespace Athena.Net.LoginServer.Application;

/// <summary>
/// ASP.NET Core Identity-backed <see cref="IPlayerAuthenticationService"/>.
/// <para>
/// Creates a new DI scope per authentication call (via <see cref="IServiceScopeFactory"/>)
/// rather than depending on a connection-scoped UserManager/DbContext: a player's
/// LoginServer TCP connection is inherently one-shot (the stock client
/// disconnects immediately after 0x0A4D), but this service must not assume that
/// about every future caller, so it never ties its EF DbContext to anything
/// longer-lived than a single authentication attempt.
/// </para>
/// <para>
/// Only the plain-password stock login path (0x0064/CA_LOGIN_PCBANG/CA_LOGIN_CHANNEL,
/// PasswordEnc == 0) can succeed here. The legacy rAthena MD5 challenge-response
/// login variants (CA_LOGIN2/3/4) require a recoverable stored password to hash
/// against, which ASP.NET Core Identity intentionally never exposes; a request
/// using them safely fails as an invalid password rather than being accepted or
/// crashing. ai/iro-2026-wire.md only verifies 0x0064 for the current stock iRO
/// client, so this does not affect stock-client compatibility.
/// </para>
/// </summary>
public sealed class IdentityPlayerAuthenticationService : IPlayerAuthenticationService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly LoginConfigStore _configStore;

    public IdentityPlayerAuthenticationService(IServiceScopeFactory scopeFactory, LoginConfigStore configStore)
    {
        _scopeFactory = scopeFactory;
        _configStore = configStore;
    }

    public async Task<PlayerAuthenticationResult> AuthenticateAsync(string userName, string password, string remoteIp, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AthenaIdentityUser>>();
        var db = scope.ServiceProvider.GetRequiredService<AthenaIdentityDbContext>();

        var user = await userManager.FindByNameAsync(userName);
        if (user == null)
        {
            return PlayerAuthenticationResult.Fail(PlayerAuthenticationFailureReason.AccountNotFound, 0);
        }

        if (await userManager.IsLockedOutAsync(user))
        {
            return PlayerAuthenticationResult.Fail(PlayerAuthenticationFailureReason.LockedOut, 6, user.LockoutEnd?.LocalDateTime);
        }

        var passwordValid = await userManager.CheckPasswordAsync(user, password);
        if (!passwordValid)
        {
            await userManager.AccessFailedAsync(user);
            return PlayerAuthenticationResult.Fail(PlayerAuthenticationFailureReason.InvalidPassword, 1);
        }

        if (userManager.SupportsUserLockout)
        {
            await userManager.ResetAccessFailedCountAsync(user);
        }

        var gameAccount = await db.GameAccounts.FirstOrDefaultAsync(a => a.IdentityUserId == user.Id, cancellationToken);
        if (gameAccount == null)
        {
            return PlayerAuthenticationResult.Fail(PlayerAuthenticationFailureReason.GameAccountMissing, 0);
        }

        var nowUnix = ToUnixTime(DateTime.UtcNow);
        if (gameAccount.ExpirationTime != 0 && gameAccount.ExpirationTime < nowUnix)
        {
            return PlayerAuthenticationResult.Fail(PlayerAuthenticationFailureReason.AccountExpired, 2);
        }

        if (gameAccount.UnbanTime != 0 && gameAccount.UnbanTime > nowUnix)
        {
            return PlayerAuthenticationResult.Fail(PlayerAuthenticationFailureReason.AccountBanned, 6, FromUnixTime(gameAccount.UnbanTime));
        }

        if (gameAccount.State != 0)
        {
            var error = (uint)Math.Max(0, (int)gameAccount.State - 1);
            return PlayerAuthenticationResult.Fail(PlayerAuthenticationFailureReason.AccountStateRestricted, error);
        }

        gameAccount.LastLogin = DateTime.Now;
        gameAccount.LastIp = remoteIp;
        gameAccount.UnbanTime = 0;
        gameAccount.LoginCount += 1;

        if (_configStore.Current.UseWebAuthToken)
        {
            await UpdateWebAuthTokenWithRetryAsync(db, gameAccount, cancellationToken);
        }
        else
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return PlayerAuthenticationResult.Ok(new AuthenticatedGameAccount(
            user.Id,
            gameAccount.Id,
            gameAccount.RagnarokAccountId,
            gameAccount.Sex,
            gameAccount.GroupId,
            gameAccount.WebAuthToken ?? string.Empty));
    }

    private static async Task UpdateWebAuthTokenWithRetryAsync(AthenaIdentityDbContext db, AthenaGameAccount account, CancellationToken cancellationToken)
    {
        const int maxRetries = 20;

        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            account.WebAuthToken = GenerateWebAuthToken();
            account.WebAuthTokenEnabled = true;

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException) when (attempt < maxRetries - 1)
            {
                db.Entry(account).State = EntityState.Unchanged;
            }
        }
    }

    private static string GenerateWebAuthToken()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(8);
        var sb = new System.Text.StringBuilder(16);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    private static uint ToUnixTime(DateTime time) => (uint)new DateTimeOffset(time).ToUnixTimeSeconds();

    private static DateTime FromUnixTime(uint value) => DateTimeOffset.FromUnixTimeSeconds(value).LocalDateTime;
}
