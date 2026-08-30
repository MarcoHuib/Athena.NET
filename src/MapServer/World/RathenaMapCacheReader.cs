using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Athena.Rathena.Data;

namespace Athena.Net.MapServer.World;

// Directly reads pinned rAthena's own multi-map map_cache.dat container (legacy/rathena/
// db/map_cache.dat, e985006171d2eb320ee512a653f4c83aea3d81b6) into Athena's existing runtime
// MapCollisionMap/MapCellFlags types - the normal Athena world-geometry source (see
// ai/world-data.md), NOT an offline conversion step. rAthena already ships this server-side
// dataset (map dimensions + per-cell static terrain, built from the client GAT/RSW resources by
// rAthena's own offline src/tool/mapcache.cpp) inside the pinned checkout, so a developer never
// needs an installed Ragnarok client, a GRF, or a manually extracted .gat to get real map
// geometry - this reader consumes that pinned file exactly as it ships.
//
// Traced directly against pinned source (independently cross-checked against the real pinned
// legacy/rathena/db/map_cache.dat, 1288 maps, byte-for-byte - see RathenaMapCacheReaderTests):
//
//   map.cpp:156-159 (struct map_cache_main_header), map.cpp:3672-3717 (map_readfromcache),
//   map.cpp:3640-3666 (map_init_mapcache - whole file read into memory, no streaming):
//     [0..4)  file_size  uint32 LE (declared total file length; this reader validates it against
//                        the real input length as a fail-fast sanity check, but otherwise walks
//                        records by their own length fields exactly like pinned map_readfromcache
//                        does - it never trusts file_size for per-record bounds)
//     [4..6)  map_count  uint16 LE
//     [6..8)  padding    2 bytes of ordinary C structure-alignment padding. The pinned struct
//                        itself declares only 6 bytes (uint32 + uint16), but the compiler pads its
//                        real in-memory sizeof() to 8, and pinned map_readfromcache walks the file
//                        starting at "buffer + sizeof(struct map_cache_main_header)"
//                        (map.cpp:3677) - i.e. offset 8, not 6. Confirmed by parsing the real
//                        pinned file: with map_count=1288, every one of the 1288 declared record
//                        lengths lines up exactly against the declared file_size only when the
//                        first record starts at offset 8.
//
//   map.cpp:162-167 (struct map_cache_map_info), repeated map_count times back-to-back, each
//   immediately followed by that record's own compressed cell payload (map.cpp:3679-3687 walks
//   entries by jumping len bytes past each record - NOT a fixed stride, since len varies per map):
//     [0..12)  name  fixed 12-byte buffer (MAP_NAME_LENGTH = 11 + 1, mmo.hpp:163), NUL-padded/
//                    terminated ASCII map name (mapcache.cpp:134 strncpy) - never includes a
//                    ".gat" extension anywhere in this container
//     [12..14) xs    int16 LE - map width in cells
//     [14..16) ys    int16 LE - map height in cells
//     [16..20) len   int32 LE - byte length of the COMPRESSED payload immediately following this
//                    record (map.cpp:3686: "p += sizeof(struct map_cache_map_info) + info->len")
//     [20..20+len)   zlib-wrapped deflate (map.cpp:3705 decode_zip -> grfio.cpp:245-248,
//                    "return uncompress(...)" - standard zlib compress()/uncompress(), RFC 1950
//                    container, not raw deflate/gzip) cell bytes; decompresses to exactly xs*ys
//                    bytes, one raw GAT cell-type byte per cell, ROW-MAJOR in (x + y*xs) order
//                    (map.cpp:3710-3711: "for (xy = 0; xy < size; ++xy) m->cell[xy] =
//                    map_gat2cell(decode_buffer[xy])" - the flat xy index IS x + y*xs, matching
//                    MapCollisionMap.GetCell's own existing index formula exactly, so no
//                    coordinate transform is needed between this container and Athena's runtime
//                    type)
//
//   map.cpp:3280-3299 (map_gat2cell): the raw GAT type byte -> static bit mapping this reader
//   applies per cell - IDENTICAL semantics to MapCollisionCompiler's direct-.gat GatTypeToFlags
//   (types 0/2/4/6 -> Walkable|Shootable; 1 -> none/wall; 3 -> Walkable|Shootable|Water; 5 ->
//   Shootable only/cliff) because map_cache.dat's payload is nothing more than the SAME raw GAT
//   type bytes read_map (mapcache.cpp:68-116) already extracted from the client .gat, just
//   zlib-compressed and bundled with every other map into one container - proving both import
//   paths are genuinely alternate INPUT ENCODINGS of identical underlying cell semantics, not two
//   independent formats that happen to look similar.
//
//   map_readfromcache (map.cpp:3692-3693) treats xs<=0/ys<=0 as "skip this one record, keep
//   scanning" because its pinned caller is searching a shared file for one specific map name by
//   linear scan and a malformed OTHER entry must not abort that search. This reader instead fails
//   the WHOLE load loudly on any malformed record (see Read's InvalidDataException) - Athena loads
//   every map from the file in one pass at startup rather than probing for one name at a time, so
//   a malformed record here is definitionally a corrupt input file requiring operator attention,
//   never "some other map I don't care about right now".
public static class RathenaMapCacheReader
{
    private const int MainHeaderLength = 8; // uint32 file_size + uint16 map_count + 2 bytes alignment padding (see doc comment above)
    private const int MapNameFieldLength = 12; // MAP_NAME_LENGTH (mmo.hpp:163)
    private const int MapInfoHeaderLength = MapNameFieldLength + 2 + 2 + 4; // name(12) + xs(2) + ys(2) + len(4)

