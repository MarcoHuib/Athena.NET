using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class IroWireCompatibilityTests
{
    // prontera-walking.pcapng frame 3246 proves the field->Prontera transition lands at (156,34),
    // diverging from pinned legacy/rathena/npc/re/warps/fields/prontera_fild.txt:105's own computed
    // (156,26) - see IroWireCompatibility's own doc comment for the full provenance.
    [Fact]
    public void ResolveVerifiedWarpDestinationOverride_PrtFild08dToProntera_ReturnsCaptureVerified156_34()
    {
        var (x, y) = IroWireCompatibility.ResolveVerifiedWarpDestinationOverride("prt_fild08d", "prontera", pinnedX: 156, pinnedY: 26);
        Assert.Equal((ushort)156, x);
        Assert.Equal((ushort)34, y);
    }

    // Any OTHER (source,destination) pair - including a different map that happens to also target
    // "prontera", or the exact pinned prontera door from a DIFFERENT source map - must pass through
    // unmodified. Proves the override is keyed narrowly (SourceMap,DestinationMap), never merely by
    // destination map name.
    [Fact]
    public void ResolveVerifiedWarpDestinationOverride_UnrelatedSourceMap_ReturnsPinnedValueUnchanged()
    {
        var (x, y) = IroWireCompatibility.ResolveVerifiedWarpDestinationOverride("some_other_map", "prontera", pinnedX: 156, pinnedY: 26);
        Assert.Equal((ushort)156, x);
        Assert.Equal((ushort)26, y);
    }

    [Fact]
    public void ResolveVerifiedWarpDestinationOverride_UnrelatedDestinationMap_ReturnsPinnedValueUnchanged()
    {
        var (x, y) = IroWireCompatibility.ResolveVerifiedWarpDestinationOverride("prt_fild08d", "some_other_map", pinnedX: 10, pinnedY: 20);
        Assert.Equal((ushort)10, x);
        Assert.Equal((ushort)20, y);
    }
}
