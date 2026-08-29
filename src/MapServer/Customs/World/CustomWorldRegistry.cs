// Handwritten Athena.NET custom development content.
// NOT generated from rAthena.
// Never modified by WorldDataImporter.
using Athena.Net.MapServer.Customs.World.Izlude;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Customs.World;

// Composition boundary between generated and handwritten Athena.NET world content - see
// ai/map-server.md's "Handwritten custom world content" section. Applies every enabled Customs
// definition onto the SAME WorldRegistryBuilder instance MapServerWorld.Build already used for
// GeneratedScriptRegistry.Register, so generated and custom content share one entity/script
// registry, one WorldActorIdAllocator, and one collision-validation pass - never a second,
// parallel world. Mirrors AcademyWorld.Register's exact signature/shape (definitions + their
// placements on an externally supplied builder) so custom content composes identically to
// generated content; the only difference is provenance, not runtime behavior (AGENTS.md/
// ai/map-server.md's "same runtime types" rule).
public static class CustomWorldRegistry
{
    public static void Register(WorldRegistryBuilder builder)
    {
        IzludeCustomWorld.Register(builder);
    }
}
