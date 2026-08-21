using System.Buffers.Binary;
using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class IroMapPacketFramingTests
{
    [Fact]
    public async Task ReadNextPacketAsync_Reassembles0c1fAcrossFragmentedReads()
    {
        var expected = new byte[PacketConstants.IroCzMapAuthLength];
        BinaryPrimitives.WriteInt16LittleEndian(expected, PacketConstants.IroCzMapAuth);
        for (var index = 2; index < expected.Length; index++)
        {
            expected[index] = (byte)(index % 251);
        }

        await using var stream = new ChunkedReadStream(expected, 100, 300, 601);

        var actual = await MapClientSession.ReadNextPacketAsync(stream, CancellationToken.None);

        Assert.Equal(1001, actual.Length);
        Assert.Equal(PacketConstants.IroCzMapAuth, BinaryPrimitives.ReadInt16LittleEndian(actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ReadNextPacketAsync_PreservesPacketBoundaryWhenReadContainsMultiplePackets()
    {
        var mapAuth = new byte[PacketConstants.IroCzMapAuthLength];
        BinaryPrimitives.WriteInt16LittleEndian(mapAuth, PacketConstants.IroCzMapAuth);
        var ping = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(ping, PacketConstants.CzPingLive);
        await using var stream = new MemoryStream([.. mapAuth, .. ping]);

        var first = await MapClientSession.ReadNextPacketAsync(stream, CancellationToken.None);
        var second = await MapClientSession.ReadNextPacketAsync(stream, CancellationToken.None);

        Assert.Equal(mapAuth, first);
        Assert.Equal(ping, second);
    }

    [Fact]
    public async Task ReadNextPacketAsync_ReassemblesThreeByte007dAcrossFragmentedReads()
    {
        var expected = new byte[] { 0x7d, 0x00, 0xba };
        await using var stream = new ChunkedReadStream(expected, 1, 2);

        var actual = await MapClientSession.ReadNextPacketAsync(stream, CancellationToken.None);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ReadNextPacketAsync_PreservesOpaque007dByteBeforeCoalescedNextPacket()
    {
        var mapLoaded = new byte[] { 0x7d, 0x00, 0xba };
        var postEnter = new byte[] { 0x60, 0x03, 0xf8, 0xcb, 0xde, 0x04, 0xab, 0xc9, 0x08, 0x90 };
        await using var stream = new MemoryStream([.. mapLoaded, .. postEnter]);

        var first = await MapClientSession.ReadNextPacketAsync(stream, CancellationToken.None);
        var second = await MapClientSession.ReadNextPacketAsync(stream, CancellationToken.None);

        Assert.Equal(mapLoaded, first);
        Assert.Equal(postEnter, second);
    }

    private sealed class ChunkedReadStream : Stream
    {
        private readonly Queue<ReadOnlyMemory<byte>> _chunks;
        private ReadOnlyMemory<byte> _current;

        public ChunkedReadStream(byte[] data, params int[] chunkLengths)
        {
            Assert.Equal(data.Length, chunkLengths.Sum());
            _chunks = new Queue<ReadOnlyMemory<byte>>();
            var offset = 0;
            foreach (var length in chunkLengths)
            {
                _chunks.Enqueue(data.AsMemory(offset, length));
                offset += length;
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_current.IsEmpty && _chunks.Count > 0)
            {
                _current = _chunks.Dequeue();
            }

            if (_current.IsEmpty)
            {
                return ValueTask.FromResult(0);
            }

            var length = Math.Min(buffer.Length, _current.Length);
            _current.Span[..length].CopyTo(buffer.Span);
            _current = _current[length..];
            return ValueTask.FromResult(length);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
