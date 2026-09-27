using System.Text.Json;
using Athena.Net.LoginServer.Logging;

namespace Athena.Net.LoginServer.Config;

public sealed class SecretConfig
{
    public string LoginDbProvider { get; init; } = string.Empty;
    public string LoginDbConnectionString { get; init; } = string.Empty;
    public string SqlServerSaPassword { get; init; } = string.Empty;

    /// <summary>
    /// Shared secret for CharServer &lt;-&gt; LoginServer HMAC-SHA256 service
    /// authentication (see <see cref="Application.ServiceAuthProofCalculator"/> and
    /// ai/login-server.md). Never transmitted over the network - only an
    /// HMAC proof derived from it is. Read from ServiceAuthentication.CharServer.Token
    /// in this file, or the ATHENA_NET_CHAR_SERVER_SERVICE_TOKEN environment
    /// variable (checked by <see cref="Application.CharServerServiceTokenProvider"/>,
    /// which takes priority when both are set).
    /// </summary>
    public string CharServerServiceToken { get; init; } = string.Empty;

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
            var charServerServiceToken = string.Empty;

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

            if (root.TryGetProperty("ServiceAuthentication", out var serviceAuth) &&
                serviceAuth.TryGetProperty("CharServer", out var charServerAuth) &&
                charServerAuth.TryGetProperty("Token", out var tokenElement))
            {
                charServerServiceToken = tokenElement.GetString() ?? string.Empty;
            }

            return new SecretConfig
            {
                LoginDbProvider = provider,
                LoginDbConnectionString = connectionString,
                SqlServerSaPassword = saPassword,
                CharServerServiceToken = charServerServiceToken,
            };
        }
        catch (Exception ex)
        {
            LoginLogger.Warning($"Secrets config invalid: {ex.Message}");
            return new SecretConfig();
        }
    }
}
