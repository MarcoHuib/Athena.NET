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
        Assert.Contains(touch.Blockers!, item => item.Feature == "sleep2" && item.Stage == FailureStage.Lowering);
        Assert.True(touch.SourceContext!.Text.Count <= 3);
        var two = Assert.Single(first.Unsupported, item => item.EntityName == "Two blockers");
        Assert.Contains(two.Blockers!, item => item.Feature == "sleep2");
        Assert.Contains(two.Blockers!, item => item.Feature == "callfunc");
        Assert.Equal(1, Assert.Single(first.Blockers, item => item.Feature == "sleep2").SoleBlockerFor);
        Assert.Equal(0, Assert.Single(first.Blockers, item => item.Feature == "callfunc").SoleBlockerFor);
        Assert.Contains(first.Summary.Categories, item => item.Category == "warp" && item.Compatible == 1);
        Assert.Contains(first.Summary.Categories, item => item.Category == "shop" && item.NotYetAnalyzed == 1);
        Assert.Contains(first.Summary.Categories, item => item.Category == "function" && item.NotYetAnalyzed == 1);
        Assert.Contains(first.Dependencies, item => item.Dependencies.Contains("quest:1001"));

        var jsonl = await File.ReadAllLinesAsync(Path.Combine(fixture.Output, "unsupported.jsonl"));
        Assert.Equal(first.Unsupported.Count, jsonl.Length);
        Assert.All(jsonl, line => Assert.NotEqual(default, System.Text.Json.JsonDocument.Parse(line).RootElement));
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
}
