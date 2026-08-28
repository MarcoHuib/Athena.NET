using System.Buffers.Binary;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Net;

// Variable-length: opcode.W length.W result.B charId.L skillCount.W [row x skillCount].
// Framed via CharServerConnector.VariableLengthMinLength - `length` is the TOTAL packet length,
// matching pinned rAthena's own variable-length packet convention (same shape as
// MapInventoryListProtocol).
internal static class MapSkillListProtocol
{
    internal const int GetRequestLength = 10;
    internal const int ResponseHeaderLength = 11;
    // skillId.W(2) level.B(1) = 3. CharServer's rows carry no runtime concept at all - a missing
    // row already means level 0 (CharacterSkillSnapshot never persists a level-0 row).
    internal const int SkillLength = 3;

    private static void WriteRow(Span<byte> span, ushort skillId, byte level)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(span, skillId);
        span[2] = level;
    }

    private static (ushort SkillId, byte Level) ReadRow(ReadOnlySpan<byte> span) => (
        SkillId: BinaryPrimitives.ReadUInt16LittleEndian(span),
        Level: span[2]);

    internal static byte[] BuildGetRequest(uint accountId, uint characterId)
    {
        var packet = new byte[GetRequestLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapSkillListGetRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), accountId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(6), characterId);
        return packet;
    }

    // Returns false (malformed/truncated packet) if the declared length doesn't match the actual
    // packet size, or skillCount doesn't agree with the payload length actually present. Rows may
    // not declare a duplicate SkillId - CharSkill's composite primary key makes a real duplicate
    // impossible from a correct load - treated as a data/protocol invariant violation, not a case
    // to silently resolve.
    internal static bool TryParseResponse(byte[] packet, out byte result, out uint charId, out CharacterSkillReadResult skills)
    {
        result = 1;
        charId = 0;
        skills = CharacterSkillReadResult.Failed();
        if (packet.Length < ResponseHeaderLength) return false;

        var declaredLength = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2));
        if (declaredLength != packet.Length) return false;

        result = packet[4];
        charId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(5));
        if (result != 0) return true;

        var skillCount = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(9));
        var expectedLength = ResponseHeaderLength + skillCount * SkillLength;
        if (expectedLength != packet.Length) return false;

        var rows = new (ushort SkillId, byte Level)[skillCount];
        var seenIds = new HashSet<ushort>();
        for (var i = 0; i < skillCount; i++)
        {
            var row = ReadRow(packet.AsSpan(ResponseHeaderLength + i * SkillLength, SkillLength));
            if (!seenIds.Add(row.SkillId)) return false;
            rows[i] = row;
        }

        skills = CharacterSkillReadResult.Success(CharacterSkillSnapshot.FromLogin(rows));
        return true;
    }

    // Test-only: MapServer never sends this response in production (CharServer does - see
    // Athena.Net.CharServer.Net.MapSkillListProtocol.BuildResponse), but mirroring the write logic
    // here lets tests build fixtures without duplicating byte-packing across two protocol files.
    internal static byte[] BuildResponse(byte result, uint charId, CharacterSkillSnapshot? skills)
    {
        var skillCount = skills?.Learned.Count ?? 0;
        var length = ResponseHeaderLength + skillCount * SkillLength;
        var packet = new byte[length];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapSkillListGetResponse);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)length);
        packet[4] = result;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(5), charId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(9), (ushort)skillCount);
        if (skills is not null)
        {
            for (var i = 0; i < skills.Learned.Count; i++)
            {
                var row = skills.Learned[i];
                WriteRow(packet.AsSpan(ResponseHeaderLength + i * SkillLength, SkillLength), row.SkillId, row.Level);
            }
        }
        return packet;
    }
}