    // Reads every map declared in a pinned map_cache.dat buffer. One call, one file, all maps -
    // never scoped to a single map name (that is pinned rAthena's own map_readfromcache use case,
    // not Athena's: Athena loads the whole world's geometry once at startup, matching how every
    // other generated/imported Athena game-data table is loaded in full rather than probed
    // on-demand per lookup).
    public static IReadOnlyList<MapCollisionMap> ReadAll(ReadOnlySpan<byte> mapCacheBytes)
    {
        return RathenaMapCacheFormat.ReadAll(mapCacheBytes).Select(entry =>
            new MapCollisionMap(entry.Name, entry.Width, entry.Height,
                entry.RawCells.Select((cell, index) => GatTypeToFlags(cell, entry.Name, index)).ToArray())).ToArray();
    }

    public static IReadOnlyList<MapCollisionMap> ReadAllFromFile(string path) => ReadAll(File.ReadAllBytes(path));

    private static byte[] DecompressCells(ReadOnlySpan<byte> compressed, int expectedCellCount, string mapName)
    {
        // zlib format = a 2-byte zlib header + raw deflate stream + a 4-byte Adler-32 trailer
        // (RFC 1950), exactly what encode_zip/decode_zip (grfio.cpp:245-255) wrap around zlib's
        // own compress()/uncompress(). .NET's ZLibStream speaks this same container directly, so
        // no manual header/trailer stripping is needed (unlike DeflateStream, which expects raw
        // deflate only).
        using var input = new MemoryStream(compressed.ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(expectedCellCount);
        zlib.CopyTo(output);
        var decompressed = output.ToArray();

        if (decompressed.Length != expectedCellCount)
            throw new InvalidDataException($"map_cache.dat record '{mapName}' decompressed to {decompressed.Length} bytes, expected {expectedCellCount}.");

        return decompressed;
    }

    // map_gat2cell (map.cpp:3280-3299) - identical semantics to
    // Athena.WorldCompiler.Generation.MapCollisionCompiler's direct-.gat GatTypeToFlags (the two
    // are independently maintained per ai/world-data.md's WorldDataImporter/MapServer decoupling
    // rule, since MapServer has no reference to WorldDataImporter or vice versa).
    private static MapCellFlags GatTypeToFlags(byte gatType, string mapName, int cellIndex) => gatType switch
    {
        0 or 2 or 4 or 6 => MapCellFlags.Walkable | MapCellFlags.Shootable,
        1 => MapCellFlags.None,
        3 => MapCellFlags.Walkable | MapCellFlags.Shootable | MapCellFlags.Water,
        5 => MapCellFlags.Shootable,
        _ => throw new InvalidDataException($"map_cache.dat map '{mapName}' cell {cellIndex} has unrecognized GAT type {gatType}."),
    };
}
