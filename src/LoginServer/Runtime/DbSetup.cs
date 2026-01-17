using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Db;
using Athena.Net.LoginServer.Logging;

namespace Athena.Net.LoginServer.Runtime;

public static class DbSetup
{
    public static Func<LoginDbContext?> Configure(InterConfig interConfig, SecretConfig secrets, LoginDbTableNames tableNames, bool autoMigrate)
    {
        var connectionString = ResolveConnectionString(interConfig, secrets);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            LoginLogger.Error("DB: no connection string configured (check conf/inter_athena.conf).");
            return () => null;
        }

        var dbProvider = ResolveDbProvider(interConfig, secrets, connectionString);
        connectionString = ApplyMySqlCodepage(dbProvider, connectionString, interConfig.LoginDbCodepage);

        try
        {
            var optionsBuilder = new DbContextOptionsBuilder<LoginDbContext>();
            if (dbProvider == "sqlserver")
            {
                optionsBuilder.UseSqlServer(connectionString);
            }
            else if (dbProvider == "mysql")
            {
                optionsBuilder.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString));
            }
            else
            {
                LoginLogger.Error($"DB: unsupported provider '{dbProvider}'.");
                return () => null;
            }

            optionsBuilder.ConfigureWarnings(warnings =>
                warnings.Ignore(RelationalEventId.PendingModelChangesWarning));

            var options = optionsBuilder.Options;
            var factory = () => new LoginDbContext(options, tableNames);

            var dbInstance = factory();
            if (dbInstance != null)
            {
                ApplyMigrations(dbInstance, autoMigrate).GetAwaiter().GetResult();
            }
            else
            {
                LoginLogger.Error("DB: unable to create connection.");
            }

            return factory;
        }
        catch (Exception ex)
        {
            LoginLogger.Error($"DB: connection check failed ({ex.Message}).");
            return () => null;
        }
    }

    private static async Task ApplyMigrations(LoginDbContext db, bool autoMigrate)
    {
        await using var dbHandle = db;
        if (autoMigrate)
        {
            var hasMigrations = dbHandle.Database.GetMigrations().Any();
            if (hasMigrations)
            {
                await dbHandle.Database.MigrateAsync();
                LoginLogger.Status("DB: migrations applied.");
            }
            else
            {
                await dbHandle.Database.EnsureCreatedAsync();
                LoginLogger.Status("DB: schema created (EnsureCreated).");
            }
        }

        var canConnect = await dbHandle.Database.CanConnectAsync();
        LoginLogger.Status(canConnect ? "DB: connected." : "DB: unable to connect.");
    }

    private static string ResolveConnectionString(InterConfig interConfig, SecretConfig secrets)
    {
        var envConnection = Environment.GetEnvironmentVariable("ATHENA_NET_LOGIN_DB_CONNECTION");
        if (!string.IsNullOrWhiteSpace(envConnection))
        {
            return envConnection;
        }

        if (!string.IsNullOrWhiteSpace(secrets.LoginDbConnectionString))
        {
            return secrets.LoginDbConnectionString;
        }

        return interConfig.LoginDbConnectionString;
    }

    private static string ResolveDbProvider(InterConfig interConfig, SecretConfig secrets, string connectionString)
    {
        var envProvider = Environment.GetEnvironmentVariable("ATHENA_NET_LOGIN_DB_PROVIDER");
        if (!string.IsNullOrWhiteSpace(envProvider))
        {
            return envProvider.Trim().ToLowerInvariant();
        }

        var provider = !string.IsNullOrWhiteSpace(secrets.LoginDbProvider)
            ? secrets.LoginDbProvider
            : interConfig.LoginDbProvider;

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

    private static string ApplyMySqlCodepage(string provider, string connectionString, string codepage)
    {
        if (provider != "mysql")
        {
            return connectionString;
        }

        if (string.IsNullOrWhiteSpace(codepage))
        {
            return connectionString;
        }

        if (connectionString.Contains("CharSet=", StringComparison.OrdinalIgnoreCase) ||
            connectionString.Contains("Charset=", StringComparison.OrdinalIgnoreCase))
        {
            return connectionString;
        }

        return connectionString + $"CharSet={codepage};";
    }
}
