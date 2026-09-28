using System.Text.Json;
using Athena.Net.CharServer.Logging;

namespace Athena.Net.CharServer.Config;

public sealed class SecretConfig
{
    public string CharDbProvider { get; init; } = string.Empty;
    public string CharDbConnectionString { get; init; } = string.Empty;

    /// <summary>
    /// Shared secret for CharServer &lt;-&gt; LoginServer HMAC-SHA256 service
    /// authentication (see <see cref="Net.ServiceAuthProofCalculator"/> and
    /// ai/login-server.md). Never transmitted over the network - only an
    /// HMAC proof derived from it is. Read from ServiceAuthentication.CharServer.Token
    /// in this file (the same path and file LoginServer reads), or the
    /// ATHENA_NET_CHAR_SERVER_SERVICE_TOKEN environment variable (checked by
    /// <see cref="Net.CharServerServiceTokenProvider"/>, which takes priority
    /// when both are set). Independent of <see cref="MapServerServiceToken"/>,
    /// which authenticates MapServer to this CharServer, not CharServer to
    /// LoginServer.
    /// </summary>
    public string CharServerServiceToken { get; init; } = string.Empty;

    /// <summary>
    /// Shared secret for MapServer &lt;-&gt; CharServer HMAC-SHA256 service
    /// authentication (see <see cref="Net.MapServiceAuthProofCalculator"/>
    /// and ai/char-server.md). Never transmitted over the network - only an
    /// HMAC proof derived from it is. Read from ServiceAuthentication.MapServer.Token
    /// in this file (the same path and file MapServer reads), or the
    /// ATHENA_NET_MAP_SERVER_SERVICE_TOKEN environment variable (checked by
    /// <see cref="Net.MapServerServiceTokenProvider"/>, which takes priority
    /// when both are set). Completely independent of
    /// <see cref="CharServerServiceToken"/> - the two tokens protect two
    /// different trust boundaries and must never be equal in production.
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

            var provider = string.Empty;
            var connectionString = string.Empty;
            var serviceToken = string.Empty;
            var mapServiceToken = string.Empty;

            if (root.TryGetProperty("ServiceAuthentication", out var serviceAuth))
            {
                if (serviceAuth.TryGetProperty("CharServer", out var charServerAuth) &&
                    charServerAuth.TryGetProperty("Token", out var tokenElement))
                {
                    serviceToken = tokenElement.GetString() ?? string.Empty;
                }

                if (serviceAuth.TryGetProperty("MapServer", out var mapServerAuth) &&
                    mapServerAuth.TryGetProperty("Token", out var mapTokenElement))
                {
                    mapServiceToken = mapTokenElement.GetString() ?? string.Empty;
                }
            }

            if (root.TryGetProperty("CharDb", out var charDb))
            {
                if (charDb.TryGetProperty("Provider", out var providerElement))
                {
                    provider = providerElement.GetString() ?? string.Empty;
                }

                if (charDb.TryGetProperty("ConnectionString", out var connectionElement))
                {
                    connectionString = connectionElement.GetString() ?? string.Empty;
                }
            }

            return new SecretConfig
            {
                CharDbProvider = provider,
                CharDbConnectionString = connectionString,
                CharServerServiceToken = serviceToken,
                MapServerServiceToken = mapServiceToken,
            };
        }
        catch (Exception ex)
        {
            CharLogger.Warning($"Secrets config invalid: {ex.Message}");
            return new SecretConfig();
        }
    }

    public CharConfig ApplyTo(CharConfig config)
    {
        return new CharConfig
        {
            IroRenewalCompatibility = config.IroRenewalCompatibility,
            IroAdvertisedMapIp = config.IroAdvertisedMapIp,
            IroAdvertisedMapPort = config.IroAdvertisedMapPort,
            ServiceId = config.ServiceId,
            ServerName = config.ServerName,
            LoginIp = config.LoginIp,
            LoginPort = config.LoginPort,
            BindIp = config.BindIp,
            CharIp = config.CharIp,
            CharPort = config.CharPort,
            CharMaintenance = config.CharMaintenance,
            CharNew = config.CharNew,
            CharNewDisplay = config.CharNewDisplay,
            MinChars = config.MinChars,
            MaxChars = config.MaxChars,
            CharDeleteDelaySeconds = config.CharDeleteDelaySeconds,
            CharDeleteLevel = config.CharDeleteLevel,
            CharDeleteOption = config.CharDeleteOption,
            CharDeleteRestriction = config.CharDeleteRestriction,
            StartZeny = config.StartZeny,
            StartStatusPoints = config.StartStatusPoints,
            StartPoints = config.StartPoints,
            StartPointsDoram = config.StartPointsDoram,
            StartPointsPre = config.StartPointsPre,
            StartItems = config.StartItems,
            StartItemsDoram = config.StartItemsDoram,
            StartItemsPre = config.StartItemsPre,
            UsePreRenewalStartPoints = config.UsePreRenewalStartPoints,
            PincodeEnabled = config.PincodeEnabled,
            PincodeChangeTimeSeconds = config.PincodeChangeTimeSeconds,
            PincodeMaxTry = config.PincodeMaxTry,
            PincodeForce = config.PincodeForce,
            PincodeAllowRepeated = config.PincodeAllowRepeated,
            PincodeAllowSequential = config.PincodeAllowSequential,
            ConsoleEnabled = config.ConsoleEnabled,
            ConsoleMsgLog = config.ConsoleMsgLog,
            ConsoleSilent = config.ConsoleSilent,
            ConsoleLogFilePath = config.ConsoleLogFilePath,
            TimestampFormat = config.TimestampFormat,
        };
    }
}
