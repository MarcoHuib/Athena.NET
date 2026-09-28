using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Logging;

namespace Athena.Net.MapServer.Net;

/// <summary>
/// Resolves the shared ServiceToken used for HMAC-SHA256 service
/// authentication against CharServer (see <see cref="ServiceAuthProofCalculator"/>).
/// Structurally identical to CharServer's own
/// <c>Athena.Net.CharServer.Net.CharServerServiceTokenProvider</c> (used for
/// CharServer &lt;-&gt; LoginServer authentication) but reads a completely
/// independent secret: MapServer &lt;-&gt; CharServer authentication never shares
/// a token value with CharServer &lt;-&gt; LoginServer authentication.
/// <para>
/// The ATHENA_NET_MAP_SERVER_SERVICE_TOKEN environment variable - suited to
/// deployment secret sources such as Kubernetes Secrets - overrides
/// <see cref="SecretConfig.MapServerServiceToken"/> (solutionfiles/secrets/secret.json).
/// </para>
/// <para>
/// The token must be Base64-encoded and decode to at least
/// <see cref="MinimumTokenLengthBytes"/> (256 bits). A missing value,
/// invalid Base64, or a decoded length below the minimum are all treated
/// identically as "not configured". Never logs the raw configured value or
/// the decoded bytes, only that configuration was missing or invalid.
/// </para>
/// </summary>
public sealed class MapServerServiceTokenProvider
{
    public const string EnvironmentVariableName = "ATHENA_NET_MAP_SERVER_SERVICE_TOKEN";

    /// <summary>Minimum acceptable decoded token length: 256 bits.</summary>
    public const int MinimumTokenLengthBytes = 32;

    public MapServerServiceTokenProvider(SecretConfig secrets)
    {
        var raw = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        var source = "the ATHENA_NET_MAP_SERVER_SERVICE_TOKEN environment variable";
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = secrets.MapServerServiceToken;
            source = "ServiceAuthentication.MapServer.Token in the secret configuration file";
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            IsConfigured = false;
            TokenBytes = null;
            return;
        }

        if (!TryDecode(raw, out var decoded))
        {
            MapLogger.Warning(
                $"MapServer ServiceToken from {source} is invalid or too weak (must be Base64-encoded and decode " +
                $"to at least {MinimumTokenLengthBytes} bytes/256 bits). MapServer will be unable to authenticate to the char server.");
            IsConfigured = false;
            TokenBytes = null;
            return;
        }

        IsConfigured = true;
        TokenBytes = decoded;
    }

    /// <summary>
    /// False when no valid token is configured anywhere - missing, not valid
    /// Base64, or decoding to fewer than <see cref="MinimumTokenLengthBytes"/>
    /// bytes are all "not configured". MapServer cannot authenticate to
    /// CharServer in that case - never treat it as "authentication disabled".
    /// </summary>
    public bool IsConfigured { get; }

    public byte[]? TokenBytes { get; }

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
