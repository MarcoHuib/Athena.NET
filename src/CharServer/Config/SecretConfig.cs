using System.Text.Json;
using Athena.Net.CharServer.Logging;

namespace Athena.Net.CharServer.Config;

public sealed class SecretConfig
{
    public string CharServerUserId { get; init; } = string.Empty;
    public string CharServerPassword { get; init; } = string.Empty;
    public string CharDbProvider { get; init; } = string.Empty;
    public string CharDbConnectionString { get; init; } = string.Empty;

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

            var userId = string.Empty;
            var password = string.Empty;
            var provider = string.Empty;
            var connectionString = string.Empty;

            if (root.TryGetProperty("CharServer", out var charServer))
            {
                if (charServer.TryGetProperty("UserId", out var userIdElement))
                {
                    userId = userIdElement.GetString() ?? string.Empty;
                }

                if (charServer.TryGetProperty("Password", out var passwordElement))
                {
                    password = passwordElement.GetString() ?? string.Empty;
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
                CharServerUserId = userId,
                CharServerPassword = password,
                CharDbProvider = provider,
                CharDbConnectionString = connectionString,
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
            UserId = string.IsNullOrWhiteSpace(CharServerUserId) ? config.UserId : CharServerUserId,
            Password = string.IsNullOrWhiteSpace(CharServerPassword) ? config.Password : CharServerPassword,
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
            ConsoleEnabled = config.ConsoleEnabled,
            ConsoleMsgLog = config.ConsoleMsgLog,
            ConsoleSilent = config.ConsoleSilent,
            ConsoleLogFilePath = config.ConsoleLogFilePath,
            TimestampFormat = config.TimestampFormat,
        };
    }
}
