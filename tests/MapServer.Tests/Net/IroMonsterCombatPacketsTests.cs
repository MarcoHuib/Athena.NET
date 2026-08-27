using System.Buffers.Binary;
using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.Tests.Net;

// Sanitized fixtures derived from verified stock iRO capture
// kill-poring-heal-jobup.pcapng, frames 620 (0x08C8 damage), 694 (0x0080
// death), and 699 (0x0B41 Wood pickup). Actor ID 0x00001E9D is not secret
// and is retained for traceability; no authentication/session material is
// present in these fixtures.
public sealed class IroMonsterCombatPacketsTests
{
    [Fact]
    public void BuildNotifyAct3_MatchesCapturedDamageLayout()
    {
        var packet = IroMonsterCombatPackets.BuildNotifyAct3(
            srcActorId: 0x5FAF3B,
            dstActorId: 0x00001E9D,
            tick: 593742177,
            srcSpeed: 460,
            dstSpeed: 480,
            damage: 37,
            div: 1,
            actionType: 0);

        Assert.Equal(34, packet.Length);
        Assert.Equal((short)0x08c8, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal(0x5FAF3Bu, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2)));
        Assert.Equal(0x00001E9Du, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(6)));
        Assert.Equal(593742177u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(10)));
        Assert.Equal(460u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(14)));
        Assert.Equal(480u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(18)));
        Assert.Equal(37u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(22)));
        Assert.Equal(0, packet[26]);
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(27)));
        Assert.Equal(0, packet[29]);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(30)));
    }

    [Fact]
    public void BuildNotifyVanish_MatchesCapturedDeathLayout()
    {
        var packet = IroMonsterCombatPackets.BuildNotifyVanish(0x00001E9D, PacketConstants.ZcNotifyVanishReasonDied);

        Assert.Equal(7, packet.Length);
        Assert.Equal((short)0x0080, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal(0x00001E9Du, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2)));
        Assert.Equal(1, packet[6]);
    }

    [Fact]
    public void BuildItemPickupAck_MatchesCapturedWoodLayout()
    {
        var packet = IroMonsterCombatPackets.BuildItemPickupAck(clientIndex: 2, count: 1, itemId: 6008, itemType: 3);

        Assert.Equal(70, packet.Length);
        Assert.Equal((short)0x0b41, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(4)));
        Assert.Equal(6008u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(6)));
        Assert.Equal(1, packet[10]); // IsIdentified
        Assert.Equal(0, packet[11]); // IsDamaged
        Assert.Equal(3, packet[32]); // type = Etc
        Assert.Equal(0, packet[33]); // result = success
    }
}
