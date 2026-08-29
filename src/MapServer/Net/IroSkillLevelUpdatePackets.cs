using System.Buffers.Binary;

namespace Athena.Net.MapServer.Net;

// 0x0B33 - verified stock-iRO skill-up response (ai/iro-2026-wire.md, capture frame 3623):
// id.W(2) + one 15-byte SKILLDATA entry, byte-for-byte the SAME layout as one ZcSkillInfoList
// (0x0B32) row minus that packet's own 2-byte totalLength header field. A single-skill
// incremental update, not a full-list resend (task section 21).
//
// Pure serializer over an already-resolved IroSkillInfoEntry, per task section 23 - never
// queries GeneratedSkillRegistry, GeneratedSkillTreeRegistry, the database, or any
// session/service. The caller is responsible for projecting the POST-COMMIT skill state through
// IroSkillInfoEntry.From (mirroring IroSkillInfoListPackets' own bootstrap-projection split)
// BEFORE calling this - never from the pre-mutation snapshot (task section 25).
internal static class IroSkillLevelUpdatePackets
{
    internal static byte[] Build(IroSkillInfoEntry entry)
    {
        var packet = new byte[PacketConstants.ZcSkillLevelUpdateLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcSkillLevelUpdate);
        var span = packet.AsSpan(2);
        BinaryPrimitives.WriteUInt16LittleEndian(span, entry.SkillId);
        BinaryPrimitives.WriteInt32LittleEndian(span[2..], entry.Inf);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], entry.CurrentLevel);
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], entry.SpCost);
        // entry.Range is always non-negative by the time it reaches this pure serializer, same as
        // IroSkillInfoListPackets.Build - written via the signed primitive purely to match the
        // field's declared C# type.
        BinaryPrimitives.WriteInt16LittleEndian(span[10..], entry.Range);
        span[12] = entry.Upgradable ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(span[13..], entry.SecondaryLevel);
        return packet;
    }
}
