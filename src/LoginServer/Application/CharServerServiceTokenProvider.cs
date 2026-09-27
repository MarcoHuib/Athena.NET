using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Logging;

namespace Athena.Net.LoginServer.Application;

/// <summary>
/// Resolves the shared CharServer ServiceToken used for HMAC-SHA256 service
/// authentication (see <see cref="ServiceAuthProofCalculator"/>). The
/// ATHENA_NET_CHAR_SERVER_SERVICE_TOKEN environment variable - suited to
/// deployment secret sources such as Kubernetes Secrets - overrides
/// <see cref="SecretConfig.CharServerServiceToken"/> (solutionfiles/secrets/secret.json),
/// consistent with how other Athena.NET secrets (e.g. the LoginDb connection
/// string) are resolved.
/// <para>
/// The token must be Base64-encoded and decode to at least
/// <see cref="MinimumTokenLengthBytes"/> (256 bits). A missing value, invalid
/// Base64, or a decoded length below the minimum are all treated identically
/// as "not configured" - service authentication fails closed rather than
/// silently accepting a weak secret such as "password" or "test". Never
/// logs the raw configured value or the decoded bytes, only that
/// configuration was missing or invalid.
/// </para>
/// </summary>
public sealed class CharServerServiceTokenProvider
{
    public const string EnvironmentVariableName = "ATHENA_NET_CHAR_SERVER_SERVICE_TOKEN";

    /// <summary>Minimum acceptable decoded token length: 256 bits.</summary>
    public const int MinimumTokenLengthBytes = 32;

    private readonly byte[]? _tokenBytes;

    public CharServerServiceTokenProvider(SecretConfig secrets)
    {
        var raw = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        var source = "the ATHENA_NET_CHAR_SERVER_SERVICE_TOKEN environment variable";
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = secrets.CharServerServiceToken;
            source = "ServiceAuthentication.CharServer.Token in the secret configuration file";
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            IsConfigured = false;
            _tokenBytes = null;
            return;
        }

        if (!TryDecode(raw, out var decoded))
        {
            LoginLogger.Warning(
                $"CharServer ServiceToken from {source} is invalid or too weak (must be Base64-encoded and decode " +
                $"to at least {MinimumTokenLengthBytes} bytes/256 bits). Service authentication will fail closed.");
            IsConfigured = false;
            _tokenBytes = null;
            return;
        }

        IsConfigured = true;
        _tokenBytes = decoded;
    }

    /// <summary>
    /// False when no valid token is configured anywhere - missing, not valid
    /// Base64, or decoding to fewer than <see cref="MinimumTokenLengthBytes"/>
    /// bytes are all "not configured". Service authentication must fail
    /// closed in that case - never treat it as "authentication disabled".
    /// </summary>
    public bool IsConfigured { get; }

    internal byte[]? TokenBytes => _tokenBytes;

    private static bool TryDecode(string value, out byte[] decoded)
    {
        try
        {
            decoded = Convert.FromBase64String(value.Trim());
        }
        catch (FormatException)
        {
            decoded = Array.Empty<byte>();
            return false;
        }

        return decoded.Length >= MinimumTokenLengthBytes;
    }
}
