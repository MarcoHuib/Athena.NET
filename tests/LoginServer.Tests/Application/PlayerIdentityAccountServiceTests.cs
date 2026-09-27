using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Athena.Net.LoginServer.Application;
using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Db.Identity;
using Athena.Net.LoginServer.Tests.TestSupport;

namespace Athena.Net.LoginServer.Tests.Application;

/// <summary>
/// PlayerIdentityAccountService.ChangeEmailAsync goes through UserManager
/// rather than writing Email/NormalizedEmail/EmailConfirmed by hand, so
/// Identity's own normalization and uniqueness validation apply automatically -
/// covered here by the duplicate-email case.
/// </summary>
public sealed class PlayerIdentityAccountServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public PlayerIdentityAccountServiceTests()
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
        services.AddSingleton(new LoginConfigStore(new LoginConfig()));
        services.AddScoped<IRagnarokAccountIdAllocator, SqliteMaxPlusOneRagnarokAccountIdAllocator>();
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

    private async Task<ProvisionPlayerAccountResult> ProvisionAsync(string userName, string email)
    {
        using var scope = _serviceProvider.CreateScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IPlayerAccountProvisioningService>();
        var result = await provisioning.ProvisionAsync(userName, email, "password1", 'M', CancellationToken.None);
        Assert.True(result.Success, result.ErrorMessage);
        return result;
    }

    [Fact]
    public async Task ChangeEmailAsync_GameAccountNotFound_ReturnsFailure()
    {
        var service = new PlayerIdentityAccountService(new SingleScopeFactory(_serviceProvider));

        var result = await service.ChangeEmailAsync(999_999, "old@example.com", "new@example.com", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ChangeEmailFailureReason.GameAccountNotFound, result.FailureReason);
    }

    [Fact]
    public async Task ChangeEmailAsync_NewEmailAlreadyInUse_RejectedByIdentity_AndLeavesEmailUnchanged()
    {
        await ProvisionAsync("takenname", "taken@example.com");
        var provisioned = await ProvisionAsync("changer", "changer@example.com");

        var service = new PlayerIdentityAccountService(new SingleScopeFactory(_serviceProvider));
        var result = await service.ChangeEmailAsync(provisioned.RagnarokAccountId, "changer@example.com", "taken@example.com", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ChangeEmailFailureReason.RejectedByIdentity, result.FailureReason);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AthenaIdentityDbContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == provisioned.IdentityUserId);
        Assert.Equal("changer@example.com", user.Email);
    }

    private sealed class SingleScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceProvider _root;

        public SingleScopeFactory(IServiceProvider root) => _root = root;

        public IServiceScope CreateScope() => _root.CreateScope();
    }
}
