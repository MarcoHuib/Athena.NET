using System.Buffers.Binary;

namespace Athena.Net.MapServer.Net;

// 0x0B32 (ZC_SKILLINFO_LIST3) - verified stock-iRO capture layout (ai/iro-2026-wire.md,
// ai/map-server.md): 4-byte header (id.W totalLength.W) plus one 15-byte SKILLDATA entry per
// visible skill: SkillId.W(0) inf.L(2) currentLevel.W(6) spCost.W(8) range.W(10)
// upgradable.B(12) secondaryLevel.W(13). `inf` matches pinned SKILLDATA's `int inf` field width
// (4 bytes) even though its value range fits in a much smaller bitmask - see
// GeneratedSkillDefinition.Inf's own doc comment for the value semantics.
//
// Pure serializer over already-resolved IroSkillInfoEntry values, per task section 22 - never
// queries GeneratedSkillRegistry, GeneratedSkillTreeRegistry, the database, or any
// session/service. Callers (see MapClientSession's bootstrap wiring) are responsible for
// filtering CharacterSkillService.CalculateEffectiveState's result to ClientVisible entries and
// projecting each through IroSkillInfoEntry.From before calling this.
internal static class IroSkillInfoListPackets
{
    internal const int HeaderLength = 4;
    internal const int EntryLength = 15;

    internal static byte[] Build(IReadOnlyList<IroSkillInfoEntry> entries)
    {
        var totalLength = HeaderLength + entries.Count * EntryLength;
        var packet = new byte[totalLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcSkillInfoList);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)totalLength);
        for (var i = 0; i < entries.Count; i++)
        {
            var span = packet.AsSpan(HeaderLength + i * EntryLength, EntryLength);
            var entry = entries[i];
            BinaryPrimitives.WriteUInt16LittleEndian(span, entry.SkillId);
            BinaryPrimitives.WriteInt32LittleEndian(span[2..], entry.Inf);
            BinaryPrimitives.WriteUInt16LittleEndian(span[6..], entry.CurrentLevel);
            BinaryPrimitives.WriteUInt16LittleEndian(span[8..], entry.SpCost);
            // entry.Range is always non-negative by the time it reaches this pure serializer
            // (IroSkillRangeResolver/IroWireCompatibility already resolved it) - written via the
            // signed primitive purely to match the field's declared C# type; the bit pattern is
            // identical to an unsigned write for any non-negative value.
            BinaryPrimitives.WriteInt16LittleEndian(span[10..], entry.Range);
            span[12] = entry.Upgradable ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt16LittleEndian(span[13..], entry.SecondaryLevel);
        }
        return packet;
    }
}
