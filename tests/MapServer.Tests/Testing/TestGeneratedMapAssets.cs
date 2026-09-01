namespace Athena.Net.MapServer.Tests.Testing;

// Locates the checked-in generated map pack source asset directly from the repository, rather than
// requiring MSBuild to copy the 53 MiB src/MapServer/Generated/Assets/Maps/AthenaMaps.bin into
// every test output directory (MapServer.csproj deliberately only copies it to PUBLISH output -
// see that file and ai/world-data.md - so ordinary development/test builds stay fast). Tests that
// need real generated collision data should open GeneratedMapCollisionProvider.Open(MapPackPath)
// against this path instead of OpenProduction(), which only ever resolves the published
// AppContext.BaseDirectory/MapData/AthenaMaps.bin layout and is not expected to find anything under
// a test project's own bin/ output.
internal static class TestGeneratedMapAssets
{
    public static string MapPackPath { get; } = Path.Combine(FindRepositoryRoot(), "src", "MapServer", "Generated", "Assets", "Maps", "AthenaMaps.bin");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Athena.NET repository root was not found.");
    }
}
