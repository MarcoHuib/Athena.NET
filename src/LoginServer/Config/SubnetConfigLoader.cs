using System.Net;

namespace Rathena.LoginServer.Config;

public static class SubnetConfigLoader
{
    public static SubnetConfig Load(string path)
    {
        var config = new SubnetConfig();
        if (!File.Exists(path))
        {
            Console.WriteLine($"Subnet config not found: {path}. Using defaults.");
            return config;
        }

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (!line.StartsWith("subnet", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var rest = line[(separator + 1)..].Trim();
            var parts = rest.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 3)
            {
                continue;
            }

            if (!TryParseIpv4(parts[0], out var mask))
            {
                continue;
            }

            if (!TryParseIpv4(parts[1], out var charIp))
            {
                continue;
            }

            if (!TryParseIpv4(parts[2], out var mapIp))
            {
                continue;
            }

            config.Add(new SubnetEntry(mask, charIp, mapIp));
        }

        return config;
    }

    private static string StripComment(string line)
    {
        var index = line.IndexOf("//", StringComparison.Ordinal);
        return index < 0 ? line : line[..index];
    }

    private static bool TryParseIpv4(string ip, out uint value)
    {
        value = 0;
        if (!IPAddress.TryParse(ip, out var address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return false;
        }

        value = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
        return true;
    }
}
