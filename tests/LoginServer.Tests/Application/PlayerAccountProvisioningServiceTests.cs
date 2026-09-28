using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Athena.Net.LoginServer.Application;
using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Db.Identity;
using Athena.Net.LoginServer.Net;
using Athena.Net.LoginServer.Tests.TestSupport;

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
        AddProvisioningStack(services, _connection, new SqliteMaxPlusOneRagnarokAccountIdAllocator());

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

        var result = await provisioning.ProvisionAsync("MarcoP", "marco@example.com", "hunter22", 'M', CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotEqual(Guid.Empty, result.IdentityUserId);
        Assert.NotEqual(Guid.Empty, result.GameAccountId);
        Assert.True(result.RagnarokAccountId > 0);

        var user = await db.Users.SingleAsync(u => u.Id == result.IdentityUserId);
        Assert.Equal("MarcoP", user.UserName);
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

        var first = await provisioning.ProvisionAsync("UserAlpha", "same@example.com", "password1", 'M', CancellationToken.None);
        Assert.True(first.Success);

        var second = await provisioning.ProvisionAsync("UserBeta", "same@example.com", "password2", 'F', CancellationToken.None);

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

    // Credential-limit validation: the stock 0x0064 login packet's username and
    // password fields are fixed-width, NUL-terminated PacketConstants.NameLength
    // (24) byte buffers, so the longest usable value is 23 characters. The
    // minimums come from the default LoginConfig (AccountNameMinLength =
    // PasswordMinLength = 6), which every test in this file (via _serviceProvider)
    // uses unless it builds its own stack.

    [Theory]
    [InlineData(5)] // one below the minimum
    [InlineData(24)] // one above the maximum (PacketConstants.NameLength)
    public async Task ProvisionAsync_UsernameOutOfBounds_FailsAndCreatesNothing(int length)
    {
        using var scope = _serviceProvider.CreateScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IPlayerAccountProvisioningService>();
        var db = scope.ServiceProvider.GetRequiredService<AthenaIdentityDbContext>();

        var userName = new string('u', length);
        var result = await provisioning.ProvisionAsync(userName, "boundary@example.com", "password1", 'M', CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(await db.Users.AnyAsync(u => u.UserName == userName));
    }

    [Theory]
    [InlineData(6)] // exactly AccountNameMinLength
    [InlineData(23)] // exactly PacketConstants.NameLength - 1
    public async Task ProvisionAsync_UsernameAtBoundary_Succeeds(int length)
    {
        using var scope = _serviceProvider.CreateScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IPlayerAccountProvisioningService>();

        var userName = new string('b', length);
        var result = await provisioning.ProvisionAsync(userName, $"boundary{length}@example.com", "password1", 'M', CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(24)]
    public async Task ProvisionAsync_PasswordOutOfBounds_FailsAndCreatesNothing(int length)
    {
        using var scope = _serviceProvider.CreateScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IPlayerAccountProvisioningService>();
        var db = scope.ServiceProvider.GetRequiredService<AthenaIdentityDbContext>();

        var userName = $"pwdbound{length}";
        var password = new string('p', length);
        var result = await provisioning.ProvisionAsync(userName, "pwdboundary@example.com", password, 'M', CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(await db.Users.AnyAsync(u => u.UserName == userName));
    }

    [Theory]
    [InlineData('X', false)]
    [InlineData('Q', false)]
    [InlineData('m', true)] // lowercase is normalized (char.ToUpperInvariant) before the M/F check
    [InlineData('f', true)]
    public async Task ProvisionAsync_SexValidation(char sex, bool expectedSuccess)
    {
        using var scope = _serviceProvider.CreateScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IPlayerAccountProvisioningService>();
        var db = scope.ServiceProvider.GetRequiredService<AthenaIdentityDbContext>();

        var userName = $"sextest{(int)sex}";
        var result = await provisioning.ProvisionAsync(userName, $"sextest{(int)sex}@example.com", "password1", sex, CancellationToken.None);

        Assert.Equal(expectedSuccess, result.Success);
        if (!expectedSuccess)
        {
            Assert.False(await db.Users.AnyAsync(u => u.UserName == userName));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("missing-domain@")]
    [InlineData("@missing-local.com")]
    public async Task ProvisionAsync_InvalidEmail_FailsAndCreatesNothing(string email)
    {
        using var scope = _serviceProvider.CreateScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IPlayerAccountProvisioningService>();
        var db = scope.ServiceProvider.GetRequiredService<AthenaIdentityDbContext>();

        var userName = $"emailtest{Math.Abs(email.GetHashCode())}";
        var result = await provisioning.ProvisionAsync(userName, email, "password1", 'M', CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(await db.Users.AnyAsync(u => u.UserName == userName));
    }

    [Fact]
    public async Task ProvisionAsync_UsesTheAllocatedRagnarokAccountId()
    {
        // Application-level semantics, independent of which IRagnarokAccountIdAllocator
        // is wired in: the provisioned game account's RagnarokAccountId must be
        // exactly whatever the allocator returned.
        var services = new ServiceCollection();
        AddProvisioningStack(services, _connection, new FixedRagnarokAccountIdAllocator(9_000_042));
        await using var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IPlayerAccountProvisioningService>();

        var result = await provisioning.ProvisionAsync("FixedIdUser", "fixed@example.com", "password1", 'M', CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(9_000_042u, result.RagnarokAccountId);
    }

    [Fact]
    public async Task ProvisionAsync_AllocatorReturnsAColludingId_FailsViaTheRealUniqueConstraint()
    {
        // Defense in depth: even if an IRagnarokAccountIdAllocator implementation
        // were buggy and handed out a duplicate (which SqlServerSequenceRagnarokAccountIdAllocator
        // cannot do, but this proves PlayerAccountProvisioningService does not
        // simply trust the allocator), the database's unique index on
        // RagnarokAccountId must still reject the second insert and roll back
        // cleanly rather than silently colliding two accounts.
        var services = new ServiceCollection();
        AddProvisioningStack(services, _connection, new FixedRagnarokAccountIdAllocator(9_000_099));
        await using var provider = services.BuildServiceProvider();

        using (var firstScope = provider.CreateScope())
        {
            var provisioning = firstScope.ServiceProvider.GetRequiredService<IPlayerAccountProvisioningService>();
            var first = await provisioning.ProvisionAsync("CollisionOne", "collision1@example.com", "password1", 'M', CancellationToken.None);
            Assert.True(first.Success, first.ErrorMessage);
        }

        using var secondScope = provider.CreateScope();
        var db = secondScope.ServiceProvider.GetRequiredService<AthenaIdentityDbContext>();
        var secondProvisioning = secondScope.ServiceProvider.GetRequiredService<IPlayerAccountProvisioningService>();
        var second = await secondProvisioning.ProvisionAsync("CollisionTwo", "collision2@example.com", "password2", 'F', CancellationToken.None);

        Assert.False(second.Success);
        Assert.Equal(1, await db.Users.CountAsync(u => u.UserName == "CollisionOne" || u.UserName == "CollisionTwo"));
    }

    [Fact]
    public async Task ProvisionAsync_FailureAfterIdentityUserIsTracked_LeavesTheSharedDbContextCleanForTheNextCall()
    {
        // Regression test for the execution-strategy retry-safety concern: the
        // production ExecuteAsync(...) wrapper reuses the same DbContext
        // instance (_db) across a retried delegate invocation. If a failure
        // happens after UserManager.CreateAsync's own internal SaveChangesAsync
        // has already tracked the new AthenaIdentityUser, but before this
        // service's own SaveChangesAsync/CommitAsync, that tracked entity must
        // not corrupt a later attempt on the same DbContext (this is what
        // PlayerAccountProvisioningService.ChangeTracker.Clear() at the top of
        // the retry delegate exists to prevent). SQLite's default execution
        // strategy never retries automatically (unlike SQL Server's
        // SqlServerRetryingExecutionStrategy), so this drives the same
        // scenario directly: fail once via the allocator after the Identity
        // user is already tracked (the allocator's exception propagates out of
        // ProvisionAsync uncaught, same as production - only DbUpdateException
        // is caught there - with the `await using` transaction rolling back on
        // disposal), then call ProvisionAsync again on the same scoped
        // service/DbContext, exactly as a retried delegate invocation would
        // reuse it.
        var allocator = new FailOnceRagnarokAccountIdAllocator(9_000_200);
        var services = new ServiceCollection();
        AddProvisioningStack(services, _connection, allocator);
        await using var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IPlayerAccountProvisioningService>();
        var db = scope.ServiceProvider.GetRequiredService<AthenaIdentityDbContext>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provisioning.ProvisionAsync("RetrySafe", "retrysafe@example.com", "password1", 'M', CancellationToken.None));

        // Simulate what a retried execution-strategy delegate invocation does:
        // call ProvisionAsync again on the very same DbContext/UserManager.
        var retriedAttempt = await provisioning.ProvisionAsync("RetrySafe", "retrysafe@example.com", "password1", 'M', CancellationToken.None);

        Assert.True(retriedAttempt.Success, retriedAttempt.ErrorMessage);
        Assert.Equal(1, await db.Users.CountAsync(u => u.UserName == "RetrySafe"));
        Assert.Equal(1, await db.GameAccounts.CountAsync(a => a.Id == retriedAttempt.GameAccountId));
    }

    private sealed class FixedRagnarokAccountIdAllocator : IRagnarokAccountIdAllocator
    {
        private readonly uint _value;

        public FixedRagnarokAccountIdAllocator(uint value) => _value = value;

        public Task<uint> AllocateAsync(AthenaIdentityDbContext db, CancellationToken cancellationToken) => Task.FromResult(_value);
    }

    /// <summary>
    /// Throws on its first call (simulating a transient failure that occurs
    /// after UserManager.CreateAsync has already tracked/saved the Identity
    /// user but before the game account is persisted) and succeeds on every
    /// call after that - standing in for the point in
    /// PlayerAccountProvisioningService.ProvisionAsync where a real SQL
    /// Server transient fault (triggering an execution-strategy retry) could
    /// occur.
    /// </summary>
    private sealed class FailOnceRagnarokAccountIdAllocator : IRagnarokAccountIdAllocator
    {
        private readonly uint _value;
        private bool _hasFailed;

        public FailOnceRagnarokAccountIdAllocator(uint value) => _value = value;

        public Task<uint> AllocateAsync(AthenaIdentityDbContext db, CancellationToken cancellationToken)
        {
            if (!_hasFailed)
            {
                _hasFailed = true;
                throw new InvalidOperationException("Simulated transient allocation failure.");
            }

            return Task.FromResult(_value);
        }
    }

    private static void AddProvisioningStack(ServiceCollection services, SqliteConnection connection, IRagnarokAccountIdAllocator allocator, LoginConfig? config = null)
    {
        services.AddDbContext<AthenaIdentityDbContext>(options => options.UseSqlite(connection));
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
        services.AddSingleton(new LoginConfigStore(config ?? new LoginConfig()));
        services.AddScoped<IRagnarokAccountIdAllocator>(_ => allocator);
        services.AddScoped<IPlayerAccountProvisioningService, PlayerAccountProvisioningService>();
    }
}
