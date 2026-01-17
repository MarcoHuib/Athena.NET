using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Rathena.LoginServer.Config;

namespace Rathena.LoginServer.Db;

public sealed class DesignTimeLoginDbContextFactory : IDesignTimeDbContextFactory<LoginDbContext>
{
    public LoginDbContext CreateDbContext(string[] args)
    {
        var baseDir = FindRepoRoot(Environment.CurrentDirectory);
        var secretsPath = Path.Combine(baseDir, "athenadotnet", "SolutionFiles", "Secrets", "secret.json");
        var interConfigPath = Path.Combine(baseDir, "conf", "inter_athena.conf");

        var secrets = SecretConfig.Load(secretsPath);
        var interConfig = InterConfigLoader.Load(interConfigPath);

        var connectionString = ResolveConnectionString(interConfig, secrets);
        var provider = ResolveProvider(interConfig, secrets, connectionString);
        if (string.IsNullOrWhiteSpace(provider))
        {
            provider = "sqlserver";
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = provider == "mysql"
                ? "Server=127.0.0.1;Port=3306;Database=rathena;User=ragnarok;Password=ragnarok;SslMode=None;"
                : "Server=localhost;Database=rathena;User ID=sa;Password=Password123!;Encrypt=True;TrustServerCertificate=True;";
        }

        var optionsBuilder = new DbContextOptionsBuilder<LoginDbContext>();
        if (provider == "mysql")
        {
            optionsBuilder.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString));
        }
        else
        {
            optionsBuilder.UseSqlServer(connectionString);
        }

        var tableNames = new LoginDbTableNames
        {
            AccountTable = interConfig.LoginAccountTable,
            IpBanTable = interConfig.IpBanTable,
            LoginLogTable = interConfig.LoginLogTable,
            GlobalAccRegNumTable = interConfig.GlobalAccRegNumTable,
            GlobalAccRegStrTable = interConfig.GlobalAccRegStrTable,
        };

        return new LoginDbContext(optionsBuilder.Options, tableNames);
    }

    private static string ResolveConnectionString(InterConfig interConfig, SecretConfig secrets)
    {
        var envConnection = Environment.GetEnvironmentVariable("RATHENA_LOGIN_DB_CONNECTION");
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

    private static string ResolveProvider(InterConfig interConfig, SecretConfig secrets, string connectionString)
    {
        var envProvider = Environment.GetEnvironmentVariable("RATHENA_LOGIN_DB_PROVIDER");
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

    private static string FindRepoRoot(string start)
    {
        var current = new DirectoryInfo(start);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "rAthena.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return start;
    }
}
