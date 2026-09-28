using Athena.WorldCompiler.Generation;

namespace WorldDataImporter.Tests;

// Warp source-load classification (the warp analogue of MobSpawnLoadClassifier). Pinned rAthena ships
// both npc/pre-re/warps/** and npc/re/warps/** rows for the same trigger cell; the runtime must activate
// only the ruleset's effective rows, decided by the script-config graph - never by file order.
public sealed class WarpLoadClassifierTests
{
    private static readonly IReadOnlySet<string> Active = new HashSet<string>(StringComparer.Ordinal)
    {
        "npc/re/warps/cities/izlude.txt",
        "npc/warps/fields/some_field.txt",
    };

    [Theory]
    [InlineData("legacy/rathena/npc/re/warps/cities/izlude.txt", "RenewalDefault")]
    [InlineData("npc/re/warps/cities/izlude.txt", "RenewalDefault")]
    [InlineData("legacy/rathena/npc/warps/fields/some_field.txt", "RenewalDefault")]
    [InlineData("legacy/rathena/npc/pre-re/warps/fields/prontera_fild.txt", "PreRenewalSource")]
    [InlineData("legacy/rathena/npc/events/some_event.txt", "Disabled")]
    [InlineData("legacy/rathena/npc/battleground/bg_common.txt", "Disabled")]
    public void Classify_ActiveGraphMembershipDecidesTheClass_NotThePathShape(string file, string expected) =>
        Assert.Equal(expected, WarpLoadClassifier.Classify(file, Active).ToString());

    [Fact]
    public void Classify_ClassIsIndependentOfListOrder()
    {
        string[] preFirst = ["legacy/rathena/npc/pre-re/warps/fields/prontera_fild.txt", "legacy/rathena/npc/re/warps/cities/izlude.txt"];
        string[] renewalFirst = [.. preFirst.Reverse()];

        Assert.Equal([WarpLoadClass.PreRenewalSource, WarpLoadClass.RenewalDefault], preFirst.Select(file => WarpLoadClassifier.Classify(file, Active)));
        Assert.Equal([WarpLoadClass.RenewalDefault, WarpLoadClass.PreRenewalSource], renewalFirst.Select(file => WarpLoadClassifier.Classify(file, Active)));
    }

    [Fact]
    public void Classify_ActiveGraphMembershipWinsOverPreRenewalPrefix()
    {
        // A pre-re path that the (hypothetical) active graph really loads is RenewalDefault: the graph is
        // consulted first, the path prefix is only the last fallback for the leftover bucket.
        var active = new HashSet<string>(StringComparer.Ordinal) { "npc/pre-re/warps/x.txt" };
        Assert.Equal(WarpLoadClass.RenewalDefault, WarpLoadClassifier.Classify("npc/pre-re/warps/x.txt", active));
    }

    [Fact]
    public void Classify_OverlayWarpFilesAreEmptyByDefault_SoNoDisabledWarpIsSilentlyActivated()
    {
        Assert.Empty(AthenaOverlaySourceFiles.WarpFiles);
    }

    [Fact]
    public void RealPinnedGraph_ClassifiesPrtFild08PreReAndRenewalRowsDifferently()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.sln"))) directory = directory.Parent;
        var root = Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("Athena.NET repository root was not found."), "legacy/rathena");
        var active = RathenaScriptConfigGraph.ResolveActiveNpcFiles(root).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(WarpLoadClass.PreRenewalSource, WarpLoadClassifier.Classify("legacy/rathena/npc/pre-re/warps/fields/prontera_fild.txt", active));
        Assert.Equal(WarpLoadClass.RenewalDefault, WarpLoadClassifier.Classify("legacy/rathena/npc/re/warps/cities/izlude.txt", active));
        Assert.Contains("npc/re/warps/cities/izlude.txt", active);
        Assert.DoesNotContain("npc/pre-re/warps/fields/prontera_fild.txt", active);
    }
}
