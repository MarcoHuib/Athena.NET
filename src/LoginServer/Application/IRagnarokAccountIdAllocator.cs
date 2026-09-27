using Athena.Net.LoginServer.Db.Identity;

namespace Athena.Net.LoginServer.Application;

/// <summary>
/// Allocates the next legacy uint32 compatibility id (<see cref="AthenaGameAccount.RagnarokAccountId"/>)
/// for a newly provisioned game account. Isolated behind this interface
/// because the concurrency-safe production implementation
/// (<see cref="SqlServerSequenceRagnarokAccountIdAllocator"/>) is a SQL Server
/// database SEQUENCE and cannot be exercised by this project's SQLite-backed
/// tests for <see cref="PlayerAccountProvisioningService"/>. Tests substitute
/// a simpler allocator and verify PlayerAccountProvisioningService's own
/// behavior (it uses whatever the allocator returns, and the database's real
/// unique constraint on RagnarokAccountId is still the final safety net); the
/// SQL Server migration that creates the sequence is authoritative for actual
/// concurrency safety.
/// </summary>
public interface IRagnarokAccountIdAllocator
{
    Task<uint> AllocateAsync(AthenaIdentityDbContext db, CancellationToken cancellationToken);
}
