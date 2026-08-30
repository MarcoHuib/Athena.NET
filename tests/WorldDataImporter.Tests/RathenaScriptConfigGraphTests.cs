using Athena.WorldCompiler.Generation;

namespace WorldDataImporter.Tests;

// RathenaScriptConfigGraph task-section-26 coverage: active npc:, commented npc:, active import:,
// commented import:, nested imports, source order, cycle detection (real cycle vs. legitimate
// diamond-import DAG re-use), missing referenced file, duplicate active reference. Synthetic
// fixtures live under a per-test temp directory (small, disposable .conf/.txt trees) - the real
// pinned legacy/rathena tree is exercised separately, in the second [Fact] group below.
public sealed class RathenaScriptConfigGraphTests : IDisposable
{
    private readonly string _root;

    public RathenaScriptConfigGraphTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "athena-script-config-graph-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "npc"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void WriteConf(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private void WriteNpcFile(string relativePath) => WriteConf(relativePath, "// synthetic npc source file\n");

    [Fact]
    public void ParseFile_RecognizesActiveNpcDirective()
    {
        var directives = RathenaScriptConfigGraph.ParseFile("npc: npc/foo.txt\n", "test.conf");
        var directive = Assert.Single(directives);
        Assert.Equal(ScriptDirectiveKind.Npc, directive.Kind);
        Assert.Equal("npc/foo.txt", directive.TargetPath);
        Assert.True(directive.Active);
        Assert.Equal(1, directive.SourceLine);
    }

    [Fact]
    public void ParseFile_CommentedNpcDirective_IsRepresentedButInactive()
    {
        var directives = RathenaScriptConfigGraph.ParseFile("//npc: npc/foo.txt\n", "test.conf");
        var directive = Assert.Single(directives);
        Assert.Equal(ScriptDirectiveKind.Npc, directive.Kind);
        Assert.False(directive.Active);
    }

    [Fact]
    public void ParseFile_RecognizesActiveImportDirective()
    {
        var directives = RathenaScriptConfigGraph.ParseFile("import: npc/other.conf\n", "test.conf");
        var directive = Assert.Single(directives);
        Assert.Equal(ScriptDirectiveKind.Import, directive.Kind);
        Assert.True(directive.Active);
    }

    [Fact]
    public void ParseFile_CommentedImportDirective_IsRepresentedButInactive()
    {
        var directives = RathenaScriptConfigGraph.ParseFile("//import: npc/other.conf\n", "test.conf");
        var directive = Assert.Single(directives);
        Assert.Equal(ScriptDirectiveKind.Import, directive.Kind);
        Assert.False(directive.Active);
    }

    [Fact]
    public void ParseFile_PreservesSourceOrder()
    {
        var text = "npc: npc/a.txt\nnpc: npc/b.txt\nnpc: npc/c.txt\n";
        var directives = RathenaScriptConfigGraph.ParseFile(text, "test.conf");
        Assert.Equal(["npc/a.txt", "npc/b.txt", "npc/c.txt"], directives.Select(d => d.TargetPath));
    }

    [Fact]
    public void ResolveActiveNpcFiles_FollowsNestedActiveImports()
    {
        WriteConf("npc/main.conf", "import: npc/a.conf\n");
        WriteConf("npc/a.conf", "import: npc/b.conf\n");
        WriteConf("npc/b.conf", "npc: npc/leaf.txt\n");
        WriteNpcFile("npc/leaf.txt");

        var result = RathenaScriptConfigGraph.ResolveActiveNpcFiles(_root, "npc/main.conf");

        Assert.Equal(["npc/leaf.txt"], result);
    }

    [Fact]
    public void ResolveActiveNpcFiles_DoesNotFollowCommentedImport()
    {
        WriteConf("npc/main.conf", "//import: npc/a.conf\nnpc: npc/direct.txt\n");
        WriteConf("npc/a.conf", "npc: npc/leaf.txt\n");
        WriteNpcFile("npc/direct.txt");
        WriteNpcFile("npc/leaf.txt");

        var result = RathenaScriptConfigGraph.ResolveActiveNpcFiles(_root, "npc/main.conf");

        Assert.Equal(["npc/direct.txt"], result);
    }

    [Fact]
    public void ResolveActiveNpcFiles_DoesNotIncludeCommentedNpcDirective()
    {
        WriteConf("npc/main.conf", "npc: npc/active.txt\n//npc: npc/inactive.txt\n");
        WriteNpcFile("npc/active.txt");

        var result = RathenaScriptConfigGraph.ResolveActiveNpcFiles(_root, "npc/main.conf");

        Assert.Equal(["npc/active.txt"], result);
    }

