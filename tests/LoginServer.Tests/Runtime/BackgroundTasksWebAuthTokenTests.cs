using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Db.Identity;
using Athena.Net.LoginServer.Net;
using Athena.Net.LoginServer.Runtime;

namespace Athena.Net.LoginServer.Tests.Runtime;

/// <summary>
/// Regression coverage for BackgroundTasks.DisableWebAuthTokenAsync, the
/// LoginState.OnAutoDisconnect callback fired when a pending Login-&gt;Char
/// handoff times out (see LoginState.ScheduleWaitingDisconnect). Player
/// WebAuthToken state lives on AthenaGameAccount (Identity schema), looked up
/// by RagnarokAccountId - never the legacy LoginDbContext.Accounts table,
/// which service accounts use and which never has web auth tokens.
/// </summary>
public sealed class BackgroundTasksWebAuthTokenTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public BackgroundTasksWebAuthTokenTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var db = CreateDb();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private AthenaIdentityDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AthenaIdentityDbContext>().UseSqlite(_connection).Options;
        return new AthenaIdentityDbContext(options);
    }

    private async Task<(Guid IdentityUserId, Guid GameAccountId, uint RagnarokAccountId)> SeedGameAccountAsync(bool webAuthTokenEnabled)
    {
        await using var db = CreateDb();
        var user = new AthenaIdentityUser { Id = Guid.NewGuid(), UserName = "autodisconnectuser", NormalizedUserName = "AUTODISCONNECTUSER", Email = "a@example.com", NormalizedEmail = "A@EXAMPLE.COM" };
        db.Users.Add(user);

        var account = new AthenaGameAccount
        {
            Id = Guid.NewGuid(),
            IdentityUserId = user.Id,
            RagnarokAccountId = 2000123,
            WebAuthToken = webAuthTokenEnabled ? "abc123" : null,
            WebAuthTokenEnabled = webAuthTokenEnabled,
        };
        db.GameAccounts.Add(account);
        await db.SaveChangesAsync();

        return (user.Id, account.Id, account.RagnarokAccountId);
    }

    [Fact]
    public async Task DisableWebAuthTokenAsync_AccountNotOnline_DisablesTokenOnAthenaGameAccount()
    {
        var seeded = await SeedGameAccountAsync(webAuthTokenEnabled: true);
        var configStore = new LoginConfigStore(new LoginConfig { UseWebAuthToken = true, DisableWebTokenDelayMs = 0 });
        var state = new LoginState();

        await BackgroundTasks.DisableWebAuthTokenAsync(seeded.RagnarokAccountId, configStore, state, CreateDb, CancellationToken.None);

        await using var db = CreateDb();
        var account = await db.GameAccounts.AsNoTracking().SingleAsync(a => a.Id == seeded.GameAccountId);
        Assert.False(account.WebAuthTokenEnabled);
    }

    [Fact]
    public async Task DisableWebAuthTokenAsync_AccountStillOnline_LeavesTokenEnabled()
    {
        var seeded = await SeedGameAccountAsync(webAuthTokenEnabled: true);
        var configStore = new LoginConfigStore(new LoginConfig { UseWebAuthToken = true, DisableWebTokenDelayMs = 0 });
        var state = new LoginState();
        state.AddOnlineUser(1, seeded.RagnarokAccountId);

        await BackgroundTasks.DisableWebAuthTokenAsync(seeded.RagnarokAccountId, configStore, state, CreateDb, CancellationToken.None);

        await using var db = CreateDb();
        var account = await db.GameAccounts.AsNoTracking().SingleAsync(a => a.Id == seeded.GameAccountId);
        Assert.True(account.WebAuthTokenEnabled, "A still-online account's token must not be disabled.");
    }

    [Fact]
    public async Task DisableWebAuthTokenAsync_UseWebAuthTokenDisabled_DoesNotTouchDatabase()
    {
        var seeded = await SeedGameAccountAsync(webAuthTokenEnabled: true);
        var configStore = new LoginConfigStore(new LoginConfig { UseWebAuthToken = false });
        var state = new LoginState();

        await BackgroundTasks.DisableWebAuthTokenAsync(seeded.RagnarokAccountId, configStore, state, CreateDb, CancellationToken.None);

        await using var db = CreateDb();
        var account = await db.GameAccounts.AsNoTracking().SingleAsync(a => a.Id == seeded.GameAccountId);
        Assert.True(account.WebAuthTokenEnabled);
    }
}
