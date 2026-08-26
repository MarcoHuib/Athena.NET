using System.Buffers.Binary;
namespace Athena.Net.CharServer.Net;
internal static class MapInventoryListProtocol
{
    internal const int GetRequestLength = 10;
    internal const int ResponseHeaderLength = 11;
    internal const int ItemLength = 20;

    internal static bool TryParseGet(ReadOnlySpan<byte> p, out uint a, out uint c)
    {
        a = 0; c = 0;
        if (p.Length != GetRequestLength || BinaryPrimitives.ReadInt16LittleEndian(p) != PacketConstants.MapInventoryListGetRequest) return false;
        a = BinaryPrimitives.ReadUInt32LittleEndian(p[2..]);
        c = BinaryPrimitives.ReadUInt32LittleEndian(p[6..]);
        return true;
    }

    // Row order in `rows` IS the stable server-side slot order (see
    // MapServerSession.HandleInventoryListGetAsync's OrderBy(i => i.Id)) - SlotIndex is each
    // row's position in the list, written here rather than stored on the DTO.
    internal static byte[] BuildResponse(byte result, uint charId, IReadOnlyList<CharacterInventoryRowDto>? rows)
    {
        var itemCount = rows?.Count ?? 0;
        var length = ResponseHeaderLength + itemCount * ItemLength;
        var packet = new byte[length];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapInventoryListGetResponse);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)length);
        packet[4] = result;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(5), charId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(9), (ushort)itemCount);
        if (rows is not null)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var span = packet.AsSpan(ResponseHeaderLength + i * ItemLength, ItemLength);
                var row = rows[i];
                BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)i);
                BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)row.ItemId);
                BinaryPrimitives.WriteUInt32LittleEndian(span[8..], row.Amount);
                BinaryPrimitives.WriteUInt32LittleEndian(span[12..], row.Equip);
                span[16] = row.Identified ? (byte)1 : (byte)0;
                span[17] = row.Refine;
                span[18] = row.Favorite;
                span[19] = row.Bound;
            }
        }
        return packet;
    }
}
