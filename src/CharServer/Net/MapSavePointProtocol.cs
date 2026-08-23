using System.Buffers.Binary;
using System.Text;

namespace Athena.Net.CharServer.Net;

internal sealed record MapSavePointRequest(uint AccountId, uint CharId, string Map, ushort X, ushort Y);
internal static class MapSavePointProtocol
{
    public const int RequestLength = 30;
    public const int ResponseLength = 7;
    public static bool TryParseRequest(ReadOnlySpan<byte> packet, out MapSavePointRequest request)
    {
        request = default!;
        if (packet.Length != RequestLength || BinaryPrimitives.ReadInt16LittleEndian(packet) != PacketConstants.MapSavePointRequest) return false;
        var map = Encoding.ASCII.GetString(packet.Slice(10, PacketConstants.MapNameLength)).TrimEnd('\0');
        request = new(BinaryPrimitives.ReadUInt32LittleEndian(packet[2..]), BinaryPrimitives.ReadUInt32LittleEndian(packet[6..]), map,
            BinaryPrimitives.ReadUInt16LittleEndian(packet[26..]), BinaryPrimitives.ReadUInt16LittleEndian(packet[28..]));
        return true;
    }
    public static byte[] BuildResponse(uint charId, bool success)
    {
        var packet = new byte[ResponseLength]; BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapSavePointResponse);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), charId); packet[6] = success ? (byte)0 : (byte)1; return packet;
    }
}
