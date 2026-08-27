using System.Buffers.Binary;
using System.Text;

namespace Athena.WorldCompiler.Generation;

// Writes the small Athena-owned deterministic collision artifact format consumed at the MapServer
// end by Athena.Net.MapServer.World.MapCollisionArtifact.Read. Kept as a standalone writer here
// (not a shared type) because WorldDataImporter has no project dependency on MapServer - see
// MapCollisionCompiler's own doc comment. The two sides are kept in sync by
// CompilerTests.MapCollisionRoundTrip_MatchesRuntimeReader, which decodes this writer's own output
// byte-for-byte against the exact layout MapCollisionArtifact.Read expects.
//
// Layout (all multi-byte integers little-endian) - see MapCollisionArtifact's own doc comment for
// the authoritative description this mirrors:
//   magic("AMC1") mapNameLen(uint32) mapName(UTF-8) width(int32) height(int32) cells(width*height bytes)
public static class MapCollisionArtifactWriter
{
    private static readonly byte[] Magic = "AMC1"u8.ToArray();

    public static byte[] Write(CompiledMapCollision map)
    {
        var nameBytes = Encoding.UTF8.GetBytes(map.MapName);
        var cellCount = map.Width * map.Height;
        if (map.Cells.Length != cellCount)
            throw new ArgumentException($"Cell count {map.Cells.Length} does not match width*height ({cellCount}).", nameof(map));

        var buffer = new byte[4 + 4 + nameBytes.Length + 4 + 4 + cellCount];
        var offset = 0;

        Magic.CopyTo(buffer, offset); offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), (uint)nameBytes.Length); offset += 4;
        nameBytes.CopyTo(buffer, offset); offset += nameBytes.Length;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), map.Width); offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), map.Height); offset += 4;

        for (var i = 0; i < cellCount; i++)
            buffer[offset + i] = (byte)map.Cells[i];

        return buffer;
    }
}
