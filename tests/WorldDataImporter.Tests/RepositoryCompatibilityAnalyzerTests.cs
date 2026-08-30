public sealed class RepositoryCompatibilityAnalyzerTests
{
    [Fact]
    public async Task AnalyzesEventsAggregatesSoleBlockersAndWritesDeterministicReports()
    {
        using var fixture = new AnalysisFixture("""
            prontera,10,10,4	script	Compatible	100,{
            mes "hello";
            setquest 1001;
            close;
            }
            prontera,11,10,4	script	Mixed	100,{
            mes "click";
            close;
            OnTouch:
            sleep2 1000;
            end;
            }
            prontera,12,10,4	script	Two blockers	100,{
            sleep2 1000;
            callfunc "Missing";
            }
            prontera,20,20,0	warp	Test Warp	1,1,izlude,10,10
            prontera,30,30,0	shop	Test Shop	100,501:-1
            prontera,40,40,0	monster	Poring	1002,1,0,0
            prontera	mapflag	noteleport
            function	script	TestFunction	{
            end;
            }
            """);
        var options = new AnalysisOptions(fixture.Directory, fixture.Output, 1);

        var first = RepositoryCompatibilityAnalyzer.Analyze(options);
        var second = RepositoryCompatibilityAnalyzer.Analyze(options);
        await RepositoryCompatibilityAnalyzer.WriteAsync(options, first);

        Assert.Equal(DeterministicJson.Serialize(first), DeterministicJson.Serialize(second));
        Assert.Contains(first.Compatible, item => item.EntityName == "Compatible" && item.Event == "OnClick");
        Assert.Contains(first.Compatible, item => item.EntityName == "Mixed" && item.Event == "OnClick");
        var touch = Assert.Single(first.Unsupported, item => item.EntityName == "Mixed" && item.Event == "OnTouch");
        Assert.Equal("fixture.txt", touch.SourceFile);
        Assert.Equal(6, touch.SourceLine);
        Assert.Contains(touch.Blockers!, item => item.Feature == "timer:sleep2" && item.CompilerConstruct == "CommandStatementSyntax" && item.Stage == FailureStage.Lowering);
        Assert.True(touch.SourceContext!.Text.Count <= 3);
        var two = Assert.Single(first.Unsupported, item => item.EntityName == "Two blockers");
        Assert.Contains(two.Blockers!, item => item.Feature == "timer:sleep2");
        Assert.Contains(two.Blockers!, item => item.Feature == "function:callfunc");
        Assert.Equal(1, Assert.Single(first.Blockers, item => item.Feature == "timer:sleep2").SoleBlockerFor);
        Assert.Equal(0, Assert.Single(first.Blockers, item => item.Feature == "function:callfunc").SoleBlockerFor);
        Assert.Contains(first.Summary.Categories, item => item.Category == "warp" && item.Compatible == 1);
        Assert.Contains(first.Summary.Categories, item => item.Category == "shop" && item.NotYetAnalyzed == 1);
        Assert.Contains(first.Summary.Categories, item => item.Category == "function" && item.NotYetAnalyzed == 1);
        Assert.Contains(first.Dependencies, item => item.Dependencies.Contains("quest:1001"));

        var jsonl = await File.ReadAllLinesAsync(Path.Combine(fixture.Output, "unsupported.jsonl"));
        Assert.Equal(first.Unsupported.Count, jsonl.Length);
        Assert.All(jsonl, line => Assert.NotEqual(default, System.Text.Json.JsonDocument.Parse(line).RootElement));
        Assert.Contains("\"NpcDefinitions\"", await File.ReadAllTextAsync(Path.Combine(fixture.Output, "summary.json")), StringComparison.Ordinal);
        Assert.Contains("## NPC definitions", await File.ReadAllTextAsync(Path.Combine(fixture.Output, "report.md")), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(fixture.Output, "*.cs", SearchOption.AllDirectories));
        Assert.Equal(fixture.Original, await File.ReadAllTextAsync(fixture.Source));
    }

    [Fact]
    public void FailureInOneNpcDoesNotAbortOtherNpcAndDuplicateIsPreservedAsFamilyDeclaration()
    {
        using var fixture = new AnalysisFixture("""
            -	script	::BaseNpc	100,{
            sleep2 1;
            }
            prontera,10,10,4	duplicate(BaseNpc)	CopyNpc	100
            prontera,11,10,4	script	GoodNpc	100,{
            mes "ok";
            close;
            }
            """);

        var result = RepositoryCompatibilityAnalyzer.Analyze(new(fixture.Directory, fixture.Output));

        Assert.Single(result.Unsupported, item => item.EntityName == "BaseNpc");
        Assert.Single(result.Compatible, item => item.EntityName == "GoodNpc");
        Assert.Contains(result.Summary.Categories, item => item.Category == "duplicate" && item.NotApplicable == 1);
    }

    [Fact]
    public void NormalizesSyntaxFunctionsVariablesAndOperatorsWithoutNearestFeatureGuessing()
    {
        using var fixture = new AnalysisFixture("""
            prontera,1,1,4	script	WhileNpc	100,{
            while (1) { next; }
            }
            prontera,2,1,4	script	FunctionNpc	100,{
            rand(1);
            }
            prontera,3,1,4	script	VariablesNpc	100,{
            #account = 1;
            $server = 2;
            }
            prontera,4,1,4	script	AndNpc	100,{
            if (1 && 1) mes "x";
            }
            prontera,5,1,4	script	OrNpc	100,{
            if (1 || 1) mes "x";
            }
            prontera,6,1,4	script	BitNpc	100,{
            if (1 & 1) mes "x";
            }
            prontera,7,1,4	script	ForNpc	100,{
            for (.@i = 0; .@i < 1; .@i++) next;
            }
            prontera,8,1,4	script	ArrayNpc	100,{
            .@x = #account[0];
            }
            prontera,9,1,4	script	UnaryNpc	100,{
            .@x = ~1;
            }
            prontera,10,1,4	script	DirectNpc	100,{
            disablenpc "Foo";
            enablenpc "Foo";
            donpcevent "Foo::OnBar";
            mapannounce "prontera", "hello";
            questinfo 1001;
            initnpctimer;
            ##global = 1;
            }
            """);

        var result = RepositoryCompatibilityAnalyzer.Analyze(new(fixture.Directory, fixture.Output));

        AssertFeatures("WhileNpc", "control-flow:while");
        Assert.DoesNotContain(Assert.Single(result.Unsupported, item => item.EntityName == "WhileNpc").Blockers!, item => item.Feature == "command:next");
        AssertFeatures("FunctionNpc", "function:rand");
        Assert.Equal("CallExpressionSyntax", Assert.Single(result.Unsupported, item => item.EntityName == "FunctionNpc").Blockers!.Single().CompilerConstruct);
        AssertFeatures("VariablesNpc", "variable:account", "variable:server-global");
        AssertFeatures("AndNpc", "operator:logical-and");
        Assert.Equal("BinaryExpressionSyntax", Assert.Single(result.Unsupported, item => item.EntityName == "AndNpc").Blockers!.Single().CompilerConstruct);
        AssertFeatures("OrNpc", "operator:logical-or");
        AssertFeatures("BitNpc", "operator:bit-and");
        AssertFeatures("ForNpc", "control-flow:for");
        AssertFeatures("ArrayNpc", "array:index");
        AssertFeatures("UnaryNpc", "operator:bit-not");
        AssertFeatures("DirectNpc", "announcement:mapannounce", "npc:disable", "npc:enable", "npc:event-dispatch", "quest:info", "timer:initnpctimer", "variable:account-global");
        Assert.All(result.WorkItems, item => CompatibilityDiagnosticNormalizer.Validate(item.Feature));
        Assert.DoesNotContain(result.WorkItems, item => item.Feature.Contains("syntax", StringComparison.OrdinalIgnoreCase) || item.Feature.Contains("exception", StringComparison.OrdinalIgnoreCase));

        void AssertFeatures(string npc, params string[] expected)
        {
            var actual = Assert.Single(result.Unsupported, item => item.EntityName == npc).Blockers!.Select(item => item.Feature).Distinct().ToArray();
            Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void AggregatesRepeatedNormalizedBlockersAndCalculatesSoleBlockersFromDistinctCapabilities()
    {
        using var fixture = new AnalysisFixture("""
            prontera,1,1,4	script	Repeated	100,{
            while (1) { next; }
            while (1) { next; }
            while (1) { next; }
            }
            prontera,2,1,4	script	Multiple	100,{
            while (1) { next; }
            #account = 1;
            }
            """);

        var result = RepositoryCompatibilityAnalyzer.Analyze(new(fixture.Directory, fixture.Output));
        var loop = Assert.Single(result.Blockers, item => item.Feature == "control-flow:while");
        Assert.Equal(4, loop.Occurrences);
        Assert.Equal(2, loop.AffectedEntities);
        Assert.Equal(1, loop.SoleBlockerFor);
        Assert.Equal(0, Assert.Single(result.Blockers, item => item.Feature == "variable:account").SoleBlockerFor);
    }

    [Fact]
    public void SummarizesNpcDefinitionsIndependentlyFromEvents()
    {
        using var fixture = new AnalysisFixture("""
            prontera,1,1,4	script	Full	100,{
            mes "ok"; close;
            }
            prontera,2,1,4	script	Partial	100,{
            mes "ok"; close;
            OnTouch:
            sleep2 1;
            }
            prontera,3,1,4	script	None	100,{
            while (1) { next; }
            }
            """);

        var summary = RepositoryCompatibilityAnalyzer.Analyze(new(fixture.Directory, fixture.Output)).Summary.NpcDefinitions;

        Assert.Equal(3, summary.Total);
        Assert.Equal(1, summary.FullyCompatible);
        Assert.Equal(1, summary.PartiallyCompatible);
        Assert.Equal(1, summary.Unsupported);
        Assert.Equal(0, summary.NotApplicable);
    }

    [Fact]
    public void RuntimeScopeExcludesDocumentationWhileAllScopeIncludesIt()
    {
        using var fixture = new DirectoryFixture();
        fixture.Write("npc/real.txt", """
            prontera,1,1,4	script	Real	100,{
            mes "ok"; close;
            }
            """);
        fixture.Write("doc/sample/example.txt", """
            prontera,2,1,4	script	Sample	100,{
            sleep2 1;
            }
            """);

        var runtime = RepositoryCompatibilityAnalyzer.Analyze(new(fixture.Directory, fixture.Output));
        var all = RepositoryCompatibilityAnalyzer.Analyze(new(fixture.Directory, fixture.Output, Scope: AnalysisScope.All));

        Assert.Equal(1, runtime.Summary.FilesAnalyzed);
        Assert.DoesNotContain(runtime.Unsupported, item => item.EntityName == "Sample");
        Assert.Equal(2, all.Summary.FilesAnalyzed);
        Assert.Contains(all.Unsupported, item => item.EntityName == "Sample");
    }

    private sealed class AnalysisFixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "athena-analysis-" + Guid.NewGuid().ToString("N"));
        public string Output => Path.Combine(Directory, "reports");
        public string Source => Path.Combine(Directory, "fixture.txt");
        public string Original { get; }
        public AnalysisFixture(string source)
        {
            System.IO.Directory.CreateDirectory(Directory);
            Original = source.Replace("\r\n", "\n");
            File.WriteAllText(Source, Original);
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }

    private sealed class DirectoryFixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "athena-analysis-tree-" + Guid.NewGuid().ToString("N"));
        public string Output => Path.Combine(Directory, "reports");
        public DirectoryFixture() => System.IO.Directory.CreateDirectory(Directory);
        public void Write(string relativePath, string text)
        {
            var path = Path.Combine(Directory, relativePath); System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text);
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
