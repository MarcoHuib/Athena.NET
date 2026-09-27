using Microsoft.EntityFrameworkCore;
using Athena.Net.LoginServer.Db.Identity;

namespace Athena.Net.LoginServer.Application;

/// <summary>
/// Production <see cref="IRagnarokAccountIdAllocator"/>: allocates from the SQL
/// Server sequence "AthenaGameAccountRagnarokIds" (declared in
/// <see cref="AthenaIdentityDbContext"/> and created by its migration), which
/// is atomic under concurrent provisioning without an in-process counter and
/// without holding any lock across the allocation. SQL Server sequences are
/// intentionally not transactional: a value handed out to a provisioning
/// attempt that later rolls back is never reused, which is expected and
/// harmless (the id space is far larger than Athena.NET will ever need).
/// </summary>
public sealed class SqlServerSequenceRagnarokAccountIdAllocator : IRagnarokAccountIdAllocator
{
    public async Task<uint> AllocateAsync(AthenaIdentityDbContext db, CancellationToken cancellationToken)
    {
        var next = await db.Database
            .SqlQueryRaw<long>("SELECT NEXT VALUE FOR AthenaGameAccountRagnarokIds")
            .SingleAsync(cancellationToken);

        return (uint)next;
    }
}
