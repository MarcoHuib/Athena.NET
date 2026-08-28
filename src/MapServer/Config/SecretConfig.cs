using System.Text.Json;

namespace Athena.Net.MapServer.Config;

public sealed class SecretConfig
{
    public string UserId { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;

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

            if (root.TryGetProperty("MapServer", out var mapServer))
            {
                return new SecretConfig
                {
                    UserId = GetString(mapServer, "UserId"),
                    Password = GetString(mapServer, "Password"),
                };
            }

            if (root.TryGetProperty("CharServer", out var charServer))
            {
                return new SecretConfig
                {
                    UserId = GetString(charServer, "UserId"),
                    Password = GetString(charServer, "Password"),
                };
            }
        }
        catch
        {
            return new SecretConfig();
        }

        return new SecretConfig();
    }

    // SecretConfig owns exactly UserId/Password - nothing else. MapConfig is a
    // record, so this clones every other property unchanged via `with` instead of
    // re-listing them; adding a new non-secret MapConfig property in the future
    // requires no change here.
    public MapConfig ApplyTo(MapConfig config) => config with
    {
        UserId = string.IsNullOrWhiteSpace(UserId) ? config.UserId : UserId,
        Password = string.IsNullOrWhiteSpace(Password) ? config.Password : Password,
    };

    private static string GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return string.Empty;
        }

        return value.GetString() ?? string.Empty;
    }
}
