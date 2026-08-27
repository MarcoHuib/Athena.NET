using System.Buffers.Binary;

namespace Athena.Net.MapServer.Net;

internal static class IroUseItemPackets
{
    // ZC_USE_ITEM_ACK2 (0x01C8, packets_struct.hpp:2577-2589, PACKETVER_MAIN_NUM >= 20181121 /
    // PACKETVER_RE_NUM >= 20180704 gate, pinned build satisfies this): packetType.W index.W
    // itemId.L accountId.L amount.W result.B = 15 bytes. Not yet capture-verified byte-for-byte
    // (only the request side has a live capture so far - see ai/map-server.md); this is the
    // pinned-source layout for the current PACKETVER branch, following this project's evidence
    // priority (capture > runtime > pinned source) until a response capture exists.
    //
    // Pinned clif_useitemack (clif.cpp:4468-4497): index is client_index() (server SlotIndex+2);
    // itemId is client_nameid() (the item's ClientViewId, matching every other item-bearing
    // packet's identity convention, never the weapon/type enum); amount is the row's amount
    // AFTER this use (0 on failure); result=false sends to SELF only, result=true sends to AREA
    // (other nearby players see the use-item animation/feedback too) - see BuildUseItemAck's
    // caller for the SELF-vs-AREA distinction; this builder only constructs the bytes.
    internal static byte[] BuildUseItemAck(ushort clientIndex, int clientViewItemId, uint accountId, uint amountAfterUse, bool success)
    {
        var packet = new byte[PacketConstants.ZcUseItemAckLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcUseItemAck);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), clientIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)clientViewItemId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), accountId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(12), (ushort)amountAfterUse);
        packet[14] = success ? (byte)1 : (byte)0;
        return packet;
    }
}
