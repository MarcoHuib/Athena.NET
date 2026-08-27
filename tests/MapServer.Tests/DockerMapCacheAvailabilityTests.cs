namespace Athena.Net.MapServer.Tests;

// Lightweight, Docker-daemon-free consistency check between the Dockerfile, the Docker-served
// config, and the real pinned map_cache.dat file - actually building/running the image was
// verified manually (see the task history for this fix: `docker build` + `docker run` against the
// real image confirmed "Loaded map_cache.dat '...': 1288 maps." and
// "Monster spawn positioning: rAthena collision-backed" in the container's own stdout), but a
// full Docker build is unreasonable to run as part of every test suite invocation. This test
// instead guards the THREE pieces that must all agree with each other, so an edit to any one of
// them that breaks the arrangement fails immediately rather than silently regressing until the
// next manual Docker run:
//   1. legacy/rathena/db/map_cache.dat actually exists at the path both the Dockerfile and the
//      Docker config reference (it is part of the pinned e985006171d2eb320ee512a653f4c83aea3d81b6
//      legacy/rathena submodule checkout, not generated/copied content);
//   2. the MapServer Dockerfile bakes that exact file into the image at that same relative path
//      (COPY legacy/rathena/db/map_cache.dat ./legacy/rathena/db/map_cache.dat);
//   3. conf/docker/map_athena.conf's map_cache_path value is that same relative path, so it
//      resolves correctly against the image's own WORKDIR /app (which matches this repository's
//      own root-relative layout one level down - see the Dockerfile's own doc comment).
public sealed class DockerMapCacheAvailabilityTests
{
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Athena.NET repository root was not found.");
    }

    [Fact]
    public void RealPinnedMapCache_ExistsAtTheRepositoryRelativePathDockerAndConfigBothReference()
    {
        var repository = FindRepositoryRoot();
        var path = Path.Combine(repository, "legacy/rathena/db/map_cache.dat");

        Assert.True(File.Exists(path), $"'{path}' does not exist - is the legacy/rathena submodule checked out?");
    }

    [Fact]
    public void MapServerDockerfile_CopiesTheRealMapCacheFile_AtTheRepositoryRelativePath()
    {
        var repository = FindRepositoryRoot();
        var dockerfile = File.ReadAllText(Path.Combine(repository, "src/MapServer/Dockerfile"));

        Assert.Contains("COPY legacy/rathena/db/map_cache.dat ./legacy/rathena/db/map_cache.dat", dockerfile);
    }

    [Fact]
    public void DockerMapServerConfig_ConfiguresMapCachePath_AtTheSameRelativePathTheDockerfileBakesIn()
    {
        var repository = FindRepositoryRoot();
        var config = File.ReadAllText(Path.Combine(repository, "conf/docker/map_athena.conf"));

        Assert.Contains("map_cache_path: legacy/rathena/db/map_cache.dat", config);
    }

    [Fact]
    public void LocalDevTemplateConfig_AlsoConfiguresMapCachePath_AtTheSameRepositoryRelativePath()
    {
        // Local `dotnet run` from the repo root and the Docker image both resolve
        // "legacy/rathena/db/map_cache.dat" the same way (relative to their respective CWD, which
        // is the repository root locally and /app in the image - matching layouts one level down)
        // - a single repository-relative config value is correct for both, so both templates use
        // the identical string rather than diverging environment-specific paths.
        var repository = FindRepositoryRoot();
        var config = File.ReadAllText(Path.Combine(repository, "conf/templates/map_athena.conf"));

        Assert.Contains("map_cache_path: legacy/rathena/db/map_cache.dat", config);
    }
}
