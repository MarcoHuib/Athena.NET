using System.Buffers.Binary;

namespace Athena.Net.MapServer.Net;

internal static class IroMapEnterPackets
{
    internal const short ZcInventoryExpansionSize = 0x0b18;
    internal const short ZcAccountId = 0x0283;
    internal const short ZcOverweightPercent = 0x0ade;
    internal const uint DefaultOverweightPercent = 70;

    internal static byte[] BuildInitialBootstrap(
        MapAuthOkData authOk,
        uint tick,
        short inventoryExpansionSize = 0,
        uint overweightPercent = DefaultOverweightPercent)
    {
        return
        [
            .. BuildInventoryExpansionSize(inventoryExpansionSize),
            .. BuildAccountId(authOk.AccountId),
            .. BuildOverweightPercent(overweightPercent),
            .. BuildAcceptEnter(authOk, tick),
        ];
    }

    internal static byte[] BuildInventoryExpansionSize(short expansionSize)
    {
        var packet = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(packet, ZcInventoryExpansionSize);
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(2), expansionSize);
        return packet;
    }

    internal static byte[] BuildAccountId(uint accountId)
    {
        var packet = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(packet, ZcAccountId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), accountId);
        return packet;
    }

    internal static byte[] BuildOverweightPercent(uint percent)
    {
        var packet = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(packet, ZcOverweightPercent);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), percent);
        return packet;
    }

    internal static byte[] BuildAcceptEnter(MapAuthOkData authOk, uint tick)
    {
        var packet = new byte[13];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcAcceptEnter);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), tick);
        WritePackedPosition(packet.AsSpan(6, 3), authOk.X, authOk.Y, authOk.Direction);
        packet[9] = 5;
        packet[10] = 5;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(11), authOk.Font);
        return packet;
    }

    private static void WritePackedPosition(Span<byte> buffer, ushort x, ushort y, byte direction)
    {
        buffer[0] = (byte)(x >> 2);
        buffer[1] = (byte)((x << 6) | ((y >> 4) & 0x3f));
        buffer[2] = (byte)((y << 4) | (direction & 0x0f));
    }
}
