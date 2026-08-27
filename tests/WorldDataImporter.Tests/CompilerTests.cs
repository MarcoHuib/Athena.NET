using Athena.WorldCompiler.Generation;
using Athena.WorldCompiler.Rathena;
using Athena.WorldCompiler.Rathena.Syntax;
using Athena.WorldCompiler.Semantics;
using Athena.WorldCompiler.Lowering;

public sealed class CompilerTests
{
    [Fact]
    public void Lexer_RecognizesVariablesEscapesCommentsOperatorsAndLocations()
    {
        var lexer=new RathenaLexer("// ignored\n.@name$ += \"a\\n\\\"b\"; ##account++;", "npc/test.txt", 10);
        var tokens=lexer.Lex().Where(t=>t.Kind!=TokenKind.EndOfFile).ToArray();
        Assert.Equal([TokenKind.Variable,TokenKind.PlusAssign,TokenKind.String,TokenKind.Semicolon,TokenKind.Variable,TokenKind.PlusPlus,TokenKind.Semicolon],tokens.Select(t=>t.Kind));
        Assert.Equal((11,1),(tokens[0].Span.Start.Line,tokens[0].Span.Start.Column));
        Assert.Equal("a\n\"b",tokens[2].StringValue); Assert.Empty(lexer.Diagnostics);
    }

    [Fact]
    public void Parser_UsesPrecedenceAndRepresentsBroadControlFlow()
    {
        var unit=new RathenaParser("OnTouch: .@a = 1 + 2 * 3; if (.@a >= 7) { mes \"ok\"; } else close; switch(.@a){ case 7: break; default: goto Done; } while(.@a) .@a--; for(.@i=0;.@i<2;.@i++) continue; Done: return;", "test.txt").ParseCompilationUnit();
        Assert.DoesNotContain(unit.Diagnostics,d=>d.Severity=="Error");
        Assert.IsType<LabelStatementSyntax>(unit.Statements[0]);
        var assignment=Assert.IsType<AssignmentExpressionSyntax>(Assert.IsType<ExpressionStatementSyntax>(unit.Statements[1]).Expression);
        var plus=Assert.IsType<BinaryExpressionSyntax>(assignment.Value); Assert.Equal(TokenKind.Plus,plus.Operator); Assert.Equal(TokenKind.Star,Assert.IsType<BinaryExpressionSyntax>(plus.Right).Operator);
        Assert.Contains(unit.Statements,s=>s is IfStatementSyntax); Assert.Contains(unit.Statements,s=>s is SwitchStatementSyntax); Assert.Contains(unit.Statements,s=>s is WhileStatementSyntax); Assert.Contains(unit.Statements,s=>s is ForStatementSyntax); Assert.Contains(unit.Statements,s=>s is LabelStatementSyntax { Name:"Done" });
    }

    [Fact]
    public void Parser_ReportsMalformedSyntaxWithSourceLocation()
    {
        var unit=new RathenaParser("if (1 { mes \"x\";", "bad.txt", 20).ParseCompilationUnit();
        var issue=unit.Diagnostics.First(d=>d.Code=="RAT2002"); Assert.Equal("bad.txt",issue.Span.Start.File); Assert.True(issue.Span.Start.Line>=20);
    }

    [Fact]
    public void SemanticAnalysis_DistinguishesKnownAndUnknownCalls()
    {
        var unit=new RathenaParser("mes \"hello\"; mystery(1);", "test.txt").ParseCompilationUnit(); var analysis=SemanticAnalyzer.Analyze(unit);
        Assert.Contains(analysis.Occurrences,x=>x.Name=="mes"&&x.Stage==CompilerSupportStage.FullySupported);
        Assert.Contains(analysis.Occurrences,x=>x.Name=="mystery"&&x.Stage==CompilerSupportStage.Parsed);
        Assert.Contains(analysis.Diagnostics,x=>x.Code=="RAT3001");
    }

