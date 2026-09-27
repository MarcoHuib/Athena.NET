using System.Text;
using Athena.Net.CharServer.Config;

namespace Athena.Net.CharServer.Net;

/// <summary>
/// Resolves the shared ServiceToken used for HMAC-SHA256 service
/// authentication against LoginServer (see <see cref="ServiceAuthProofCalculator"/>).
/// The ATHENA_NET_CHAR_SERVER_SERVICE_TOKEN environment variable - suited to
/// deployment secret sources such as Kubernetes Secrets - overrides
/// <see cref="SecretConfig.CharServerServiceToken"/> (solutionfiles/secrets/secret.json),
/// matching LoginServer's own resolution of the same value
/// (Athena.Net.LoginServer.Application.CharServerServiceTokenProvider). Never
/// logs the token itself.
/// </summary>
public sealed class CharServerServiceTokenProvider
{
    public const string EnvironmentVariableName = "ATHENA_NET_CHAR_SERVER_SERVICE_TOKEN";

    public CharServerServiceTokenProvider(SecretConfig secrets)
    {
        var raw = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = secrets.CharServerServiceToken;
        }

        IsConfigured = !string.IsNullOrWhiteSpace(raw);
        TokenBytes = IsConfigured ? Encoding.UTF8.GetBytes(raw!) : null;
    }

    /// <summary>
    /// False when no token is configured anywhere. CharServer cannot
    /// authenticate to LoginServer in that case - never treat "no token
    /// configured" as "authentication disabled".
    /// </summary>
    public bool IsConfigured { get; }

    public byte[]? TokenBytes { get; }
}
