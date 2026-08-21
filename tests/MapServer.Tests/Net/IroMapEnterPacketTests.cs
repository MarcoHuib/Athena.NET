using System.Buffers.Binary;
using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class IroMapEnterPacketTests
{
    [Fact]
    public void BuildInitialBootstrap_SerializesCaptureProvenPacketSequence()
    {
        var auth = new MapAuthOkData(
            0x005faf3b,
            0x025c423a,
            1,
            2,
            0,
            0,
            false,
            "iz_int01.gat",
            18,
            27,
            0,
            0,
            0);

        var result = IroMapEnterPackets.BuildInitialBootstrap(auth, 0x0a10acb2);

        Assert.Equal(29, result.Length);
        Assert.Equal((short)0x0b18, BinaryPrimitives.ReadInt16LittleEndian(result));
        Assert.Equal((short)0, BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(2)));
        Assert.Equal((short)0x0283, BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(4)));
        Assert.Equal(auth.AccountId, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(6)));
        Assert.Equal((short)0x0ade, BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(10)));
        Assert.Equal((uint)70, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(12)));
        Assert.Equal((short)0x02eb, BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(16)));
        Assert.Equal((uint)0x0a10acb2, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(18)));
        Assert.Equal(new byte[] { 0x04, 0x81, 0xb0 }, result.AsSpan(22, 3).ToArray());
        Assert.Equal((byte)5, result[25]);
        Assert.Equal((byte)5, result[26]);
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(27)));
    }
}
