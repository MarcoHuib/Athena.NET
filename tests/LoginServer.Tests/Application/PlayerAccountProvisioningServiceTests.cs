using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Athena.Net.LoginServer.Application;
using Athena.Net.LoginServer.Db.Identity;

namespace Athena.Net.LoginServer.Tests.Application;

/// <summary>
/// Exercises PlayerAccountProvisioningService against a real relational database
/// (SQLite in-memory) rather than the EF Core InMemory provider, because these
/// tests depend on real transaction rollback and real unique-constraint
/// enforcement, neither of which the InMemory provider supports.
/// </summary>
public sealed class PlayerAccountProvisioningServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public PlayerAccountProvisioningServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AthenaIdentityDbContext>(options => options.UseSqlite(_connection));
        services.AddIdentityCore<AthenaIdentityUser>(options =>
        {
            options.User.RequireUniqueEmail = true;
            options.Password.RequiredLength = 6;
            options.Password.RequireDigit = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireNonAlphanumeric = false;
        })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AthenaIdentityDbContext>();
        services.AddScoped<IPlayerAccountProvisioningService, PlayerAccountProvisioningService>();

        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<AthenaIdentityDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task ProvisionAsync_CreatesIdentityUserAndGameAccount_Consistently()
    {
        using var scope = _serviceProvider.CreateScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IPlayerAccountProvisioningService>();
        var db = scope.ServiceProvider.GetRequiredService<AthenaIdentityDbContext>();

        var result = await provisioning.ProvisionAsync("Marco", "marco@example.com", "hunter22", 'M', CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotEqual(Guid.Empty, result.IdentityUserId);
        Assert.NotEqual(Guid.Empty, result.GameAccountId);
        Assert.True(result.RagnarokAccountId > 0);

        var user = await db.Users.SingleAsync(u => u.Id == result.IdentityUserId);
        Assert.Equal("Marco", user.UserName);
        Assert.Equal("marco@example.com", user.Email);
        Assert.NotNull(user.PasswordHash);
        Assert.NotEqual("hunter22", user.PasswordHash); // never stored in plaintext

        var gameAccount = await db.GameAccounts.SingleAsync(a => a.Id == result.GameAccountId);
        Assert.Equal(result.IdentityUserId, gameAccount.IdentityUserId);
        Assert.Equal("M", gameAccount.Sex);
        Assert.Equal(result.RagnarokAccountId, gameAccount.RagnarokAccountId);
    }

    [Fact]
    public async Task ProvisionAsync_AllocatesDistinctIncreasingRagnarokAccountIds()
    {
        using var scope = _serviceProvider.CreateScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IPlayerAccountProvisioningService>();

        var first = await provisioning.ProvisionAsync("PlayerOne", "one@example.com", "password1", 'M', CancellationToken.None);
        var second = await provisioning.ProvisionAsync("PlayerTwo", "two@example.com", "password2", 'F', CancellationToken.None);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.True(second.RagnarokAccountId > first.RagnarokAccountId);
    }

    [Fact]
    public async Task ProvisionAsync_DuplicateUserName_FailsAndCreatesNothing()
    {
        using var scope = _serviceProvider.CreateScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IPlayerAccountProvisioningService>();
        var db = scope.ServiceProvider.GetRequiredService<AthenaIdentityDbContext>();

        var first = await provisioning.ProvisionAsync("DupUser", "dup1@example.com", "password1", 'M', CancellationToken.None);
        Assert.True(first.Success);

        var second = await provisioning.ProvisionAsync("DupUser", "dup2@example.com", "password2", 'F', CancellationToken.None);

        Assert.False(second.Success);
        Assert.NotNull(second.ErrorMessage);
        Assert.Equal(1, await db.Users.CountAsync());
        Assert.Equal(1, await db.GameAccounts.CountAsync());
    }

    [Fact]
    public async Task ProvisionAsync_DuplicateEmail_FailsAndCreatesNothing()
    {
        using var scope = _serviceProvider.CreateScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IPlayerAccountProvisioningService>();
        var db = scope.ServiceProvider.GetRequiredService<AthenaIdentityDbContext>();

        var first = await provisioning.ProvisionAsync("UserA", "same@example.com", "password1", 'M', CancellationToken.None);
        Assert.True(first.Success);

        var second = await provisioning.ProvisionAsync("UserB", "same@example.com", "password2", 'F', CancellationToken.None);

        Assert.False(second.Success);
        Assert.Equal(1, await db.Users.CountAsync());
        Assert.Equal(1, await db.GameAccounts.CountAsync());
    }

    [Fact]
    public async Task GameAccount_SecondAccountForSameIdentityUser_ViolatesUniqueConstraint()
    {
        ProvisionPlayerAccountResult result;
        using (var provisionScope = _serviceProvider.CreateScope())
        {
            var provisioning = provisionScope.ServiceProvider.GetRequiredService<IPlayerAccountProvisioningService>();
            result = await provisioning.ProvisionAsync("OnlyOneAccount", "one-account@example.com", "password1", 'M', CancellationToken.None);
            Assert.True(result.Success);
        }

        // A fresh scope/DbContext avoids EF's in-memory navigation-fixup for the
        // 1:1 relationship and lets the real unique-index violation surface, as it
        // would for two independent requests attempting the same thing.
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AthenaIdentityDbContext>();
        db.GameAccounts.Add(new AthenaGameAccount
        {
            Id = Guid.NewGuid(),
            IdentityUserId = result.IdentityUserId, // same Identity user - must be rejected
            RagnarokAccountId = result.RagnarokAccountId + 100,
            Sex = "M",
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task GameAccount_DuplicateRagnarokAccountId_ViolatesUniqueConstraint()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AthenaIdentityDbContext>();
        var provisioning = scope.ServiceProvider.GetRequiredService<IPlayerAccountProvisioningService>();

        var first = await provisioning.ProvisionAsync("RagOne", "ragone@example.com", "password1", 'M', CancellationToken.None);
        var second = await provisioning.ProvisionAsync("RagTwo", "ragtwo@example.com", "password2", 'F', CancellationToken.None);
        Assert.True(first.Success);
        Assert.True(second.Success);

        var secondAccount = await db.GameAccounts.SingleAsync(a => a.Id == second.GameAccountId);
        secondAccount.RagnarokAccountId = first.RagnarokAccountId; // force a collision

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public void IdentityUser_Id_And_GameAccount_Id_AreGuids()
    {
        Assert.Equal(typeof(Guid), typeof(AthenaIdentityUser).GetProperty("Id")!.PropertyType);
        Assert.Equal(typeof(Guid), typeof(AthenaGameAccount).GetProperty("Id")!.PropertyType);
    }
}
