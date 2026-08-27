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

    // Same capture (kill-poring-heal-jobup.pcapng), frame 566/674: the walking Poring's
    // 0x09FD ZC_NOTIFY_MOVEENTRY11 decodes to src=(75,51) dst=(75,57) subcell 8/8, and its
    // matching 0x0088 ZC_STOPMOVE at frame 674 confirms it stops at (75,57) - see
    // ai/iro-2026-wire.md lines 404/409. Full HP (55/55), matching the Poring's own stand-entry
    // fixture above.
    [Fact]
    public void BuildWalkEntry_MatchesCapturedPoringWalkLayout()
    {
        var packet = IroMonsterActorPackets.BuildWalkEntry(
            actorId: 0x00001E9D,
            mobClassId: 2401,
            mobWalkSpeed: 400,
            name: "Poring",
            srcX: 75,
            srcY: 51,
            dstX: 75,
            dstY: 57,
            moveStartTime: 0,
            currentHp: 55,
            maxHp: 55);

        Assert.Equal(90 + 6, packet.Length);
        Assert.Equal((short)0x09fd, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal((ushort)packet.Length, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)));
        Assert.Equal((byte)5, packet[4]); // NPC_MOB_TYPE, same object type as the stand entry.
        Assert.Equal(0x00001E9Du, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(5)));
        Assert.Equal((ushort)400, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(13)));
        Assert.Equal((ushort)2401, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(23)));

        // Packed src(75,51)/dst(75,57)/subcell(8,8), independently re-derived from pinned WBUFPOS2
        // (clif.cpp:182-190) and cross-checked to match ai/iro-2026-wire.md's own decoded values.
        Assert.Equal(new byte[] { 0x12, 0xc3, 0x31, 0x2c, 0x39, 0x88 }, packet.AsSpan(67, 6).ToArray());

        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(79)));
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(83)));
        Assert.Equal("Poring", Encoding.ASCII.GetString(packet.AsSpan(90)));
    }

    [Fact]
    public void BuildWalkEntry_DamagedMonster_SendsRealHpValues()
    {
        var packet = IroMonsterActorPackets.BuildWalkEntry(
            actorId: 0x00001E9D, mobClassId: 2401, mobWalkSpeed: 400, name: "Poring",
            srcX: 75, srcY: 51, dstX: 75, dstY: 57, moveStartTime: 0, currentHp: 18, maxHp: 55);

        Assert.Equal(55, BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(79)));
        Assert.Equal(18, BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(83)));
    }

    [Fact]
    public void BuildWalkEntry_EncodesMoveStartTimeAtOffset37()
    {
        var packet = IroMonsterActorPackets.BuildWalkEntry(
            actorId: 1, mobClassId: 2401, mobWalkSpeed: 400, name: "Poring",
            srcX: 0, srcY: 0, dstX: 1, dstY: 1, moveStartTime: 0x12345678, currentHp: 55, maxHp: 55);

        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(37)));
    }
}
