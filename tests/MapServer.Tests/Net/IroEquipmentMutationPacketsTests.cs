using System.Buffers.Binary;
using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.Tests.Net;

// Layouts traced from pinned PACKET_CZ_REQ_WEAR_EQUIP (packets.hpp:1502-1509),
// PACKET_ZC_REQ_WEAR_EQUIP_ACK (packets_struct.hpp:1268-1274), CZ_REQ_TAKEOFF_EQUIP
// (clif_packetdb.hpp:59), PACKET_ZC_REQ_TAKEOFF_EQUIP_ACK (packets.hpp:1006-1013) - all gated
// to the pinned PACKETVER 20220406 branch.
//
// VERIFIED STOCK-iRO WIRE DIVERGENCE: the request packets below are one byte LONGER than the
// pinned rAthena shapes (0x0998: 9 bytes not 8; 0x00AB: 5 bytes not 4). Confirmed via stock
// iRO capture, map flow 192.168.178.55 -> 128.241.92.42:4506:
//   Frame 388 (equip):   98 09 02 00 02 00 00 00 5B
//   Frame 449 (equip):   98 09 03 00 10 00 00 00 88
//   Frame 370 (unequip): AB 00 02 00 4F
//   Frame 395 (unequip): AB 00 03 00 85
// Parsing these as the pinned 8/4-byte shapes leaves one real payload byte unconsumed, which
// then corrupts the next packet's 2-byte opcode header (observed live as fake opcodes 0xAB98,
// 0x6025). The trailing byte's semantics are unverified and intentionally left opaque.
public sealed class IroEquipmentMutationPacketsTests
{
    [Fact]
    public void IroEquipRequestPacket_TryParse_CapturedFrame388_ReadsAllFields()
    {
        var packet = Convert.FromHexString("98090200020000005B");

        Assert.True(IroEquipRequestPacket.TryParse(packet, out var request));
        Assert.Equal((ushort)2, request.ClientIndex);
        Assert.Equal(0x000002u, request.Position); // EQP_HAND_R
        Assert.Equal((byte)0x5B, request.OpaqueTrailingByte);
    }

    [Fact]
    public void IroEquipRequestPacket_TryParse_CapturedFrame449_ReadsAllFields()
    {
        var packet = Convert.FromHexString("980903001000000088");

        Assert.True(IroEquipRequestPacket.TryParse(packet, out var request));
        Assert.Equal((ushort)3, request.ClientIndex);
        Assert.Equal(0x000010u, request.Position); // EQP_ARMOR
        Assert.Equal((byte)0x88, request.OpaqueTrailingByte);
    }

    [Fact]
    public void IroEquipRequestPacket_TryParse_WrongOpcodeOrLength_ReturnsFalse()
    {
        var wrongOpcode = new byte[9];
        BinaryPrimitives.WriteInt16LittleEndian(wrongOpcode, 0x0999);
        Assert.False(IroEquipRequestPacket.TryParse(wrongOpcode, out _));

        // The old (incorrect) pinned-shape 8-byte length must no longer be accepted.
        Assert.False(IroEquipRequestPacket.TryParse(new byte[8], out _));
    }

    [Fact]
    public void IroUnequipRequestPacket_TryParse_CapturedFrame370_ReadsAllFields()
    {
        var packet = Convert.FromHexString("AB0002004F");

        Assert.True(IroUnequipRequestPacket.TryParse(packet, out var request));
        Assert.Equal((ushort)2, request.ClientIndex);
        Assert.Equal((byte)0x4F, request.OpaqueTrailingByte);
    }

    [Fact]
    public void IroUnequipRequestPacket_TryParse_CapturedFrame395_ReadsAllFields()
    {
        var packet = Convert.FromHexString("AB00030085");

        Assert.True(IroUnequipRequestPacket.TryParse(packet, out var request));
        Assert.Equal((ushort)3, request.ClientIndex);
        Assert.Equal((byte)0x85, request.OpaqueTrailingByte);
    }

    [Fact]
    public void IroUnequipRequestPacket_TryParse_WrongOpcodeOrLength_ReturnsFalse()
    {
        var wrongOpcode = new byte[5];
        BinaryPrimitives.WriteInt16LittleEndian(wrongOpcode, 0x00ac);
        Assert.False(IroUnequipRequestPacket.TryParse(wrongOpcode, out _));

        // The old (incorrect) pinned-shape 4-byte length must no longer be accepted.
        Assert.False(IroUnequipRequestPacket.TryParse(new byte[4], out _));
    }