    // Diamond-import DAG re-use is NOT a cycle: main imports both A and B, and both A and B import
    // the same shared Common conf. Common must be visited (its npc: targets included) exactly once,
    // with no exception thrown - proves the `completed` set (not merely a flat visited set) is what
    // guards re-entry, matching pinned npc_addsrcfile's own silent-no-op-on-already-loaded semantics
    // (src/map/npc.cpp:3605-3629).
    [Fact]
    public void ResolveActiveNpcFiles_DiamondImport_IsNotReportedAsACycle()
    {
        WriteConf("npc/main.conf", "import: npc/a.conf\nimport: npc/b.conf\n");
        WriteConf("npc/a.conf", "import: npc/common.conf\n");
        WriteConf("npc/b.conf", "import: npc/common.conf\n");
        WriteConf("npc/common.conf", "npc: npc/shared.txt\n");
        WriteNpcFile("npc/shared.txt");

        var result = RathenaScriptConfigGraph.ResolveActiveNpcFiles(_root, "npc/main.conf");

        Assert.Equal(["npc/shared.txt"], result);
    }

    // A genuine cycle (A imports B, B imports A) must fail with a useful chain diagnostic - distinct
    // from the diamond case above, which must NOT throw.
    [Fact]
    public void ResolveActiveNpcFiles_RealCycle_ThrowsWithChain()
    {
        WriteConf("npc/a.conf", "import: npc/b.conf\n");
        WriteConf("npc/b.conf", "import: npc/a.conf\n");

        var ex = Assert.Throws<InvalidOperationException>(() => RathenaScriptConfigGraph.ResolveActiveNpcFiles(_root, "npc/a.conf"));

        Assert.Contains("npc/a.conf", ex.Message, StringComparison.Ordinal);
        Assert.Contains("npc/b.conf", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveActiveNpcFiles_MissingActiveImportTarget_Throws()
    {
        WriteConf("npc/main.conf", "import: npc/missing.conf\n");

        Assert.Throws<FileNotFoundException>(() => RathenaScriptConfigGraph.ResolveActiveNpcFiles(_root, "npc/main.conf"));
    }

    [Fact]
    public void ResolveActiveNpcFiles_MissingActiveNpcTarget_Throws()
    {
        WriteConf("npc/main.conf", "npc: npc/missing.txt\n");

        Assert.Throws<FileNotFoundException>(() => RathenaScriptConfigGraph.ResolveActiveNpcFiles(_root, "npc/main.conf"));
    }

    [Fact]
    public void ResolveActiveNpcFiles_MissingCommentedTarget_IsNeverRead()
    {
        // A commented directive's target is never visited/validated at all - matches "commented
        // directives remain inactive" (task section 1); no exception even though the file is absent.
        WriteConf("npc/main.conf", "//npc: npc/missing.txt\n//import: npc/missing.conf\n");

        var result = RathenaScriptConfigGraph.ResolveActiveNpcFiles(_root, "npc/main.conf");

        Assert.Empty(result);
    }

    // A duplicate ACTIVE npc: reference to the same target file, from two distinct sibling lines
    // (not a diamond-import), collapses to one entry - matches pinned npc_addsrcfile's real
    // deduplication semantics (src/map/npc.cpp:3619-3621: `util::vector_exists` short-circuits a
    // re-add).
    [Fact]
    public void ResolveActiveNpcFiles_DuplicateActiveNpcReference_CollapsesToOneEntry()
    {
        WriteConf("npc/main.conf", "npc: npc/shared.txt\nnpc: npc/shared.txt\n");
        WriteNpcFile("npc/shared.txt");

        var result = RathenaScriptConfigGraph.ResolveActiveNpcFiles(_root, "npc/main.conf");

        Assert.Equal(["npc/shared.txt"], result);
    }
}

// Real-pinned-data assertions against the genuine legacy/rathena Renewal config graph (commit
// e985006171d2eb320ee512a653f4c83aea3d81b6) - proves the parser's real-world classification
// decisions, not merely synthetic-fixture correctness.
public sealed class RathenaScriptConfigGraphRealDataTests
{
    private static readonly Lazy<(string Root, IReadOnlyList<string> ActiveFiles)> LazyResolved = new(() =>
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.sln"))) directory = directory.Parent;
        var repositoryRoot = directory?.FullName ?? throw new DirectoryNotFoundException("Athena.NET repository root was not found.");
        var root = Path.Combine(repositoryRoot, "legacy/rathena");
        return (root, RathenaScriptConfigGraph.ResolveActiveNpcFiles(root));
    });

    private static IReadOnlyList<string> ActiveFiles => LazyResolved.Value.ActiveFiles;

    [Fact]
    public void RealRenewalGraph_DoesNotIncludeAcademy_PinnedDisabledAtScriptsMonstersConf()
    {
        Assert.DoesNotContain("npc/re/mobs/academy.txt", ActiveFiles);
    }

    [Fact]
    public void RealRenewalGraph_IncludesARealActiveRenewalMonsterFile()
    {
        Assert.Contains("npc/re/mobs/towns.txt", ActiveFiles);
    }

    [Theory]
    [InlineData("npc/events/christmas_2013.txt")]
    [InlineData("npc/events/halloween_2008.txt")]
    [InlineData("npc/events/halloween_2013.txt")]
    [InlineData("npc/events/RWC_2011.txt")]
    [InlineData("npc/events/StPatrick_2008.txt")]
    public void RealRenewalGraph_DoesNotIncludeCommentedDisabledEventFiles(string eventFile)
    {
        Assert.DoesNotContain(eventFile, ActiveFiles);
    }
}
