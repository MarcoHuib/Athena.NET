using System.Buffers.Binary;
using System.Text;
using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class IroMapTransitionPacketTests
{
    [Theory]
    [InlineData("iz_int01", "iz_int01.gat")]
    [InlineData("iz_int01.gat", "iz_int01.gat")]
    [InlineData("IZ_INT01.GAT", "IZ_INT01.GAT")]
    public void BuildSameServerMapChange_SerializesProvenLayout(string inputMap, string expectedWireMap)
    {
        var packet = IroMapTransitionPackets.BuildSameServerMapChange(inputMap, 51, 30);

        Assert.Equal(22, packet.Length);
        Assert.Equal((short)0x0091, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal(expectedWireMap, ReadNullTerminatedAscii(packet.AsSpan(2, 16)));
        Assert.Equal((ushort)51, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(18)));
        Assert.Equal((ushort)30, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(20)));
    }

    [Fact]
    public void BuildSameServerMapChange_RejectsMapNameBeyondWireField()
    {
        Assert.Throws<ArgumentException>(() =>
            IroMapTransitionPackets.BuildSameServerMapChange("map_name_is_too_long", 1, 2));
    }

    private static string ReadNullTerminatedAscii(ReadOnlySpan<byte> field)
    {
        var terminator = field.IndexOf((byte)0);
        return Encoding.ASCII.GetString(terminator >= 0 ? field[..terminator] : field);
    }
}
