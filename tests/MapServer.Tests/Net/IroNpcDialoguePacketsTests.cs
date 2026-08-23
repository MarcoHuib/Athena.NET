using System.Buffers.Binary;
using System.Text;
using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class IroNpcDialoguePacketsTests
{
    [Fact]
    public void CapturedChangeDirection_ParsesClassicFieldsAndOpaqueTrailingByte()
    {
        Assert.True(IroChangeDirectionPacket.TryParse([0x61, 0x03, 0x01, 0x00, 0x03, 0x7d], out var packet));
        Assert.Equal((byte)1, packet.HeadDirection);
        Assert.Equal((byte)3, packet.BodyDirection);
        Assert.Equal((byte)0x7d, packet.OpaqueTrailingByte);
    }

    [Fact]
    public void CapturedClientPackets_ParseActorIdWithOpaqueTrailingByte()
    {
        Assert.True(IroNpcDialoguePackets.TryParseInteraction([0x90, 0x00, 0x1b, 0x1f, 0x00, 0x00, 0x00, 0x8d], out var interaction));
        Assert.True(IroNpcDialoguePackets.TryParseNext([0xb9, 0x00, 0x1b, 0x1f, 0x00, 0x00, 0x8e], out var next));
        Assert.Equal(7963u, interaction);
        Assert.Equal(7963u, next);
        Assert.False(IroNpcDialoguePackets.TryParseNext([0xb9, 0x00, 0x1b, 0x1f, 0x00, 0x00], out _));
    }

    [Fact]
    public void ServerDialoguePackets_MatchCapturedLayouts()
    {
        var message = IroNpcDialoguePackets.BuildMessage(7963, "[Captain Carocc]");
        Assert.Equal((short)0x00b4, BinaryPrimitives.ReadInt16LittleEndian(message));
        Assert.Equal(message.Length, BinaryPrimitives.ReadUInt16LittleEndian(message.AsSpan(2)));
        Assert.Equal(7963u, BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(4)));
        Assert.Equal("[Captain Carocc]\0", Encoding.ASCII.GetString(message.AsSpan(8)));
        Assert.Equal(new byte[] { 0xb5, 0x00, 0x1b, 0x1f, 0x00, 0x00 }, IroNpcDialoguePackets.BuildNext(7963));
        Assert.Equal(new byte[] { 0xb6, 0x00, 0x1b, 0x1f, 0x00, 0x00 }, IroNpcDialoguePackets.BuildClose(7963));
    }

    [Fact]
    public void CapturedMenuAndSelectionLayouts_AreExact()
    {
        var menu = IroNpcDialoguePackets.BuildMenu(7963, ["Yes", "No"]);
        Assert.Equal((short)0x00b7, BinaryPrimitives.ReadInt16LittleEndian(menu));
        Assert.Equal(menu.Length, BinaryPrimitives.ReadUInt16LittleEndian(menu.AsSpan(2)));
        Assert.Equal(7963u, BinaryPrimitives.ReadUInt32LittleEndian(menu.AsSpan(4)));
        Assert.Equal("Yes:No:\0", Encoding.ASCII.GetString(menu.AsSpan(8)));

        Assert.True(IroNpcDialoguePackets.TryParseSelection([0xb8, 0x00, 0x1b, 0x1f, 0x00, 0x00, 0x02, 0x59], out var actorId, out var wireIndex, out var opaque));
        Assert.Equal(7963u, actorId);
        Assert.Equal((byte)2, wireIndex);
        Assert.Equal((byte)0x59, opaque);
    }

    [Fact]
    public void NormalNpcActor_UsesCapturedClassInExisting09ffLayout()
    {
        var packet = IroWorldActorPackets.BuildWarpActor(new(110_000_000, "Athena Test NPC", "int_land", 55, 63, 0, 0, 873));
        Assert.Equal((short)0x09ff, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal((byte)6, packet[4]);
        Assert.Equal((ushort)873, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(23)));
        Assert.Equal(new byte[] { 0x0d, 0xc3, 0xf0 }, packet.AsSpan(63, 3).ToArray());
    }
}
