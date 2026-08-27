using System.Buffers.Binary;
using System.Text;

namespace Athena.Net.MapServer.World;

// Reads the small Athena-owned deterministic collision artifact format produced offline by
// tools/WorldDataImporter's compile-map-collision command (MapCollisionArtifactWriter) from a
// locally supplied .gat file (see MapCollisionCompiler's own doc comment for the pinned
// .gat/GAT-type trace). MapServer only ever reads this format; nothing in the runtime writes it -
// see that command's own doc comment for why the writer is a separate, dependency-free type
// rather than shared with this reader. This is NOT a container for
// original GAT bytes, textures, heights, RSW objects, or dynamic runtime cell state (npc/icewall/
// basilica/etc.) - only the three static MapCellFlags bits per cell, matching MapCollisionMap's
// own doc comment on why one byte per cell (not one bit) is the correct minimum.
//
// Layout (all multi-byte integers little-endian):
//   [0..4)   magic       ASCII "AMC1" (Athena Map Collision, format version 1 folded into the magic
//                         itself rather than a separate version field - a future incompatible
//                         layout gets its own magic value instead of a branch on an old one)
//   [4..8)   mapNameLen  uint32, length in bytes of the UTF-8 map name that follows
//   [..]     mapName     UTF-8, extensionless internal Athena map name (e.g. "int_land03")
//   [..+4)   width       int32, > 0
//   [..+4)   height      int32, > 0
//   [..]     cells       width*height bytes, one MapCellFlags value per cell, row-major
//                         (index = x + y*width, matching MapCollisionMap.GetCell)
//
// No compression, no multi-map container: one artifact file is exactly one map, matching this
// project's existing one-file-per-generated-unit convention (compile-mob-spawn, compile-item,
// etc.) rather than rAthena's own multi-map map_cache.dat container, which this format does not
// need to be compatible with (Athena never reads a real mapcache file).
public static class MapCollisionArtifact
{
    private static readonly byte[] Magic = "AMC1"u8.ToArray();
    private const int MaxMapNameLength = 64; // Matches pinned MAP_NAME_LENGTH_EXT headroom; a sanity bound, not a real rAthena constant.

    public static MapCollisionMap Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) throw new InvalidDataException("Map collision artifact is truncated: missing header.");
        if (!data[..4].SequenceEqual(Magic))
            throw new InvalidDataException("Map collision artifact has an unrecognized/unsupported format magic.");

        var offset = 4;
        var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]); offset += 4;
        if (nameLength == 0 || nameLength > MaxMapNameLength)
            throw new InvalidDataException($"Map collision artifact has an invalid map name length ({nameLength}).");
        if (data.Length < offset + (int)nameLength + 8)
            throw new InvalidDataException("Map collision artifact is truncated: missing map name/dimensions.");

        var mapName = Encoding.UTF8.GetString(data.Slice(offset, (int)nameLength)); offset += (int)nameLength;
        var width = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]); offset += 4;
        var height = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]); offset += 4;
        if (width <= 0 || height <= 0)
            throw new InvalidDataException($"Map collision artifact has invalid dimensions ({width}x{height}).");

        var cellCount = checked(width * height);
        if (data.Length - offset != cellCount)
            throw new InvalidDataException($"Map collision artifact cell data length ({data.Length - offset}) does not match width*height ({cellCount}).");

        var cells = new MapCellFlags[cellCount];
        for (var i = 0; i < cellCount; i++)
            cells[i] = (MapCellFlags)data[offset + i];

        return new MapCollisionMap(mapName, width, height, cells);
    }

    public static MapCollisionMap ReadFile(string path) => Read(File.ReadAllBytes(path));
}
