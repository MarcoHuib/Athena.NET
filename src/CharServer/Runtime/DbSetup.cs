using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Athena.Net.CharServer.Config;
using Athena.Net.CharServer.Db;
using Athena.Net.CharServer.Logging;

namespace Athena.Net.CharServer.Runtime;

public static class DbSetup
{
    public static Func<CharDbContext?> Configure(InterConfig interConfig, SecretConfig secrets, CharDbTableNames tableNames, bool autoMigrate)
    {
        var connectionString = ResolveConnectionString(interConfig, secrets);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            CharLogger.Error("DB: no connection string configured (check conf/inter_athena.conf). ");
            return () => null;
        }

        var dbProvider = ResolveDbProvider(interConfig, secrets, connectionString);
        try
        {
            var optionsBuilder = new DbContextOptionsBuilder<CharDbContext>();
            if (dbProvider == "sqlserver")
            {
                optionsBuilder.UseSqlServer(connectionString, sql =>
                    sql.EnableRetryOnFailure());
            }
            else if (dbProvider == "mysql")
            {
                optionsBuilder.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString), mysql =>
                    mysql.EnableRetryOnFailure());
            }
            else
            {
                CharLogger.Error($"DB: unsupported provider '{dbProvider}'.");
                return () => null;
            }

            optionsBuilder.ConfigureWarnings(warnings =>
                warnings.Ignore(RelationalEventId.PendingModelChangesWarning));

            var options = optionsBuilder.Options;
            var factory = () => new CharDbContext(options, tableNames);

            ApplyMigrationsWithRetry(factory, autoMigrate).GetAwaiter().GetResult();

            return factory;
        }
        catch (Exception ex)
        {
            CharLogger.Error($"DB: connection check failed ({ex.Message}).");
            return () => null;
        }
    }

    private static async Task ApplyMigrationsWithRetry(Func<CharDbContext> factory, bool autoMigrate)
    {
        const int maxAttempts = 60;
        var delay = TimeSpan.FromSeconds(2);
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await using var dbHandle = factory();

                if (autoMigrate)
                {
                    var hasMigrations = dbHandle.Database.GetMigrations().Any();
                    if (hasMigrations)
                    {
                        await dbHandle.Database.MigrateAsync();
                        CharLogger.Status("DB: migrations applied.");
                    }
                    else
                    {
                        await dbHandle.Database.EnsureCreatedAsync();
                        CharLogger.Status("DB: schema created (EnsureCreated).");
                    }
                }

                var canConnect = await dbHandle.Database.CanConnectAsync();
                if (canConnect)
                {
                    CharLogger.Status("DB: connected.");
                    return;
                }

                lastError = new InvalidOperationException("Database not reachable.");
                CharLogger.Status($"DB: waiting for database ({attempt}/{maxAttempts})...");
                await Task.Delay(delay);
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                lastError = ex;
                CharLogger.Status($"DB: waiting for database ({attempt}/{maxAttempts})...");
                await Task.Delay(delay);
            }
            catch (Exception ex)
            {
                lastError = ex;
                break;
            }
        }

        CharLogger.Error("DB: unable to connect.");
        if (lastError != null)
        {
            CharLogger.Error($"DB: last error ({lastError.Message}).");
        }
    }

    private static string ResolveConnectionString(InterConfig interConfig, SecretConfig secrets)
    {
        var aspireConnection = Environment.GetEnvironmentVariable("ConnectionStrings__CharDb");
        if (!string.IsNullOrWhiteSpace(aspireConnection))
        {
            return aspireConnection;
        }

        var envConnection = Environment.GetEnvironmentVariable("ATHENA_NET_CHAR_DB_CONNECTION");
        if (!string.IsNullOrWhiteSpace(envConnection))
        {
            return envConnection;
        }

        if (!string.IsNullOrWhiteSpace(secrets.CharDbConnectionString))
        {
            return secrets.CharDbConnectionString;
        }

        return interConfig.CharDbConnectionString;
    }

    private static string ResolveDbProvider(InterConfig interConfig, SecretConfig secrets, string connectionString)
    {
        var envProvider = Environment.GetEnvironmentVariable("ATHENA_NET_CHAR_DB_PROVIDER");
        if (!string.IsNullOrWhiteSpace(envProvider))
        {
            return envProvider.Trim().ToLowerInvariant();
        }

        var provider = !string.IsNullOrWhiteSpace(secrets.CharDbProvider)
            ? secrets.CharDbProvider
            : interConfig.CharDbProvider;

        if (!string.IsNullOrWhiteSpace(provider))
        {
            return provider.Trim().ToLowerInvariant();
        }

        return GuessDbProvider(connectionString);
    }

    private static string GuessDbProvider(string connectionString)
    {
        if (connectionString.Contains("Port=", StringComparison.OrdinalIgnoreCase) ||
            connectionString.Contains("SslMode=", StringComparison.OrdinalIgnoreCase))
        {
            return "mysql";
        }

        if (connectionString.Contains("TrustServerCertificate=", StringComparison.OrdinalIgnoreCase) ||
            connectionString.Contains("Encrypt=", StringComparison.OrdinalIgnoreCase) ||
            connectionString.Contains("User ID=", StringComparison.OrdinalIgnoreCase))
        {
            return "sqlserver";
        }

        return string.Empty;
    }
}
