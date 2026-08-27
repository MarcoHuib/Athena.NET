using System.Buffers.Binary;

namespace Athena.Net.MapServer.Net;

// Live-captured via targeted diagnostic instrumentation (see ai/map-server.md "Item-use
// request"): A7 00 04 00 80 84 1E 00 D2 -> opcode.W clientIndex.W accountId.L opaqueByte.B.
// clientIndex is the CLIENT-facing inventory index (client_index() = server SlotIndex + 2,
// clif.cpp:122-124) - callers must apply server_index() (clientIndex - 2, clif.cpp:127-129)
// before treating it as an authoritative CharacterInventorySnapshot.SlotIndex, exactly like
// every other equip/unequip/pickup path already does. AccountId must be validated against the
// authenticated session's own account id (pinned clif_parse_UseItem reads only the index off
// the wire and resolves everything else server-side; the account id field is carried but the
// client never gets to assert WHICH account's item is used - only that this request belongs to
// its own already-authenticated connection). Matches pinned generic CZ_USE_ITEM/CZ_USE_ITEM2
// (clif.cpp:12077-12078, 8 bytes: index.W accountId.L) plus the one opaque trailing byte
// pattern already proven for attack/equip/unequip/movement/NPC packets - its semantics are not
// assigned.
public readonly record struct IroUseItemRequestPacket(ushort ClientIndex, uint AccountId, byte OpaqueTrailingByte)
{
    public static bool TryParse(ReadOnlySpan<byte> packet, out IroUseItemRequestPacket value)
    {
        value = default;
        if (packet.Length != PacketConstants.IroCzUseItemLength ||
            BinaryPrimitives.ReadInt16LittleEndian(packet) != PacketConstants.IroCzUseItem)
        {
            return false;
        }

        value = new(
            BinaryPrimitives.ReadUInt16LittleEndian(packet[2..]),
            BinaryPrimitives.ReadUInt32LittleEndian(packet[4..]),
            packet[8]);
        return true;
    }
}
