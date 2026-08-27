using System.Buffers.Binary;

namespace Athena.WorldCompiler.Generation;

// Mirrors Athena.Net.MapServer.World.MapCellFlags exactly (same bit values/names) so
// MapCollisionArtifactWriter's output matches what MapCollisionArtifact.Read expects at the
// MapServer end. Deliberately a SEPARATE type, not a shared reference: WorldDataImporter has no
// project dependency on MapServer (an explicit architectural rule for this vertical slice - the
// offline importer and the runtime it feeds stay decoupled), so the two enums are kept in sync by
// this file's own doc comment/tests rather than by sharing a compiled type.
[Flags]
public enum CompiledMapCellFlags : byte
{
    None = 0,
    Walkable = 1,
    Shootable = 2,
    Water = 4,
}

public sealed record CompiledMapCollision(string MapName, int Width, int Height, CompiledMapCellFlags[] Cells);

// Compiles a raw client .gat file into Athena collision data. Traced against pinned
// legacy/rathena/src/tool/mapcache.cpp's own GAT reader (read_map, mapcache.cpp:68-116,
// e985006171d2eb320ee512a653f4c83aea3d81b6) and legacy/rathena/src/map/map.cpp's map_gat2cell
// (map.cpp:3280-3299), NOT a general GAT/GRF library:
//
//   offset 0..6   6-byte file signature ("GRAT\x01\x02" per the documented client .gat format;
//                 pinned rAthena's own reader never validates this - mapcache.cpp:76-79 opens the
//                 file via grfio and reads dimensions unconditionally - so this check is an
//                 Athena-side strengthening for "fail clearly on malformed input", not a rAthena-
//                 sourced behavior)
//   offset 6..10  width  (uint32 little-endian, GetULong(gat+6), mapcache.cpp:88)
//   offset 10..14 height (uint32 little-endian, GetULong(gat+10), mapcache.cpp:89)
//   offset 14..   width*height records of 20 bytes each:
//                     [0..16)  four little-endian floats (per-corner cell heights - unused here,
//                              this compiler produces collision data only, never terrain height)
//                     [16..20) uint32 little-endian GAT cell type (mapcache.cpp:104, GetULong(gat+off+16))
//
// GAT type -> CompiledMapCellFlags (map_gat2cell, map.cpp:3286-3296): types 0/2/4/6 ->
// Walkable|Shootable (rAthena's own comments mark 2/4/6 as "???" - unused/unknown types that
// behave identically to plain walkable ground; not invented by this compiler); type 1 -> None
// (wall); type 3 -> Walkable|Shootable|Water; type 5 -> Shootable only (a snipable gap/cliff,
// CELL_CHKCLIFF). An unrecognized type is a hard failure here (never silently treated as walkable
// or as a wall), matching pinned map_gat2cell's own ShowWarning-on-unknown-type behavior
// repurposed as a thrown error since this compiler has no equivalent of continuing with
// best-effort runtime state.
//
// Deliberately NOT implemented: the pinned mapcache tool's separate .rsw water-height adjustment
// (mapcache.cpp:107-108, "type==0 but height above water level -> promote to type 3") - that
// requires parsing a second proprietary file format for a refinement this vertical slice does not
// need; a .gat cell already encoded as type 3 is unaffected by this omission.
public static class MapCollisionCompiler
{
    private static readonly byte[] ExpectedSignature = [0x47, 0x52, 0x41, 0x54, 0x01, 0x02]; // "GRAT" + 0x01 0x02
    private const int HeaderLength = 14;
    private const int CellRecordLength = 20;
    private const int CellTypeOffsetInRecord = 16;

    public static CompiledMapCollision Compile(ReadOnlySpan<byte> gatBytes, string mapName)
    {
        if (gatBytes.Length < HeaderLength)
            throw new InvalidDataException("GAT input is truncated: missing the 14-byte header.");
        if (!gatBytes[..6].SequenceEqual(ExpectedSignature))
            throw new InvalidDataException("GAT input does not start with the expected 'GRAT' file signature.");

        var width = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(gatBytes.Slice(6, 4)));
        var height = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(gatBytes.Slice(10, 4)));
        if (width <= 0 || height <= 0)
            throw new InvalidDataException($"GAT input declares invalid dimensions ({width}x{height}).");

        var cellCount = checked(width * height);
        var expectedLength = checked(HeaderLength + cellCount * CellRecordLength);
        if (gatBytes.Length < expectedLength)
            throw new InvalidDataException($"GAT input is truncated: expected {expectedLength} bytes for a {width}x{height} map, found {gatBytes.Length}.");

        var cells = new CompiledMapCellFlags[cellCount];
        for (var i = 0; i < cellCount; i++)
        {
            var recordOffset = HeaderLength + i * CellRecordLength;
            var gatType = BinaryPrimitives.ReadUInt32LittleEndian(gatBytes.Slice(recordOffset + CellTypeOffsetInRecord, 4));
            cells[i] = GatTypeToFlags(gatType, i);
        }

        return new CompiledMapCollision(mapName, width, height, cells);
    }

    private static CompiledMapCellFlags GatTypeToFlags(uint gatType, int cellIndex) => gatType switch
    {
        0 or 2 or 4 or 6 => CompiledMapCellFlags.Walkable | CompiledMapCellFlags.Shootable,
        1 => CompiledMapCellFlags.None,
        3 => CompiledMapCellFlags.Walkable | CompiledMapCellFlags.Shootable | CompiledMapCellFlags.Water,
        5 => CompiledMapCellFlags.Shootable,
        _ => throw new InvalidDataException($"GAT cell {cellIndex} has unrecognized type {gatType}."),
    };
}