    [Fact]
    public void GeneratedWarpCSharp_IsDeterministicAndCarriesProvenance()
    {
        var world=new LoweredWorld([new(new("warp:a:x"),"#x",new("a"),1,2,1,1,new("b"),3,4,true,"npc/test.txt",1)]);
        var first=CSharpWorldEmitter.Emit(world,"abc"); var second=CSharpWorldEmitter.Emit(world,"abc");
        Assert.Equal(first,second); Assert.Contains("WorldBuildInfo",first); Assert.Contains("RathenaCommit = \"abc\"",first); Assert.Contains("WarpDefinition[] All",first);
    }

    [Fact]
    public void GotoAndLabels_ArePlannedAsGeneratedStateMachine()
    {
        var syntax=new RathenaParser("Start: next; goto Start;","flow.txt").ParseCompilationUnit();
        var plan=ScriptControlFlowLowerer.Plan(syntax);
        Assert.Equal(ScriptControlFlowShape.StateMachine,plan.Shape);
        Assert.Contains("async suspension",plan.Reason);
    }

    [Fact]
    public void GeneratedExecutionSubset_LowersCommandsAssignmentsAndIfElse()
    {
        const string source = "OnTouch: mes \"Hello\"; next; if (isbegin_quest(1) == 0) { setquest 1; } else completequest 1; .@map$ = \"map\" + replacestr(strnpcinfo(2), \"npc\", \"\"); warp .@map$,1,2; savepoint .@map$,3,4; close2; close;";
        var syntax = new RathenaParser(source, "fixture.txt").ParseCompilationUnit();
        var result = RathenaScriptLowerer.LowerEvent(syntax, "OnTouch");

        Assert.True(result.Success, string.Join('\n', result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Collection(result.Script!.Statements,
            statement => Assert.Equal("mes", Assert.IsType<LoweredCommand>(statement).Name),
            statement => Assert.Equal("next", Assert.IsType<LoweredCommand>(statement).Name),
            statement =>
            {
                var conditional = Assert.IsType<LoweredIf>(statement);
                Assert.Equal("setquest", Assert.IsType<LoweredCommand>(Assert.IsType<LoweredBlock>(conditional.Then).Statements.Single()).Name);
                Assert.Equal("completequest", Assert.IsType<LoweredCommand>(conditional.Else).Name);
            },
            statement => Assert.Equal(".@map$", Assert.IsType<LoweredAssignment>(statement).Variable),
            statement => Assert.Equal("warp", Assert.IsType<LoweredCommand>(statement).Name),
            statement => Assert.Equal("savepoint", Assert.IsType<LoweredCommand>(statement).Name),
            statement => Assert.Equal("close2", Assert.IsType<LoweredCommand>(statement).Name),
            statement => Assert.True(Assert.IsType<LoweredCommand>(statement).Terminates));
    }

    [Fact]
    public void ExecutableNpcEmitter_IsDeterministicAndSourceMapped()
    {
        var syntax = new RathenaParser("OnTouch: mes \"Welcome\"; next; close;", "legacy/rathena/npc/test.txt", 40).ParseCompilationUnit();
        var lowered = RathenaScriptLowerer.LowerEvent(syntax, "OnTouch").Script!;
        var metadata = new GeneratedNpcMetadata("Athena.Generated", "WelcomeScript", "npc:test:welcome", "Npc", "Welcome", "test", 1, 2, 0, 45, 0, 0, "OnTouch", null, "legacy/rathena/npc/test.txt", 40, 39, "commit");

        var first = NpcScriptEmitter.Emit(lowered, metadata);
        var second = NpcScriptEmitter.Emit(lowered, metadata);

        Assert.Equal(first, second);
        Assert.Contains("#line 40 \"legacy/rathena/npc/test.txt\"", first);
        Assert.Contains("await context.NextAsync(cancellationToken);", first);
        Assert.Contains("static () => new Athena.Generated.WelcomeScript()", first);
        Assert.DoesNotContain("ScriptInstructionDefinition", first);
    }

    [Fact]
    public void GetExp_LowersToExecutableRuntimeCapability()
    {
        var syntax = new RathenaParser("OnClick: getexp 600,600;", "legacy/rathena/npc/test.txt", 10).ParseCompilationUnit();
        var lowered = RathenaScriptLowerer.LowerEvent(syntax, "OnClick");
        Assert.True(lowered.Success);
        var metadata = new GeneratedNpcMetadata("Athena.Generated", "ExperienceScript", "npc:test:experience", "Npc", "Experience", "test", 1, 2, 0, 45, 0, 0, "OnClick", null, "legacy/rathena/npc/test.txt", 10, 9, "commit");

        var generated = NpcScriptEmitter.Emit(lowered.Script!, metadata);

        Assert.Contains("await context.GrantExperienceAsync(600, 600, cancellationToken);", generated);
        Assert.DoesNotContain("ScriptInstructionDefinition", generated);
    }

    [Fact]
    public async Task RealAcademyWorld_GenerationIsDeterministicAndMatchesCompiledAcademyTree()
    {
        var repository = FindRepositoryRoot();
        var first = Path.Combine(Path.GetTempPath(), $"academy-{Guid.NewGuid():N}");
        var second = Path.Combine(Path.GetTempPath(), $"academy-{Guid.NewGuid():N}");
        try
        {
            string[] Arguments(string outputDir) =>
            [
                "compile-npc-world",
                "--source-root", Path.Combine(repository, "legacy/rathena/npc/re/jobs/novice"),
                "--source-root", Path.Combine(repository, "legacy/rathena/npc/re/warps/cities"),
                "--name", "Wounded Swordsman#intro_npc02_iz_int", "--name", "Wounded Swordsman#intro_npc01_iz_int",
                "--name", "Captain Carocc#intro_npc03", "--name", "Lumin#new_ship",
                "--no-behavior", "Lumin#new_ship",
                "--warp-name", "#ship_out", "--warp-name", "#intro_to_izlude",
                "--namespace", "Athena.Net.MapServer.Generated.World.Izlude.Academy",
                "--rathena-commit", "e985006171d2eb320ee512a653f4c83aea3d81b6",
                "--output-dir", outputDir,
            ];
            Assert.Equal(0, await WorldDataImporterCli.RunAsync(Arguments(first)));
            Assert.Equal(0, await WorldDataImporterCli.RunAsync(Arguments(second)));

            var academyDir = Path.Combine(repository, "src/MapServer/Generated/World/Izlude/Academy");
            foreach (var relative in new[] { "AcademyNpcs.cs", "AcademyWorld.cs", "AcademyWarpTriggers.cs" })
            {
                var checkedIn = await File.ReadAllBytesAsync(Path.Combine(academyDir, relative));
                var firstRun = await File.ReadAllBytesAsync(Path.Combine(first, relative));
                var secondRun = await File.ReadAllBytesAsync(Path.Combine(second, relative));
                Assert.Equal(firstRun, secondRun);
                Assert.Equal(checkedIn, firstRun);
            }

            var scriptFiles = Directory.EnumerateFiles(Path.Combine(academyDir, "Scripts"), "*.cs").OrderBy(f => f, StringComparer.Ordinal).ToArray();
            var firstScriptFiles = Directory.EnumerateFiles(Path.Combine(first, "Scripts"), "*.cs").OrderBy(f => f, StringComparer.Ordinal).ToArray();
            Assert.Equal(scriptFiles.Select(Path.GetFileName), firstScriptFiles.Select(Path.GetFileName));
            foreach (var (checkedInPath, firstRunPath) in scriptFiles.Zip(firstScriptFiles))
                Assert.Equal(await File.ReadAllBytesAsync(checkedInPath), await File.ReadAllBytesAsync(firstRunPath));

            var woundedSwordsmanScript = scriptFiles.Select(File.ReadAllText).Single(source => source.Contains("context.CutinAsync(\"tutorial02\", (byte)4"));
            Assert.Contains("OnClickScript", woundedSwordsmanScript);

            var shipOutScript = scriptFiles.Select(File.ReadAllText).Single(source => source.Contains("\"ship_out\", \"\")"));
            Assert.Contains("await context.WarpAsync(local_map, 85, 107, cancellationToken);", shipOutScript);

            var introToIzludeScript = scriptFiles.Select(File.ReadAllText).Single(source => source.Contains("\"intro_to_izlude\", \"\")"));
            Assert.Contains("await context.WarpAsync(local_map, 196, 209, cancellationToken);", introToIzludeScript);

            var captainCaroccScript = scriptFiles.Select(File.ReadAllText).Single(source => source.Contains("CaptainCaroccOnClickScript"));
            Assert.Contains("await context.HealAsync(9999, 0, cancellationToken);", captainCaroccScript);
            Assert.Contains("await context.SpecialEffectAsync(RathenaConstants.EF_HEAL2, cancellationToken);", captainCaroccScript);
            Assert.Contains("await context.SkillEffectAsync(34, 0, cancellationToken);", captainCaroccScript);
            Assert.Contains("await context.StartStatusAsync(RathenaConstants.SC_BLESSING, 240000, 10, cancellationToken);", captainCaroccScript);
            Assert.Contains("await context.StartStatusAsync(RathenaConstants.SC_INCREASEAGI, 240000, 10, cancellationToken);", captainCaroccScript);
            Assert.Contains("await context.GrantExperienceAsync(600, 600, cancellationToken);", captainCaroccScript);

            var academyNpcs = await File.ReadAllTextAsync(Path.Combine(academyDir, "AcademyNpcs.cs"));
            Assert.Contains("\"Wounded Swordsman#intro_npc02_iz_int\"", academyNpcs);
            Assert.Contains("CaptainCarocc = new(", academyNpcs);
            Assert.Contains("static () => new Athena.Net.MapServer.Generated.World.Izlude.Academy.Scripts.CaptainCaroccOnClickScript()", academyNpcs);
            Assert.Contains("Lumin = new(", academyNpcs);

            var academyWarpTriggers = await File.ReadAllTextAsync(Path.Combine(academyDir, "AcademyWarpTriggers.cs"));
            Assert.Contains("\"#ship_out\"", academyWarpTriggers);
            Assert.Contains("\"#intro_to_izlude\"", academyWarpTriggers);

            var academyWorld = await File.ReadAllTextAsync(Path.Combine(academyDir, "AcademyWorld.cs"));
            Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(academyWorld, "warp:iz_int0.:ship_out0.").Count);
            Assert.Contains("warp:int_land04:intro_to_izlude_d", academyWorld);
        }
        finally { Directory.Delete(first, true); Directory.Delete(second, true); }
    }

    [Fact]
    public async Task MinimalIzIntWarps_AreDeterministicAndMatchCompiledSource()
    {
        var repository = FindRepositoryRoot();
        var first = Path.Combine(Path.GetTempPath(), $"required-warps-{Guid.NewGuid():N}.cs");
        var second = Path.Combine(Path.GetTempPath(), $"required-warps-{Guid.NewGuid():N}.cs");
        try
        {
            string[] Arguments(string output) => ["compile", "--source-root", Path.Combine(repository, "legacy/rathena/npc/re/warps/cities"), "--source-file", "izlude.txt",
                "--name", "#room_out", "--name", "#room_in", "--name", "#room_out01", "--name", "#room_in01", "--name", "#room_out02", "--name", "#room_in02",
                "--name", "#room_out03", "--name", "#room_in03", "--name", "#room_out04", "--name", "#room_in04", "--kind", "warp",
                "--rathena-commit", "6e6bca69b8a2ee03cd744cbc7a78a054a6f376ca", "--output", output];
            Assert.Equal(0, await WorldDataImporterCli.RunAsync(Arguments(first)));
            Assert.Equal(0, await WorldDataImporterCli.RunAsync(Arguments(second)));
            Assert.Equal(await File.ReadAllBytesAsync(first), await File.ReadAllBytesAsync(second));
            var checkedIn = await File.ReadAllTextAsync(Path.Combine(repository, "src/MapServer/Generated/World/Izlude/Academy/AcademyWarps.cs"));
            var generated = (await File.ReadAllTextAsync(first)).Replace(
                "namespace Athena.Net.MapServer.Generated.World.Izlude;",
                "namespace Athena.Net.MapServer.Generated.World.Izlude.Academy;", StringComparison.Ordinal);
            Assert.Equal(checkedIn, generated);
        }
        finally { File.Delete(first); File.Delete(second); }
    }

    // compile-script (the single-file WARPNPC path) stays available for standalone use, but #ship_out's
    // checked-in representation now lives only inside the area-grouped Academy tree (AcademyWarpTriggers.cs
    // + Scripts/ShipOutOnTouchScript.cs, one shared class for all 5 placements) - proven by
    // RealAcademyWorld_GenerationIsDeterministicAndMatchesCompiledAcademyTree above. This test instead
    // proves compile-script's independent lowering of the SAME pinned #ship_out03 duplicate produces the
    // identical executable body as the Academy-emitted shared class, i.e. the underlying lowering logic
    // for this content is unchanged by the Definition/Placement/Behavior split.
    [Fact]
    public async Task RealShipOut03OnTouch_MatchesTheSharedAcademyBehaviorBody()
    {
        var repository = FindRepositoryRoot();
        var first = Path.Combine(Path.GetTempPath(), $"ship-out03-{Guid.NewGuid():N}.cs");
        try
        {
            var arguments = new[] { "compile-script", "--source-root", Path.Combine(repository, "legacy/rathena/npc/re/warps/cities"), "--source-file", "izlude.txt", "--map", "iz_int03", "--name", "#ship_out03", "--kind", "warp", "--rathena-commit", "e985006171d2eb320ee512a653f4c83aea3d81b6", "--output", first };
            Assert.Equal(0, await WorldDataImporterCli.RunAsync(arguments));

            var standalone = await File.ReadAllTextAsync(first);
            var standaloneBody = ExecuteAsyncBody(standalone);

            var academyScript = Directory.EnumerateFiles(Path.Combine(repository, "src/MapServer/Generated/World/Izlude/Academy/Scripts"), "*.cs")
                .Select(File.ReadAllText).Single(source => source.Contains("\"ship_out\", \"\")"));
            var academyBody = ExecuteAsyncBody(academyScript);

            Assert.Equal(academyBody, standaloneBody);
        }
        finally { File.Delete(first); }
    }

    private static string ExecuteAsyncBody(string source)
    {
        var start = source.IndexOf("#line", StringComparison.Ordinal);
        var end = source.IndexOf("#line default", StringComparison.Ordinal);
        var body = source[start..end];
        return body[(body.IndexOf('\n') + 1)..].Trim();
    }

    [Fact]
    public async Task NoviceProgression_IsDeterministicAndMatchesCompiledSource()
    {
        var repository = FindRepositoryRoot();
        var first = Path.Combine(Path.GetTempPath(), $"novice-progression-{Guid.NewGuid():N}.cs");
        var second = Path.Combine(Path.GetTempPath(), $"novice-progression-{Guid.NewGuid():N}.cs");
        try
        {
            string[] Arguments(string output) => ["compile-progression", "--rathena-root", Path.Combine(repository, "legacy/rathena"), "--output", output];
            Assert.Equal(0, await WorldDataImporterCli.RunAsync(Arguments(first)));
            Assert.Equal(0, await WorldDataImporterCli.RunAsync(Arguments(second)));
            Assert.Equal(await File.ReadAllBytesAsync(first), await File.ReadAllBytesAsync(second));
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(repository, "src/MapServer/Generated/Progression/NoviceProgression.cs")), await File.ReadAllBytesAsync(first));
        }
        finally { File.Delete(first); File.Delete(second); }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Athena.NET repository root was not found.");
    }
}
