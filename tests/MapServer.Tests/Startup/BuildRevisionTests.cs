using Athena.Net.MapServer.Startup;

namespace Athena.Net.MapServer.Tests.Startup;

// Section 1 of the monster-engagement corrective pass: this repo's own build must embed a real
// git revision (never "unknown") into the compiled assembly - a stale-bin/obj incident on this
// project is exactly what this mechanism exists to make impossible to reproduce blindly. This
// test proves the embedding actually happened for the assembly under test, not merely that the
// MSBuild target exists in source.
public sealed class BuildRevisionTests
{
    [Fact]
    public void Current_IsARealGitRevision_NotTheUnknownFallback()
    {
        Assert.NotEqual("unknown", BuildRevision.Current);
        Assert.Matches("^[0-9a-f]{40}$", BuildRevision.Current);
    }
}
