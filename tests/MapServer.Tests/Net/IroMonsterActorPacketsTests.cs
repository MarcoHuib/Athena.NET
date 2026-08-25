using System.Buffers.Binary;
using System.Text;
using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.Tests.Net;

// Sanitized fixture derived from verified stock iRO capture
// kill-poring-heal-jobup.pcapng, frame 566: G_PORING (class 2401), actor
// 0x00001E9D, position (75,51,dir 0), full 55/55 HP. Actor ID 0x00001E9D is
// not secret and is retained for traceability; no authentication/session
// material is present in this fixture.
public sealed class IroMonsterActorPacketsTests
{
    [Fact]
    public void BuildStandEntry_MatchesCapturedPoringLayout()
    {
        var packet = IroMonsterActorPackets.BuildStandEntry(
            actorId: 0x00001E9D,
            mobClassId: 2401,
            mobWalkSpeed: 400,
            name: "Poring",
            x: 75,
            y: 51,
            direction: 0,
            currentHp: 55,
            maxHp: 55);

        Assert.Equal(84 + 6, packet.Length);
        Assert.Equal((short)0x09ff, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal((ushort)packet.Length, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)));
        Assert.Equal((byte)5, packet[4]);
        Assert.Equal(0x00001E9Du, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(5)));
        Assert.Equal((ushort)400, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(13)));
        Assert.Equal((ushort)2401, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(23)));
        Assert.Equal(new byte[] { 0x12, 0xc3, 0x30 }, packet.AsSpan(63, 3).ToArray());
        // Full HP: 0xFFFFFFFF/0xFFFFFFFF sentinel, matching pinned clif_set_unit_idle.
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(73)));
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(77)));
        Assert.Equal("Poring", Encoding.ASCII.GetString(packet.AsSpan(84)));
    }

    [Fact]
    public void BuildStandEntry_DamagedMonster_SendsRealHpValues()
    {
        var packet = IroMonsterActorPackets.BuildStandEntry(
            actorId: 0x00001E9D,
            mobClassId: 2401,
            mobWalkSpeed: 400,
            name: "Poring",
            x: 75,
            y: 51,
            direction: 0,
            currentHp: 18,
            maxHp: 55);

        Assert.Equal(55, BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(73)));
        Assert.Equal(18, BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(77)));
    }

    [Fact]
    public void BuildStandEntry_ObjectType5_DiffersFromWarpNpcObjectType6()
    {
        var packet = IroMonsterActorPackets.BuildStandEntry(1, 2401, 400, "Poring", 0, 0, 0, 55, 55);
        Assert.Equal((byte)5, packet[4]);
    }
}
