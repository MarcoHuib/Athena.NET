using System.Buffers.Binary;
using System.Text;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class IroWorldActorPacketTests
{
    [Fact]
    public void BuildWarpActor_SerializesCaptureProvenIro09ffLayout()
    {
        var actor = new WarpActor(110_000_123, "#room_out03", "iz_int03", 27, 30, 1, 1);

        var packet = IroWorldActorPackets.BuildWarpActor(actor);

        Assert.Equal(84 + 11, packet.Length);
        Assert.Equal((short)0x09ff, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal((ushort)packet.Length, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)));
        Assert.Equal((byte)6, packet[4]);
        Assert.Equal((uint)110_000_123, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(5)));
        Assert.Equal((ushort)300, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(13)));
        Assert.Equal((ushort)45, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(23)));
        Assert.Equal(new byte[] { 0x06, 0xc1, 0xe0 }, packet.AsSpan(63, 3).ToArray());
        Assert.Equal((byte)1, packet[66]);
        Assert.Equal((byte)1, packet[67]);
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(73)));
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(77)));
        Assert.Equal("#room_out03", Encoding.ASCII.GetString(packet.AsSpan(84)));
    }
}
