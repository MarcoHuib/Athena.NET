using System.Buffers.Binary;

namespace Athena.Net.MapServer.Net;

// Live-captured via a dedicated stock-iRO skill-up capture (iro-skill-up-nv-basic-0-to-1.pcapng,
// frame 3604 - see ai/iro-2026-wire.md for the full evidence trace): 12 01 01 00 1D ->
// opcode.W(2) skillId.W(2) opaqueTrailingByte.B(1) = 5 bytes, matching the same "+1 opaque
// trailing byte" pattern already proven for attack/equip/unequip/movement/NPC/item-use client
// packets. Only one capture exists so far, so the semantics of the trailing byte are NOT proven -
// it is preserved opaquely (task section 46) rather than guessed at or discarded.
//
// This type answers ONLY "is the packet structurally valid, and what SkillId did the client
// request" - it must never query GeneratedSkillTreeRegistry, inspect SkillPoints, or decide
// learnability; those are CharacterSkillService's job (see task sections 14/15). In particular,
// the client's request means "I want to upgrade SkillId X", never "set SkillId X to level N" -
// this packet carries no target/current level field at all, so there is nothing here to trust or
// distrust on that front.
public readonly record struct IroSkillLevelUpRequestPacket(ushort SkillId, byte OpaqueTrailingByte)
{
    public static bool TryParse(ReadOnlySpan<byte> packet, out IroSkillLevelUpRequestPacket value)
    {
        value = default;
        if (packet.Length != PacketConstants.IroCzSkillLevelUpLength ||
            BinaryPrimitives.ReadInt16LittleEndian(packet) != PacketConstants.IroCzSkillLevelUp)
        {
            return false;
        }

        value = new(
            BinaryPrimitives.ReadUInt16LittleEndian(packet[2..]),
            packet[4]);
        return true;
    }
}
