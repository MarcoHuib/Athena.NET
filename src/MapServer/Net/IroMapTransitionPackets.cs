using System.Buffers.Binary;
using System.Text;

namespace Athena.Net.MapServer.Net;

public static class IroMapTransitionPackets
{
    public const int SameServerMapChangeLength = 22;

    public static byte[] BuildSameServerMapChange(
        string mapName,
        ushort x,
        ushort y)
    {
        var wireMapName = NormalizeWireMapName(mapName);
        var mapBytes = Encoding.ASCII.GetBytes(wireMapName);
        if (mapBytes.Length > PacketConstants.MapNameLength)
        {
            throw new ArgumentException("The client-facing map name exceeds the 16-byte wire field.", nameof(mapName));
        }

        var packet = new byte[SameServerMapChangeLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcNpcAckMapMove);
        mapBytes.CopyTo(packet.AsSpan(2, PacketConstants.MapNameLength));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(18), x);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(20), y);
        return packet;
    }

    internal static string NormalizeWireMapName(string mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName))
        {
            throw new ArgumentException("A map name is required.", nameof(mapName));
        }

        return mapName.EndsWith(".gat", StringComparison.OrdinalIgnoreCase)
            ? mapName
            : $"{mapName}.gat";
    }
}
