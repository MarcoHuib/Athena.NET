using System.Buffers.Binary;
namespace Athena.Net.CharServer.Net;
internal static class MapSkillListProtocol
{
    internal const int GetRequestLength = 10;
    internal const int ResponseHeaderLength = 11;
    // skillId.W(2) level.B(1) = 3.
    internal const int SkillLength = 3;

    internal static bool TryParseGet(ReadOnlySpan<byte> p, out uint a, out uint c)
    {
        a = 0; c = 0;
        if (p.Length != GetRequestLength || BinaryPrimitives.ReadInt16LittleEndian(p) != PacketConstants.MapSkillListGetRequest) return false;
        a = BinaryPrimitives.ReadUInt32LittleEndian(p[2..]);
        c = BinaryPrimitives.ReadUInt32LittleEndian(p[6..]);
        return true;
    }

    internal static byte[] BuildResponse(byte result, uint charId, IReadOnlyList<CharacterSkillRowDto>? rows)
    {
        var skillCount = rows?.Count ?? 0;
        var length = ResponseHeaderLength + skillCount * SkillLength;
        var packet = new byte[length];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapSkillListGetResponse);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)length);
        packet[4] = result;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(5), charId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(9), (ushort)skillCount);
        if (rows is not null)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var span = packet.AsSpan(ResponseHeaderLength + i * SkillLength, SkillLength);
                var row = rows[i];
                BinaryPrimitives.WriteUInt16LittleEndian(span, row.SkillId);
                span[2] = row.Level;
            }
        }
        return packet;
    }
}
