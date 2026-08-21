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
