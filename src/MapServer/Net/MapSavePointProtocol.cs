using System.Buffers.Binary;
using System.Text;

namespace Athena.Net.MapServer.Net;

internal static class MapSavePointProtocol
{
    public const int RequestLength = 30;
    public const int ResponseLength = 7;

    public static byte[] BuildRequest(uint accountId, uint charId, string mapName, ushort x, ushort y)
    {
        var map = Encoding.ASCII.GetBytes(mapName);
        if (map.Length is 0 or > 11) throw new ArgumentOutOfRangeException(nameof(mapName));
        var packet = new byte[RequestLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapSavePointRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), accountId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(6), charId);
        map.CopyTo(packet.AsSpan(10, PacketConstants.MapNameLength));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(26), x);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(28), y);
        return packet;
    }

    public static bool TryParseResponse(ReadOnlySpan<byte> packet, out uint charId, out bool success)
    {
        charId = 0; success = false;
        if (packet.Length != ResponseLength || BinaryPrimitives.ReadInt16LittleEndian(packet) != PacketConstants.MapSavePointResponse) return false;
        charId = BinaryPrimitives.ReadUInt32LittleEndian(packet[2..]); success = packet[6] == 0; return true;
    }
}
