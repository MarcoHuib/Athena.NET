using System.Buffers.Binary;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Net;

// Live-captured via a dedicated stock-iRO base-stat-allocation capture (statsonly.pcapng - see
// ai/iro-2026-wire.md for the full evidence trace): e.g. BB 00 0D 00 01 1E ->
// opcode.W(2) statusId.W(2) amount.B(1) opaqueTrailingByte.B(1) = 6 bytes, the same "+1 opaque
// trailing byte beyond a familiar generic shape" pattern already proven for
// IroSkillLevelUpRequestPacket/0x0112 and other client packets. Six captured requests carry six
// DIFFERENT trailing-byte values, conclusively proving it is not a fixed constant - it is
// preserved opaquely, never validated against a specific value.
//
// This type answers ONLY "is the packet structurally valid, which wire StatusId did the client
// request, and what increase amount did it carry" - it must never query
// GeneratedProgressionRegistry, inspect StatPoints, or decide affordability/cap rules; those are
// CharacterStatService's job. The client's request means "increase this stat by this amount",
// never "set this stat to value N" - there is no target/current-value field on the wire at all.
public readonly record struct IroStatusUpRequestPacket(CharacterBaseStat? Stat, byte Amount, byte OpaqueTrailingByte)
{
    // Wire StatusId -> CharacterBaseStat, per the _sp enum (legacy/rathena/src/map/map.hpp:
    // 500-501) independently confirmed by the capture itself (13..18 observed across the six
    // STR/AGI/VIT/INT/DEX/LUK requests). An explicit small mapping, never a naming heuristic or
    // arithmetic offset trick - an unrecognized StatusId resolves to a null Stat rather than
    // silently aliasing onto a different CharacterBaseStat member, so an unsupported/forged
    // StatusId can never reach CharacterStatService as the wrong stat.
    public static bool TryParse(ReadOnlySpan<byte> packet, out IroStatusUpRequestPacket value)
    {
        value = default;
        if (packet.Length != PacketConstants.IroCzStatusUpLength ||
            BinaryPrimitives.ReadInt16LittleEndian(packet) != PacketConstants.IroCzStatusUp)
        {
            return false;
        }

        var statusId = BinaryPrimitives.ReadUInt16LittleEndian(packet[2..]);
        value = new(ResolveStat(statusId), packet[4], packet[5]);
        return true;
    }

    private static CharacterBaseStat? ResolveStat(ushort statusId) => statusId switch
    {
        IroStatusEffectPackets.SpStr => CharacterBaseStat.Strength,
        IroStatusEffectPackets.SpAgi => CharacterBaseStat.Agility,
        IroStatusEffectPackets.SpVit => CharacterBaseStat.Vitality,
        IroStatusEffectPackets.SpInt => CharacterBaseStat.Intelligence,
        IroStatusEffectPackets.SpDex => CharacterBaseStat.Dexterity,
        IroStatusEffectPackets.SpLuk => CharacterBaseStat.Luck,
        _ => null,
    };

    // Reverse of ResolveStat above, for building the 0x00BC/0x0141 response StatusId field from
    // an already-resolved CharacterBaseStat - kept as the single source for both directions of
    // this mapping so the request parser and response serializers can never silently drift
    // apart on which wire StatusId corresponds to which CharacterBaseStat member.
    internal static ushort WireStatusId(CharacterBaseStat stat) => stat switch
    {
        CharacterBaseStat.Strength => IroStatusEffectPackets.SpStr,
        CharacterBaseStat.Agility => IroStatusEffectPackets.SpAgi,
        CharacterBaseStat.Vitality => IroStatusEffectPackets.SpVit,
        CharacterBaseStat.Intelligence => IroStatusEffectPackets.SpInt,
        CharacterBaseStat.Dexterity => IroStatusEffectPackets.SpDex,
        CharacterBaseStat.Luck => IroStatusEffectPackets.SpLuk,
        _ => throw new ArgumentOutOfRangeException(nameof(stat), stat, "Unknown base stat."),
    };
}
