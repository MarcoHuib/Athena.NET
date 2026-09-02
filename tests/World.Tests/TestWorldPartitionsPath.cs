namespace Athena.Net.World.Tests;

// Test-only composition-root helper: resolves the real conf/world_partitions.json path for tests
// that want to exercise the actual shipped topology (as opposed to a synthetic one built inline).
// Deliberately lives HERE, in the test project, never in Athena.World.Contracts -
// WorldPartitionTopologyLoader itself must never know about repository layout or a solution file
// (see its own doc comment); resolving a concrete path is always the caller's job, and a test's
// caller is this test project itself, exactly like MapServerApp.RunAsync/Athena.World's Program.cs
// resolve their own path via ATHENA_WORLD_PARTITIONS_PATH for their own process.
internal static class TestWorldPartitionsPath
{
    public static string Resolve()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "conf", "world_partitions.json")))
            directory = directory.Parent;
        return directory is null
            ? throw new FileNotFoundException("conf/world_partitions.json was not found relative to any ancestor of the test binary's output directory.")
            : Path.Combine(directory.FullName, "conf", "world_partitions.json");
    }
}
