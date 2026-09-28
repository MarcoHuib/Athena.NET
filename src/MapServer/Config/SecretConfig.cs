using System.Text.Json;

namespace Athena.Net.MapServer.Config;

public sealed class SecretConfig
{
    /// <summary>
    /// Shared secret for MapServer &lt;-&gt; CharServer HMAC-SHA256 service
    /// authentication (see <see cref="Net.ServiceAuthProofCalculator"/> and
    /// ai/map-server.md). Never transmitted over the network - only an HMAC
    /// proof derived from it is. Read from ServiceAuthentication.MapServer.Token
    /// in this file (the same path and file CharServer reads), or the
    /// ATHENA_NET_MAP_SERVER_SERVICE_TOKEN environment variable (checked by
    /// <see cref="Net.MapServerServiceTokenProvider"/>, which takes priority
    /// when both are set).
    /// </summary>
    public string MapServerServiceToken { get; init; } = string.Empty;

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

            var serviceToken = string.Empty;
            if (root.TryGetProperty("ServiceAuthentication", out var serviceAuth) &&
                serviceAuth.TryGetProperty("MapServer", out var mapServerAuth) &&
                mapServerAuth.TryGetProperty("Token", out var tokenElement))
            {
                serviceToken = tokenElement.GetString() ?? string.Empty;
            }

            return new SecretConfig
            {
                MapServerServiceToken = serviceToken,
            };
        }
        catch
        {
            return new SecretConfig();
        }
    }

    // SecretConfig owns exactly MapServerServiceToken - nothing else. MapConfig is a
    // record, so this clones every other property unchanged via `with` instead of
    // re-listing them; adding a new non-secret MapConfig property in the future
    // requires no change here.
    public MapConfig ApplyTo(MapConfig config) => config;
}
