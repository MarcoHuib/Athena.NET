using System.Text.Json;

namespace Rathena.LoginServer.Config;

public sealed class SecretConfig
{
    public string LoginDbProvider { get; init; } = string.Empty;
    public string LoginDbConnectionString { get; init; } = string.Empty;
    public string SqlServerSaPassword { get; init; } = string.Empty;

    public static SecretConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return new SecretConfig();
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;

            var provider = string.Empty;
            var connectionString = string.Empty;
            var saPassword = string.Empty;

            if (root.TryGetProperty("LoginDb", out var loginDb))
            {
                if (loginDb.TryGetProperty("Provider", out var providerElement))
                {
                    provider = providerElement.GetString() ?? string.Empty;
                }

                if (loginDb.TryGetProperty("ConnectionString", out var connectionElement))
                {
                    connectionString = connectionElement.GetString() ?? string.Empty;
                }
            }

            if (root.TryGetProperty("SqlServer", out var sqlServer))
            {
                if (sqlServer.TryGetProperty("SaPassword", out var passwordElement))
                {
                    saPassword = passwordElement.GetString() ?? string.Empty;
                }
            }

            return new SecretConfig
            {
                LoginDbProvider = provider,
                LoginDbConnectionString = connectionString,
                SqlServerSaPassword = saPassword,
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Secrets config invalid: {ex.Message}");
            return new SecretConfig();
        }
    }
}
