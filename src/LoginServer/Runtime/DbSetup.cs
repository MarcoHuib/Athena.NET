using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Db;
using Athena.Net.LoginServer.Logging;

namespace Athena.Net.LoginServer.Runtime;

public static class DbSetup
{
    /// <summary>
    /// Registers the LoginDb <see cref="IDbContextFactory{LoginDbContext}"/> in the
    /// composition root, resolving connection string/provider configuration. Returns
    /// false (and registers nothing) when configuration is missing or unsupported,
    /// so the caller can fall back to a null db factory without a real container
    /// registration to resolve.
    /// </summary>
    public static bool TryAddLoginDbContext(IServiceCollection services, InterConfig interConfig, SecretConfig secrets, LoginDbTableNames tableNames)
    {
        var connectionString = ResolveConnectionString(interConfig, secrets);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            LoginLogger.Error("DB: no connection string configured (check conf/inter_athena.conf).");
            return false;
        }

        var dbProvider = ResolveDbProvider(interConfig, secrets);
        if (dbProvider != "sqlserver")
        {
            LoginLogger.Error($"DB: unsupported provider '{dbProvider}'. LoginServer is SQL Server only.");
            return false;
        }

        services.AddSingleton(tableNames);
        services.AddDbContextFactory<LoginDbContext>(optionsBuilder =>
        {
            optionsBuilder.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure());
            optionsBuilder.ConfigureWarnings(warnings =>
                warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
        });

        return true;
    }

    /// <summary>
    /// Resolves the registered <see cref="IDbContextFactory{LoginDbContext}"/>, applies
    /// migrations/waits for connectivity exactly as before, and returns a
    /// short-lived-context factory delegate for the many existing call sites that are
    /// not yet DI-aware. A new LoginDbContext is created per call; none is tied to a
    /// TCP connection's lifetime.
    /// </summary>
    public static async Task<Func<LoginDbContext?>> CreateDbFactoryAsync(IServiceProvider serviceProvider, bool autoMigrate)
    {
        try
        {
            var contextFactory = serviceProvider.GetRequiredService<IDbContextFactory<LoginDbContext>>();
            Func<LoginDbContext> factory = () => contextFactory.CreateDbContext();

            await ApplyMigrationsWithRetry(factory, autoMigrate);

            return factory;
        }
        catch (Exception ex)
        {
            LoginLogger.Error($"DB: connection check failed ({ex.Message}).");
            return () => null;
        }
    }

    private static async Task ApplyMigrationsWithRetry(Func<LoginDbContext> factory, bool autoMigrate)
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
                        LoginLogger.Status("DB: migrations applied.");
                    }
                    else
                    {
                        await dbHandle.Database.EnsureCreatedAsync();
                        LoginLogger.Status("DB: schema created (EnsureCreated).");
                    }
                }

                var canConnect = await dbHandle.Database.CanConnectAsync();
                if (canConnect)
                {
                    LoginLogger.Status("DB: connected.");
                    return;
                }

                lastError = new InvalidOperationException("Database not reachable.");
                LoginLogger.Status($"DB: waiting for database ({attempt}/{maxAttempts})...");
                await Task.Delay(delay);
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                lastError = ex;
                LoginLogger.Status($"DB: waiting for database ({attempt}/{maxAttempts})...");
                await Task.Delay(delay);
            }
            catch (Exception ex)
            {
                lastError = ex;
                break;
            }
        }

        LoginLogger.Error("DB: unable to connect.");
        if (lastError != null)
        {
            LoginLogger.Error($"DB: last error ({lastError.Message}).");
        }
    }

    private static string ResolveConnectionString(InterConfig interConfig, SecretConfig secrets)
    {
        var aspireConnection = Environment.GetEnvironmentVariable("ConnectionStrings__LoginDb");
        if (!string.IsNullOrWhiteSpace(aspireConnection))
        {
            return aspireConnection;
        }

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

    private static string ResolveDbProvider(InterConfig interConfig, SecretConfig secrets)
    {
        var envProvider = Environment.GetEnvironmentVariable("ATHENA_NET_LOGIN_DB_PROVIDER");
        if (!string.IsNullOrWhiteSpace(envProvider))
        {
            return envProvider.Trim().ToLowerInvariant();
        }

        var provider = !string.IsNullOrWhiteSpace(secrets.LoginDbProvider)
            ? secrets.LoginDbProvider
            : interConfig.LoginDbProvider;

        return string.IsNullOrWhiteSpace(provider) ? "sqlserver" : provider.Trim().ToLowerInvariant();
    }
}
