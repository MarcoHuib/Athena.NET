using System.Net;

namespace Rathena.LoginServer.Config;

public sealed class SubnetConfig
{
    private readonly List<SubnetEntry> _entries = new();

    public IReadOnlyList<SubnetEntry> Entries => _entries;

    public void Add(SubnetEntry entry)
    {
        _entries.Add(entry);
    }

    public bool TryGetCharIp(IPAddress clientIp, out IPAddress charIp)
    {
        charIp = IPAddress.Any;

        if (clientIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        var clientBytes = clientIp.GetAddressBytes();
        var client = ToUInt32(clientBytes);

        foreach (var entry in _entries)
        {
            if ((entry.CharIp & entry.Mask) == (client & entry.Mask))
            {
                charIp = new IPAddress(FromUInt32(entry.CharIp));
                return true;
            }
        }

        return false;
    }

    private static uint ToUInt32(byte[] bytes)
    {
        return (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
    }

    private static byte[] FromUInt32(uint value)
    {
        return new[]
        {
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value,
        };
    }
}

public readonly record struct SubnetEntry(uint Mask, uint CharIp, uint MapIp);
