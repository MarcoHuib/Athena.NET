using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
/// <para>
/// "SELECT NEXT VALUE FOR ..." must run as a standalone SQL Server statement,
/// not as EF's composable <c>SqlQueryRaw&lt;T&gt;()</c>/<c>SingleAsync()</c>:
/// that pattern is LINQ-composable and EF wraps the supplied SQL in an outer
/// query to apply it, which SQL Server rejects with "NEXT VALUE FOR function
/// is not allowed in ... sub-queries ... derived tables" even though the same
/// text is valid as a top-level command. This instead issues the statement
/// directly through the DbContext's own <see cref="IDbConnection"/>/
/// <see cref="IDbCommand"/> (never a second connection, so it shares the
/// provisioning transaction/connection EF already owns) via
/// <see cref="System.Data.Common.DbCommand.ExecuteScalarAsync(CancellationToken)"/>.
/// </para>
/// </summary>
public sealed class SqlServerSequenceRagnarokAccountIdAllocator : IRagnarokAccountIdAllocator
{
    public async Task<uint> AllocateAsync(AthenaIdentityDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();

        // The DbContext normally owns the connection's open/close lifecycle
        // (it opens it lazily and closes it once the ambient transaction/unit
        // of work ends). Only open it ourselves - and only close what we
        // opened - when nothing else already has it open, so we never yank
        // the connection out from under an ambient transaction PlayerAccountProvisioningService
        // (or any other caller) is still using.
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT NEXT VALUE FOR AthenaGameAccountRagnarokIds";

            // Participate in the caller's ambient transaction (if any) so this
            // allocation is visible/rolled-back consistently with the rest of
            // the provisioning unit of work's connection state, even though
            // the sequence value itself is never rolled back.
            var currentTransaction = db.Database.CurrentTransaction?.GetDbTransaction();
            if (currentTransaction != null)
            {
                command.Transaction = currentTransaction;
            }

            var result = await command.ExecuteScalarAsync(cancellationToken);
            return (uint)(long)result!;
        }
        finally
        {
            if (openedHere)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }
}
