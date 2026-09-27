using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Athena.Net.LoginServer.Config;

namespace Athena.Net.LoginServer.Db;

public sealed class DesignTimeLoginDbContextFactory : IDesignTimeDbContextFactory<LoginDbContext>
{
    public LoginDbContext CreateDbContext(string[] args)
    {
        var baseDir = FindRepoRoot(Environment.CurrentDirectory);
        var secretsPath = Path.Combine(baseDir, "solutionfiles", "secrets", "secret.json");
        var interConfigPath = Path.Combine(baseDir, "conf", "inter_athena.conf");

        var secrets = SecretConfig.Load(secretsPath);
        var interConfig = InterConfigLoader.Load(interConfigPath);

        var connectionString = ResolveConnectionString(interConfig, secrets);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = "Server=localhost;Database=athena.net;User ID=sa;Password=Password123!;Encrypt=True;TrustServerCertificate=True;";
        }

        var optionsBuilder = new DbContextOptionsBuilder<LoginDbContext>();
        optionsBuilder.UseSqlServer(connectionString);

        var tableNames = new LoginDbTableNames
        {
            IpBanTable = interConfig.IpBanTable,
            LoginLogTable = interConfig.LoginLogTable,
            GlobalAccRegNumTable = interConfig.GlobalAccRegNumTable,
            GlobalAccRegStrTable = interConfig.GlobalAccRegStrTable,
        };

        return new LoginDbContext(optionsBuilder.Options, tableNames);
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

    private static string FindRepoRoot(string start)
    {
        var current = new DirectoryInfo(start);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "athena.net.sln")) ||
                Directory.Exists(Path.Combine(current.FullName, "solutionfiles")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return start;
    }
}
