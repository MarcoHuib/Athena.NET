using System.Reflection;

namespace Athena.Net.MapServer.Startup;

// Source revision the running binary was built from - embedded at build time via
// MapServer.csproj's SourceRevisionId (never a runtime git shell-out). Falls back to "unknown"
// when unavailable (e.g. building from a source archive with no .git directory).
public static class BuildRevision
{
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(version)) return "unknown";

        // SourceRevisionId is appended after a '+' (e.g. "1.0.0+<sha>") when a real revision was
        // embedded; "unknown" (no '+') passes through unchanged.
        var plusIndex = version.IndexOf('+');
        return plusIndex >= 0 ? version[(plusIndex + 1)..] : version;
    }
}