    [Fact]
    public void ConcatenatedStream_EquipThenUnequip_ParsesAsExactlyTwoPacketsWithNoResidue()
    {
        // Proves the framing fix: reading exactly IroCzReqWearEquipLength (9) then
        // IroCzReqTakeoffEquipLength (5) bytes from a concatenated stream leaves zero residual
        // bytes and each opcode header is read correctly - the fake 0xAB98 opcode this bug
        // previously produced can no longer occur.
        var equip = Convert.FromHexString("98090200020000005B");
        var unequip = Convert.FromHexString("AB0002004F");
        var stream = new byte[equip.Length + unequip.Length];
        equip.CopyTo(stream, 0);
        unequip.CopyTo(stream, equip.Length);

        var firstOpcode = BinaryPrimitives.ReadInt16LittleEndian(stream);
        Assert.Equal((short)0x0998, firstOpcode);
        Assert.True(IroEquipRequestPacket.TryParse(stream.AsSpan(0, PacketConstants.IroCzReqWearEquipLength), out var equipRequest));
        Assert.Equal((ushort)2, equipRequest.ClientIndex);

        var remaining = stream.AsSpan(PacketConstants.IroCzReqWearEquipLength);
        Assert.Equal(unequip.Length, remaining.Length); // zero residual bytes from the first packet
        var secondOpcode = BinaryPrimitives.ReadInt16LittleEndian(remaining);
        Assert.Equal((short)0x00ab, secondOpcode); // NOT the fake 0xAB98 the 8-byte bug produced
        Assert.True(IroUnequipRequestPacket.TryParse(remaining, out var unequipRequest));
        Assert.Equal((ushort)2, unequipRequest.ClientIndex);
    }

    [Fact]
    public void ConcatenatedStream_UnequipThenNextPacket_ParsesWithNoResidue()
    {
        // 0x00AB(5) followed immediately by another 0x00AB(5) (e.g. a second unequip) - proves
        // the 5-byte length alone (not just the equip/unequip pairing) leaves no residue.
        var first = Convert.FromHexString("AB0002004F");
        var second = Convert.FromHexString("AB00030085");
        var stream = new byte[first.Length + second.Length];
        first.CopyTo(stream, 0);
        second.CopyTo(stream, first.Length);

        Assert.True(IroUnequipRequestPacket.TryParse(stream.AsSpan(0, PacketConstants.IroCzReqTakeoffEquipLength), out var firstRequest));
        Assert.Equal((ushort)2, firstRequest.ClientIndex);

        var remaining = stream.AsSpan(PacketConstants.IroCzReqTakeoffEquipLength);
        Assert.Equal(second.Length, remaining.Length); // zero residual bytes
        Assert.Equal((short)0x00ab, BinaryPrimitives.ReadInt16LittleEndian(remaining));
        Assert.True(IroUnequipRequestPacket.TryParse(remaining, out var secondRequest));
        Assert.Equal((ushort)3, secondRequest.ClientIndex);
    }

    [Fact]
    public void BuildEquipAck_Success_UsesUninvertedResultCode()
    {
        var packet = IroEquipmentMutationPackets.BuildEquipAck(4, 0x000002, PacketConstants.EquipAckResultOk);

        Assert.Equal(11, packet.Length);
        Assert.Equal((short)0x0999, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal((ushort)4, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)));
        Assert.Equal(0x000002u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)));
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(8)));
        Assert.Equal((byte)0, packet[10]); // EquipAckResultOk = 0, NOT inverted
    }

    [Fact]
    public void BuildEquipAck_Failure_UsesFailResultCode()
    {
        var packet = IroEquipmentMutationPackets.BuildEquipAck(4, 0, PacketConstants.EquipAckResultFail);
        Assert.Equal((byte)2, packet[10]); // EquipAckResultFail = 2
    }

    [Fact]
    public void BuildUnequipAck_Success_UsesInvertedFlag()
    {
        var packet = IroEquipmentMutationPackets.BuildUnequipAck(4, 0x000002, success: true);

        Assert.Equal(9, packet.Length);
        Assert.Equal((short)0x099a, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal((ushort)4, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)));
        Assert.Equal(0x000002u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)));
        Assert.Equal((byte)0, packet[8]); // success -> flag=0 (INVERTED, unlike the equip ack)
    }

    [Fact]
    public void BuildUnequipAck_Failure_UsesInvertedFlag()
    {
        var packet = IroEquipmentMutationPackets.BuildUnequipAck(4, 0, success: false);
        Assert.Equal((byte)1, packet[8]); // failure -> flag=1 (INVERTED)
    }
}
