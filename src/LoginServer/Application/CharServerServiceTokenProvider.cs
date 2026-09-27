using System.Text;
using Athena.Net.LoginServer.Config;

namespace Athena.Net.LoginServer.Application;

/// <summary>
/// Resolves the shared CharServer ServiceToken used for HMAC-SHA256 service
/// authentication (see <see cref="ServiceAuthProofCalculator"/>). The
/// ATHENA_NET_CHAR_SERVER_SERVICE_TOKEN environment variable - suited to
/// deployment secret sources such as Kubernetes Secrets - overrides
/// <see cref="SecretConfig.CharServerServiceToken"/> (solutionfiles/secrets/secret.json),
/// consistent with how other Athena.NET secrets (e.g. the LoginDb connection
/// string) are resolved. Never logs the token itself.
/// </summary>
public sealed class CharServerServiceTokenProvider
{
    public const string EnvironmentVariableName = "ATHENA_NET_CHAR_SERVER_SERVICE_TOKEN";

    private readonly byte[]? _tokenBytes;

    public CharServerServiceTokenProvider(SecretConfig secrets)
    {
        var raw = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = secrets.CharServerServiceToken;
        }

        IsConfigured = !string.IsNullOrWhiteSpace(raw);
        _tokenBytes = IsConfigured ? Encoding.UTF8.GetBytes(raw!) : null;
    }

    /// <summary>
    /// False when no token is configured anywhere. Service authentication must
    /// fail closed in that case - never treat "no token configured" as
    /// "authentication disabled".
    /// </summary>
    public bool IsConfigured { get; }

    internal byte[]? TokenBytes => _tokenBytes;
}
