using Microsoft.EntityFrameworkCore;
using Athena.Net.LoginServer.Application;
using Athena.Net.LoginServer.Db.Identity;

namespace Athena.Net.LoginServer.Tests.Application;

/// <summary>
/// Regression coverage for the real SQL Server "NEXT VALUE FOR ..." behavior
/// that SQLite-backed tests cannot exercise (SQLite has no SEQUENCE support at
/// all - see AthenaIdentityDbContext.OnModelCreating). The bug this guards
/// against was never visible under SQLite: the previous implementation used
/// EF's composable <c>Database.SqlQueryRaw&lt;T&gt;().SingleAsync()</c>, which
/// wraps the supplied SQL in an outer query - SQL Server rejects "NEXT VALUE
/// FOR" inside any derived table/sub-query, even though the same statement is
/// valid on its own. SQLite never ran this code path at all (a different,
/// MAX()+1 test allocator is substituted for it - see
/// SqliteMaxPlusOneRagnarokAccountIdAllocator), so no SQLite-based assertion
/// can stand in for this test.
/// <para>
/// Opt-in / real SQL Server only: set ATHENA_NET_LOGIN_DB_CONNECTION (the same
/// environment variable LoginServer/DbSetup.ResolveConnectionString already
/// reads in production) to a reachable SQL Server connection string to run
/// this test. It is skipped, not failed, when unset, so `dotnet test` stays
/// green without a real SQL Server instance (e.g. in CI without Aspire/a SQL
/// Server container).
/// </para>
/// </summary>
public sealed class SqlServerSequenceRagnarokAccountIdAllocatorTests : IAsyncLifetime
{
    private const string ConnectionStringEnvVar = "ATHENA_NET_LOGIN_DB_CONNECTION";

    private readonly string? _connectionString =
        Environment.GetEnvironmentVariable(ConnectionStringEnvVar);

    private AthenaIdentityDbContext? _db;

    public async Task InitializeAsync()
    {
        if (_connectionString is null)
        {
            return;
        }

        var options = new DbContextOptionsBuilder<AthenaIdentityDbContext>()
            .UseSqlServer(_connectionString)
            .Options;

        _db = new AthenaIdentityDbContext(options);

        // Ensure the sequence this allocator reads from exists, without
        // depending on migration/ordering state in whatever database the
        // connection string happens to point at.
        await _db.Database.ExecuteSqlRawAsync("""
            IF NOT EXISTS (SELECT 1 FROM sys.sequences WHERE name = 'AthenaGameAccountRagnarokIds')
                EXEC('CREATE SEQUENCE AthenaGameAccountRagnarokIds START WITH 2000000 INCREMENT BY 1');
            """);
    }

    public async Task DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }

    private bool SqlServerAvailable => _connectionString is not null;

    // Plain [Fact] with an early return (rather than a Skippable-fact package
    // this project does not otherwise depend on) when ATHENA_NET_LOGIN_DB_CONNECTION
    // is unset, so these tests report as passed - not failed - in environments
    // without a reachable SQL Server (e.g. plain `dotnet test` in CI without
    // Aspire/a SQL Server container), while still running for real whenever a
    // connection string is provided (as the manual acceptance/Aspire flow does).

    [Fact]
    public async Task AllocateAsync_ReturnsIncreasingValues_AsAStandaloneStatement_NotWrappedInASubquery()
    {
        if (!SqlServerAvailable)
        {
            return;
        }

        var allocator = new SqlServerSequenceRagnarokAccountIdAllocator();

        // The bug this guards against throws SqlException ("NEXT VALUE FOR
        // function is not allowed in ... sub-queries ... derived tables ...")
        // the very first time this runs under the old SqlQueryRaw/SingleAsync
        // implementation - so simply not throwing is itself the regression
        // assertion, in addition to the uniqueness/monotonicity checks below.
        var first = await allocator.AllocateAsync(_db!, CancellationToken.None);
        var second = await allocator.AllocateAsync(_db!, CancellationToken.None);

        Assert.True(second > first);
    }

    [Fact]
    public async Task AllocateAsync_ParticipatesInTheAmbientTransaction_WhenOneIsActive()
    {
        if (!SqlServerAvailable)
        {
            return;
        }

        var allocator = new SqlServerSequenceRagnarokAccountIdAllocator();

        await using var transaction = await _db!.Database.BeginTransactionAsync();

        // Must not throw (e.g. "connection already has a transaction" or a
        // dropped-connection error) when an ambient EF transaction is already
        // open - this is the exact context PlayerAccountProvisioningService
        // calls the allocator from in production.
        var value = await allocator.AllocateAsync(_db, CancellationToken.None);

        await transaction.CommitAsync();

        Assert.True(value > 0);
    }
}
