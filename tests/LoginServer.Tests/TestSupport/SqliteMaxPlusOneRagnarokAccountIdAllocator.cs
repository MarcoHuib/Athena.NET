using Microsoft.EntityFrameworkCore;
using Athena.Net.LoginServer.Application;
using Athena.Net.LoginServer.Db.Identity;

namespace Athena.Net.LoginServer.Tests.TestSupport;

/// <summary>
/// Test-only <see cref="IRagnarokAccountIdAllocator"/>: SQLite (used by this
/// project's relational tests) has no equivalent to a SQL Server SEQUENCE, so
/// this substitutes a simple MAX()+1 allocator to exercise
/// PlayerAccountProvisioningService's own behavior (it uses whatever the
/// allocator returns, and the database's real unique constraint on
/// RagnarokAccountId is still the final safety net - see
/// PlayerAccountProvisioningServiceTests.ProvisionAsync_CollidingAllocatedId_FailsViaUniqueConstraint).
/// This is deliberately NOT concurrency-safe (two callers can race between the
/// MAX query and the insert) and must never be wired into production DI -
/// SqlServerSequenceRagnarokAccountIdAllocator is the authoritative
/// concurrency-safe implementation.
/// </summary>
public sealed class SqliteMaxPlusOneRagnarokAccountIdAllocator : IRagnarokAccountIdAllocator
{
    public async Task<uint> AllocateAsync(AthenaIdentityDbContext db, CancellationToken cancellationToken)
    {
        var max = await db.GameAccounts
            .Select(a => (uint?)a.RagnarokAccountId)
            .MaxAsync(cancellationToken);

        return (max ?? 1_999_999u) + 1;
    }
}
