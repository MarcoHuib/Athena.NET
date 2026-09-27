using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Db.Identity;
using Athena.Net.LoginServer.Logging;

namespace Athena.Net.LoginServer.Runtime;

/// <summary>
/// Registers and bootstraps <see cref="AthenaIdentityDbContext"/> (ASP.NET Core
/// Identity plus the strictly-1:1 AthenaGameAccount table). This is a separate
/// persistence boundary from <see cref="DbSetup"/>'s LoginDb (service accounts,
/// IP bans, audit logs), even though both currently point at the same physical
/// SQL Server database.
/// </summary>
public static class IdentityDbSetup
{
    /// <summary>
    /// Registers <see cref="AthenaIdentityDbContext"/> as a scoped service (required
    /// by ASP.NET Core Identity's AddEntityFrameworkStores). Unlike LoginDb's
    /// IDbContextFactory registration, this context is resolved once per DI scope -
    /// callers must create a short-lived scope per logical operation and must never
    /// hold one open for a long-lived TCP connection (see IPlayerAuthenticationService's
    /// Identity-backed implementation for the pattern).
    /// </summary>
    public static bool TryAddAthenaIdentityDbContext(IServiceCollection services, InterConfig interConfig, SecretConfig secrets)
    {
        var connectionString = DbSetup.ResolveConnectionString(interConfig, secrets);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            LoginLogger.Error("Identity DB: no connection string configured (check conf/inter_athena.conf).");
            return false;
        }

        var dbProvider = DbSetup.ResolveDbProvider(interConfig, secrets);
        if (dbProvider != "sqlserver")
        {
            LoginLogger.Error($"Identity DB: unsupported provider '{dbProvider}'. LoginServer is SQL Server only.");
            return false;
        }

        services.AddDbContext<AthenaIdentityDbContext>(optionsBuilder =>
        {
            optionsBuilder.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure());
            optionsBuilder.ConfigureWarnings(warnings =>
                warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
        });

        return true;
    }

    /// <summary>
    /// Applies migrations (if requested) and waits for connectivity, mirroring
    /// DbSetup.ApplyMigrationsWithRetry's behavior for the scoped Identity context.
    /// </summary>
    public static async Task EnsureReadyAsync(IServiceProvider serviceProvider, bool autoMigrate)
    {
        const int maxAttempts = 60;
        var delay = TimeSpan.FromSeconds(2);
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AthenaIdentityDbContext>();

                if (autoMigrate)
                {
                    var hasMigrations = db.Database.GetMigrations().Any();
                    if (hasMigrations)
                    {
                        await db.Database.MigrateAsync();
                        LoginLogger.Status("Identity DB: migrations applied.");
                    }
                    else
                    {
                        await db.Database.EnsureCreatedAsync();
                        LoginLogger.Status("Identity DB: schema created (EnsureCreated).");
                    }
                }

                if (await db.Database.CanConnectAsync())
                {
                    LoginLogger.Status("Identity DB: connected.");
                    return;
                }

                lastError = new InvalidOperationException("Identity database not reachable.");
                LoginLogger.Status($"Identity DB: waiting for database ({attempt}/{maxAttempts})...");
                await Task.Delay(delay);
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                lastError = ex;
                LoginLogger.Status($"Identity DB: waiting for database ({attempt}/{maxAttempts})...");
                await Task.Delay(delay);
            }
            catch (Exception ex)
            {
                lastError = ex;
                break;
            }
        }

        LoginLogger.Error("Identity DB: unable to connect.");
        if (lastError != null)
        {
            LoginLogger.Error($"Identity DB: last error ({lastError.Message}).");
        }
    }
}
