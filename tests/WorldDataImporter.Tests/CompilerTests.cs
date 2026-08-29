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
    public void CountItem_LowersAsGenericExpressionForAnyItemId()
    {
        // Arbitrary item ID (not Sailor's 6008) to prove genericity, mirroring the isbegin_quest
        // pattern this test suite already uses for generic quest-state expressions.
        const string source = "OnClick: if (countitem(1234) < 3) { mes \"few\"; } else mes \"plenty\";";
        var syntax = new RathenaParser(source, "fixture.txt").ParseCompilationUnit();
        var lowered = RathenaScriptLowerer.LowerEvent(syntax, "OnClick");
        Assert.True(lowered.Success, string.Join('\n', lowered.Diagnostics.Select(d => d.Message)));

        var metadata = new GeneratedNpcMetadata("Athena.Generated", "CountItemScript", "npc:test:countitem", "Npc", "CountItem", "test", 1, 2, 0, 45, 0, 0, "OnClick", null, "fixture.txt", 1, 1, "commit");
        var generated = NpcScriptEmitter.Emit(lowered.Script!, metadata);

        Assert.Contains("await context.CountItemAsync(1234, cancellationToken) < 3", generated);
    }

    [Fact]
    public void DelItem_LowersAsGenericCommandForAnyItemIdAndAmount()
    {
        var syntax = new RathenaParser("OnClick: delitem 7777,3;", "fixture.txt").ParseCompilationUnit();
        var lowered = RathenaScriptLowerer.LowerEvent(syntax, "OnClick");
        Assert.True(lowered.Success, string.Join('\n', lowered.Diagnostics.Select(d => d.Message)));
        Assert.Equal("delitem", Assert.IsType<LoweredCommand>(lowered.Script!.Statements.Single()).Name);

        var metadata = new GeneratedNpcMetadata("Athena.Generated", "DelItemScript", "npc:test:delitem", "Npc", "DelItem", "test", 1, 2, 0, 45, 0, 0, "OnClick", null, "fixture.txt", 1, 1, "commit");
        var generated = NpcScriptEmitter.Emit(lowered.Script, metadata);

        Assert.Contains("await context.DeleteItemAsync(7777, 3, cancellationToken);", generated);
    }

    [Fact]
    public void GetItem_LowersAsGenericCommandForAnyItemIdAndAmount()
    {
        var syntax = new RathenaParser("OnClick: getitem 9999,2;", "fixture.txt").ParseCompilationUnit();
        var lowered = RathenaScriptLowerer.LowerEvent(syntax, "OnClick");
        Assert.True(lowered.Success, string.Join('\n', lowered.Diagnostics.Select(d => d.Message)));
        Assert.Equal("getitem", Assert.IsType<LoweredCommand>(lowered.Script!.Statements.Single()).Name);

        var metadata = new GeneratedNpcMetadata("Athena.Generated", "GetItemScript", "npc:test:getitem", "Npc", "GetItem", "test", 1, 2, 0, 45, 0, 0, "OnClick", null, "fixture.txt", 1, 1, "commit");
        var generated = NpcScriptEmitter.Emit(lowered.Script, metadata);

        Assert.Contains("await context.GetItemAsync(9999, 2, cancellationToken);", generated);
    }

    [Theory]
    [InlineData("countitem();")]
    [InlineData("countitem(1,2);")]
    public void CountItem_InvalidArity_FailsAtCompileTime(string call)
    {
        var syntax = new RathenaParser($"OnClick: mes {call}", "fixture.txt").ParseCompilationUnit();
        var analysis = SemanticAnalyzer.Analyze(syntax);
        Assert.Contains(analysis.Diagnostics, d => d.Code == "RAT3002" && d.Message.Contains("countitem"));
    }

    [Theory]
    [InlineData("delitem 1;")]
    [InlineData("delitem 1,2,3;")]
    public void DelItem_InvalidArity_FailsAtCompileTime(string statement)
    {
        var syntax = new RathenaParser($"OnClick: {statement}", "fixture.txt").ParseCompilationUnit();
        var analysis = SemanticAnalyzer.Analyze(syntax);
        Assert.Contains(analysis.Diagnostics, d => d.Code == "RAT3002" && d.Message.Contains("delitem"));
    }

    [Theory]
    [InlineData("getitem 1;")]
    [InlineData("getitem 1,2,3;")]
    public void GetItem_InvalidArity_FailsAtCompileTime(string statement)
    {
        var syntax = new RathenaParser($"OnClick: {statement}", "fixture.txt").ParseCompilationUnit();
        var analysis = SemanticAnalyzer.Analyze(syntax);
        Assert.Contains(analysis.Diagnostics, d => d.Code == "RAT3002" && d.Message.Contains("getitem"));
    }

    [Fact]
    public void StrCharInfoName_LowersAsDistinctCharacterInfoAndEmitsInsideConcatenation()
    {
        var syntax = new RathenaParser("OnClick: mes \"[\" + strcharinfo(0) + \"]\"; mes \"I am \" + strcharinfo(0) + \"!\";", "fixture.txt").ParseCompilationUnit();
        var lowered = RathenaScriptLowerer.LowerEvent(syntax, "OnClick");
        Assert.True(lowered.Success, string.Join('\n', lowered.Diagnostics.Select(diagnostic => diagnostic.Message)));

        var expressions = lowered.Script!.Statements.Cast<LoweredCommand>().Select(command => command.Arguments.Single()).ToArray();
        Assert.All(expressions, expression => Assert.Contains(Flatten(expression), item => item is LoweredCharacterInfo { InfoType: 0 }));

        var metadata = new GeneratedNpcMetadata("Athena.Generated", "CharacterNameScript", "npc:test:name", "Npc", "Name", "test", 1, 2, 0, 45, 0, 0, "OnClick", null, "fixture.txt", 1, 1, "commit");
        var generated = NpcScriptEmitter.Emit(lowered.Script, metadata);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(generated, "context\\.StrCharInfo\\(0\\)").Count);
        Assert.Contains("\"I am \" + context.StrCharInfo(0)", generated);
    }

    [Theory]
    [InlineData("strcharinfo(1)")]
    [InlineData("strcharinfo(.@mode)")]
    public void StrCharInfoUnsupportedMode_FailsLoweringLoudly(string expression)
    {
        var syntax = new RathenaParser($"OnClick: mes {expression};", "fixture.txt").ParseCompilationUnit();
        var lowered = RathenaScriptLowerer.LowerEvent(syntax, "OnClick");

        Assert.False(lowered.Success);
        var diagnostic = Assert.Single(lowered.Diagnostics, item => item.Code == "RAT4004");
        Assert.Contains("Only strcharinfo(0)", diagnostic.Message);
    }

    [Fact]
    public void StandaloneSelect_EmitsContinuationAndDiscardsResult()
    {
        var syntax = new RathenaParser("OnClick: select(\"One\", \"Two\");", "fixture.txt").ParseCompilationUnit();
        var lowered = RathenaScriptLowerer.LowerEvent(syntax, "OnClick");
        Assert.True(lowered.Success);
        var metadata = new GeneratedNpcMetadata("Athena.Generated", "SelectScript", "npc:test:select", "Npc", "Select", "test", 1, 2, 0, 45, 0, 0, "OnClick", null, "fixture.txt", 1, 1, "commit");

        var generated = NpcScriptEmitter.Emit(lowered.Script!, metadata);

        Assert.Contains("await context.SelectAsync([\"One\", \"Two\"], cancellationToken);", generated);
    }

    [Fact]
    public void End_EmitsTerminatingReturnInsteadOfFallingThrough()
    {
        var syntax = new RathenaParser("OnClick: if (1) { close2; end; } mes \"unreachable on branch\";", "fixture.txt").ParseCompilationUnit();
        var lowered = RathenaScriptLowerer.LowerEvent(syntax, "OnClick");
        Assert.True(lowered.Success);
        var metadata = new GeneratedNpcMetadata("Athena.Generated", "EndScript", "npc:test:end", "Npc", "End", "test", 1, 2, 0, 45, 0, 0, "OnClick", null, "fixture.txt", 1, 1, "commit");

        var generated = NpcScriptEmitter.Emit(lowered.Script!, metadata);

        Assert.Contains("await context.Close2Async(cancellationToken);\n            return;", generated);
    }

    private static IEnumerable<LoweredScriptExpression> Flatten(LoweredScriptExpression expression)
    {
        yield return expression;
        if (expression is LoweredBinary binary)
        {
            foreach (var item in Flatten(binary.Left)) yield return item;
            foreach (var item in Flatten(binary.Right)) yield return item;
        }
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
                "--name", "Captain Carocc#intro_npc03", "--name", "Lumin#new_ship", "--name", "Sailor#intro_npc04",
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

            var luminScript = scriptFiles.Select(File.ReadAllText).Single(source => source.Contains("LuminOnClickScript"));
            Assert.Contains("new QuestId(7471)", luminScript);
            Assert.Contains("context.StrCharInfo(0)", luminScript);
            Assert.Contains("await context.SetNpcCloakAsync(null, true, cancellationToken);", luminScript);

            var sailorScript = scriptFiles.Select(File.ReadAllText).Single(source => source.Contains("SailorOnClickScript"));
            Assert.Contains("await context.CountItemAsync(6008, cancellationToken) < 2", sailorScript);
            Assert.Contains("await context.DeleteItemAsync(6008, 2, cancellationToken);", sailorScript);
            Assert.Contains("await context.GetItemAsync(611, 5, cancellationToken);", sailorScript);
            Assert.Contains("await context.GrantExperienceAsync(100, 100, cancellationToken);", sailorScript);
            Assert.Contains("new QuestId(21008)", sailorScript);
            Assert.DoesNotContain("questinfo", sailorScript);

            var academyNpcs = await File.ReadAllTextAsync(Path.Combine(academyDir, "AcademyNpcs.cs"));
            Assert.Contains("\"Wounded Swordsman#intro_npc02_iz_int\"", academyNpcs);
            Assert.Contains("CaptainCarocc = new(", academyNpcs);
            Assert.Contains("static () => new Athena.Net.MapServer.Generated.World.Izlude.Academy.Scripts.CaptainCaroccOnClickScript()", academyNpcs);
            Assert.Contains("Lumin = new(", academyNpcs);
            Assert.Contains("static () => new Athena.Net.MapServer.Generated.World.Izlude.Academy.Scripts.LuminOnClickScript()", academyNpcs);
            Assert.Contains("Sailor = new(", academyNpcs);
            Assert.Contains("static () => new Athena.Net.MapServer.Generated.World.Izlude.Academy.Scripts.SailorOnClickScript()", academyNpcs);

            var academyWarpTriggers = await File.ReadAllTextAsync(Path.Combine(academyDir, "AcademyWarpTriggers.cs"));
            Assert.Contains("\"#ship_out\"", academyWarpTriggers);
            Assert.Contains("\"#intro_to_izlude\"", academyWarpTriggers);

            var academyWorld = await File.ReadAllTextAsync(Path.Combine(academyDir, "AcademyWorld.cs"));
            Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(academyWorld, "warp:iz_int0.:ship_out0.").Count);
            Assert.Contains("warp:int_land04:intro_to_izlude_d", academyWorld);
            // Every base+duplicate placement (int_land plus int_land01..04) must be present -
            // matching the same "no --exclude-placement for a duplicate() family's generic
            // template row" convention already proven for Captain Carocc/Lumin above.
            Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(academyWorld, "npc:int_land0?.?:sailor#intro_npc04").Count);
            Assert.Contains("\"npc:int_land:sailor#intro_npc04\"", academyWorld);
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
    public async Task CharacterData_IsDeterministicHasExactFileSetAndMatchesCheckedInOutput()
    {
        var repository = FindRepositoryRoot();
        var firstDir = Path.Combine(Path.GetTempPath(), $"character-data-{Guid.NewGuid():N}");
        var secondDir = Path.Combine(Path.GetTempPath(), $"character-data-{Guid.NewGuid():N}");
        try
        {
            string[] Arguments(string output) => ["compile-character-data", "--rathena-root", Path.Combine(repository, "legacy/rathena"), "--rathena-commit", "e985006171d2eb320ee512a653f4c83aea3d81b6", "--output", output];
            Assert.Equal(0, await WorldDataImporterCli.RunAsync(Arguments(firstDir)));
            Assert.Equal(0, await WorldDataImporterCli.RunAsync(Arguments(secondDir)));
            static string[] Files(string root) => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
            var expected = new[] { "Jobs/GeneratedJobRegistry.cs", "Progression/GeneratedProgressionData.cs", "Progression/GeneratedProgressionRegistry.cs", "Skills/GeneratedSkillRegistry.cs", "Skills/GeneratedSkillTreeRegistry.cs", "Skills/GeneratedSkillTrees.cs" };
            Assert.Equal(expected, Files(firstDir)); Assert.Equal(expected, Files(secondDir));
            var checkedIn = Path.Combine(repository, "src/MapServer/Generated");
            foreach (var relative in expected)
            {
                Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(firstDir, relative)), await File.ReadAllBytesAsync(Path.Combine(secondDir, relative)));
                Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(checkedIn, relative)), await File.ReadAllBytesAsync(Path.Combine(firstDir, relative)));
            }
        }
        finally { Directory.Delete(firstDir, recursive: true); Directory.Delete(secondDir, recursive: true); }
    }

    [Fact]
    public void CharacterDataCompiler_CompilesRealPinnedCoverageAndNoviceFacts()
    {
        var repository = FindRepositoryRoot();
        var root = Path.Combine(repository, "legacy/rathena");
        var generated = CompileCharacterData(root);
        // job_basepoints.yml's 24 fourth-job classes (Dragon_Knight, Meister, Windhawk, etc.,
        // Jobs-block starting at line 19264) declare only BaseAp - no BaseHp/BaseSp rows.
        // Pinned JobDatabase::loadingFinished (src/map/pc.cpp) resolves every base level
        // whose base_hp[j]/base_sp[j] was never set through calc_basehp/calc_basesp instead
        // of leaving it absent; all 175 generated jobs now get complete progression data
        // (previously only 147 did, because the compiler required non-null BaseHp/BaseSp
        // tables instead of falling back to the formula). Unique value sets rose from 67 to
        // 89 because these newly-resolved HP/SP curves are genuinely distinct per job, then to
        // 134 once MaxBaseStat (ResolveJobParameterCategory's ported pc_maxparameter job-
        // category cap - src/map/pc.cpp:14335-14407) joined the HashKey: jobs that previously
        // hashed identically on HP/SP/stat-point/job-bonus curves alone (e.g. an ordinary 2-1
        // job and its "2" gender-variant id) now also differ whenever their pinned stat caps
        // differ, which is the common case across the Normal/Trans/Third/ThirdTrans/Baby/
        // BabyThird/Extended/Fourth/Summoner categories.
        Assert.Equal(new CharacterDataCounts(194, 175, 175, 134, 1635, 175, 175), generated.Counts);
        var progression = generated.Artifacts.Single(item => item.RelativePath == "Progression/GeneratedProgressionData.cs").Source;
        Assert.Contains("Novice = new(JobClass.Novice, 99, 10, [0, 548, 894, 1486", progression);
        Assert.Contains("[0, 10, 18, 28", progression);
        Assert.Contains("[0, 40, 45, 50", progression);
        Assert.Contains("rAthena commit: e985006171d2eb320ee512a653f4c83aea3d81b6", progression);

        // Novice (0) is JobParameterCategory.Normal (ordinary MAPID_NOVICE, no JOBL_BABY/
        // THIRD/UPPER bit and not one of the Summoner/Kagerou/Oboro/Rebellion special mapid
        // ranges) => conf/battle/player.conf's max_parameter = 99, the trailing MaxBaseStat arg.
        var noviceLine = progression.Split('\n').Single(line => line.Contains("Novice = new(JobClass.Novice, ", StringComparison.Ordinal));
        Assert.EndsWith(", 99);", noviceLine.TrimEnd());

        // Dragon_Knight (4252) regression anchor for the HP/SP formula fix: its job_stats.yml
        // block declares HpFactor 68, HpIncrease 5828, SpFactor 7, SpIncrease 14 with no
        // Ninja/Gunslinger/Summoner/Super Novice mapid adjustment, giving calc_basehp/
        // calc_basesp(1) = 93/10, (2) = 152/10, (3) = 212/10 exactly.
        Assert.Contains("DragonKnight = new(JobClass.DragonKnight, ", progression);
        var dragonKnightLine = progression.Split('\n').Single(line => line.Contains("DragonKnight = new(JobClass.DragonKnight, ", StringComparison.Ordinal));
        Assert.Contains("[0, 93, 152, 212, ", dragonKnightLine);

        // Dragon_Knight is JobParameterCategory.Fourth (pc_is_primary_fourth's
        // MAPID_DRAGON_KNIGHT..MAPID_SHADOW_CROSS range) => max_fourth_parameter = 130.
        Assert.EndsWith(", 130);", dragonKnightLine.TrimEnd());
    }

    // Parity fixtures for CharacterDataCompiler.ResolveJobParameterCategory/
    // JobParameterCategoryMaxStat - one representative generated job per distinct pinned
    // pc_maxparameter category (src/map/pc.cpp:14335-14407), verified against
    // conf/battle/player.conf's shipped max_*_parameter values. Job/category pairing:
    //   Novice        -> Normal      (ordinary MAPID_NOVICE)                  -> 99
    //   NoviceHigh     -> Trans       (JOBL_UPPER, no JOBL_THIRD)              -> 99
    //   RuneKnight     -> Third       (JOBL_THIRD, primary 3-1 range)          -> 130
    //   RuneKnightT    -> ThirdTrans  (JOBL_THIRD|JOBL_UPPER)                  -> 130
    //   Baby           -> Baby        (JOBL_BABY, no JOBL_THIRD)               -> 80
    //   BabyRuneKnight -> BabyThird   (JOBL_BABY|JOBL_THIRD)                   -> 117
    //   Kagerou        -> Extended    (MAPID_SECONDMASK == MAPID_KAGEROUOBORO) -> 130
    //   Summoner       -> Summoner    (MAPID_FIRSTMASK == MAPID_SUMMONER)      -> 130
    //   DragonKnight   -> Fourth      (pc_is_primary_fourth)                   -> 130
    [Theory]
    [InlineData("Novice", 99)]
    [InlineData("NoviceHigh", 99)]
    [InlineData("RuneKnight", 130)]
    [InlineData("RuneKnightT", 130)]
    [InlineData("Baby", 80)]
    [InlineData("BabyRuneKnight", 117)]
    [InlineData("Kagerou", 130)]
    [InlineData("Summoner", 130)]
    [InlineData("DragonKnight", 130)]
    public void CharacterDataCompiler_ResolvesPinnedMaxBaseStatPerJobParameterCategory(string jobIdentifier, ushort expectedMaxBaseStat)
    {
        var root = Path.Combine(FindRepositoryRoot(), "legacy/rathena");
        var progression = CompileCharacterData(root).Artifacts.Single(item => item.RelativePath == "Progression/GeneratedProgressionData.cs").Source;
        var line = progression.Split('\n').Single(entry => entry.Contains($" {jobIdentifier} = new(JobClass.{jobIdentifier}, ", StringComparison.Ordinal));
        Assert.EndsWith($", {expectedMaxBaseStat});", line.TrimEnd());
    }

    // Baby_Summoner is the one pinned edge case where two of ResolveJobParameterCategory's
    // special-cased mapid ranges could both apply (JOBL_BABY is set AND MAPID_FIRSTMASK ==
    // MAPID_SUMMONER) - pinned pc.cpp:14340-14350 checks JOBL_BABY first ("Always check
    // babies first"), so this must resolve to max_baby_parameter (80), never
    // max_summoner_parameter (130).
    [Fact]
    public void CharacterDataCompiler_BabySummonerPrefersBabyCategoryOverSummoner()
    {
        var root = Path.Combine(FindRepositoryRoot(), "legacy/rathena");
        var progression = CompileCharacterData(root).Artifacts.Single(item => item.RelativePath == "Progression/GeneratedProgressionData.cs").Source;
        var line = progression.Split('\n').Single(entry => entry.Contains(" BabySummoner = new(JobClass.BabySummoner, ", StringComparison.Ordinal));
        Assert.EndsWith(", 80);", line.TrimEnd());
    }

    // Every generated JobClass must resolve a MaxBaseStat deterministically -
    // ResolveJobParameterCategory throws for an id with no pinned pc_jobid2mapid case rather
    // than silently defaulting, so simply completing Compile() without throwing already
    // proves full coverage of the 175 generated jobs; this test additionally confirms every
    // emitted definition line actually carries a trailing numeric MaxBaseStat argument.
    [Fact]
    public void CharacterDataCompiler_EveryGeneratedJobResolvesAMaxBaseStat()
    {
        var root = Path.Combine(FindRepositoryRoot(), "legacy/rathena");
        var generated = CompileCharacterData(root);
        var progression = generated.Artifacts.Single(item => item.RelativePath == "Progression/GeneratedProgressionData.cs").Source;
        var definitionLines = progression.Split('\n').Where(line => line.Contains("internal static readonly CharacterProgressionDefinition ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(175, definitionLines.Length);
        foreach (var line in definitionLines)
            Assert.Matches(@", \d+\);\s*$", line);
    }

    // Proves MaxBaseStat genuinely comes from the SUPPLIED conf/battle/player.conf source, not
    // a compiler-side constant: changing exactly one max_*_parameter value in a synthetic conf
    // source (max_fourth_parameter 130 -> 135, DragonKnight's own category - see the parity
    // Theory above) changes ONLY the generated jobs in that category, without touching
    // CharacterDataCompiler.cs. This is the direct counter-test to PR #18's review finding: if
    // the values were still hardcoded in the compiler, this edit would have no effect on the
    // generated output.
    [Fact]
    public void CharacterDataCompiler_MaxBaseStatComesFromSuppliedPlayerConfig_NotCompilerConstants()
    {
        var root = Path.Combine(FindRepositoryRoot(), "legacy/rathena");
        var sources = ReadCharacterData(root);
        var mutatedConfig = ReplaceFirst(sources.PlayerConfig, "max_fourth_parameter: 130", "max_fourth_parameter: 135");

        var baseline = CharacterDataCompiler.Compile(sources, "commit");
        var mutated = CharacterDataCompiler.Compile(sources with { PlayerConfig = mutatedConfig }, "commit");

        var baselineProgression = baseline.Artifacts.Single(item => item.RelativePath == "Progression/GeneratedProgressionData.cs").Source;
        var mutatedProgression = mutated.Artifacts.Single(item => item.RelativePath == "Progression/GeneratedProgressionData.cs").Source;

        // Fourth-job category (max_fourth_parameter): DragonKnight's cap changes 130 -> 135.
        var baselineDragonKnight = baselineProgression.Split('\n').Single(line => line.Contains(" DragonKnight = new(JobClass.DragonKnight, ", StringComparison.Ordinal));
        var mutatedDragonKnight = mutatedProgression.Split('\n').Single(line => line.Contains(" DragonKnight = new(JobClass.DragonKnight, ", StringComparison.Ordinal));
        Assert.EndsWith(", 130);", baselineDragonKnight.TrimEnd());
        Assert.EndsWith(", 135);", mutatedDragonKnight.TrimEnd());

        // A job in an untouched category (Normal, max_parameter) must be completely unaffected.
        var baselineNovice = baselineProgression.Split('\n').Single(line => line.Contains(" Novice = new(JobClass.Novice, ", StringComparison.Ordinal));
        var mutatedNovice = mutatedProgression.Split('\n').Single(line => line.Contains(" Novice = new(JobClass.Novice, ", StringComparison.Ordinal));
        Assert.Equal(baselineNovice, mutatedNovice);

        // Provenance: both the generated data and registry files now cite the conf source.
        Assert.Contains("conf/battle/player.conf", mutatedProgression);
    }

    // ParsePlayerConfigMaxParameters must fail generation loudly, not silently default, for
    // every malformed-config shape task section 35's "fail loudly" requirement covers.
    [Theory]
    [InlineData("max_parameter: 99\nmax_third_parameter: 130\nmax_third_trans_parameter: 130\nmax_baby_parameter: 80\nmax_baby_third_parameter: 117\nmax_extended_parameter: 130\nmax_summoner_parameter: 130\nmax_fourth_parameter: 130\n", "missing")] // max_trans_parameter entirely absent
    [InlineData("max_parameter: 99\nmax_trans_parameter: 99\nmax_trans_parameter: 100\nmax_third_parameter: 130\nmax_third_trans_parameter: 130\nmax_baby_parameter: 80\nmax_baby_third_parameter: 117\nmax_extended_parameter: 130\nmax_summoner_parameter: 130\nmax_fourth_parameter: 130\n", "more than once")] // duplicated key
    [InlineData("max_parameter: ninety-nine\nmax_trans_parameter: 99\nmax_third_parameter: 130\nmax_third_trans_parameter: 130\nmax_baby_parameter: 80\nmax_baby_third_parameter: 117\nmax_extended_parameter: 130\nmax_summoner_parameter: 130\nmax_fourth_parameter: 130\n", "malformed")] // non-numeric value
    [InlineData("max_parameter: 0\nmax_trans_parameter: 99\nmax_third_parameter: 130\nmax_third_trans_parameter: 130\nmax_baby_parameter: 80\nmax_baby_third_parameter: 117\nmax_extended_parameter: 130\nmax_summoner_parameter: 130\nmax_fourth_parameter: 130\n", "malformed")] // zero value
    [InlineData("max_parameter: 99999999\nmax_trans_parameter: 99\nmax_third_parameter: 130\nmax_third_trans_parameter: 130\nmax_baby_parameter: 80\nmax_baby_third_parameter: 117\nmax_extended_parameter: 130\nmax_summoner_parameter: 130\nmax_fourth_parameter: 130\n", "malformed")] // outside ushort range
    public void CharacterDataCompiler_MalformedPlayerConfig_FailsGenerationLoudly(string malformedConfig, string expectedMessageFragment)
    {
        var root = Path.Combine(FindRepositoryRoot(), "legacy/rathena");
        var sources = ReadCharacterData(root) with { PlayerConfig = malformedConfig };
        var error = Assert.Throws<ArgumentException>(() => CharacterDataCompiler.Compile(sources, "commit"));
        Assert.Contains(expectedMessageFragment, error.Message, StringComparison.Ordinal);
        Assert.Contains("conf/battle/player.conf", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CharacterDataCompiler_GeneratesRealPinnedPerLevelAndScalarRange()
    {
        // Real pinned-data regression for skill_db.yml's Range field, which is genuinely
        // level-dependent exactly like Requires.SpCost: a bare scalar (SM_BASH, SM_PROVOKE), an
        // explicit per-level "- Level: N / Size: X" sequence (KN_SPEARBOOMERANG), or absent
        // entirely (NV_BASIC). Before this fix, the compiler only recognized the scalar form and
        // silently generated Range=[] (effectively 0) for every per-level skill, including
        // KN_SPEARBOOMERANG which is reachable through the real generated Knight tree - a genuine
        // supported-job bug, not unused source data.
        var root = Path.Combine(FindRepositoryRoot(), "legacy/rathena");
        var generated = CompileCharacterData(root);
        var registry = generated.Artifacts.Single(item => item.RelativePath == "Skills/GeneratedSkillRegistry.cs").Source;

        Assert.Contains("new(1, \"NV_BASIC\", [], [], ", registry); // no Range field at all -> empty
        Assert.Contains("new(5, \"SM_BASH\", [8, 8, 8, 8, 8, 15, 15, 15, 15, 15], [-1, -1, -1, -1, -1, -1, -1, -1, -1, -1], ", registry); // scalar Range: -1, expanded across MaxLevel 10
        Assert.Contains("new(6, \"SM_PROVOKE\", [4, 5, 6, 7, 8, 9, 10, 11, 12, 13], [9, 9, 9, 9, 9, 9, 9, 9, 9, 9], ", registry); // scalar Range: 9, expanded across MaxLevel 10
        Assert.Contains("new(59, \"KN_SPEARBOOMERANG\", [10, 10, 10, 10, 10], [3, 5, 7, 9, 11], ", registry); // real per-level Range, NOT generated zero
    }

    [Fact]
    public void CharacterDataCompiler_GeneratesRealPinnedRangeFlagsForVultureModifiedSkill()
    {
        // AC_DOUBLE (real generated Archer/Hunter tree member) has Flags.AlterRangeVulture: true
        // and a scalar Range: -9 - proves the range-modifier source flags are captured alongside
        // the scalar/per-level Range parsing, not merely the numeric value.
        var root = Path.Combine(FindRepositoryRoot(), "legacy/rathena");
        var generated = CompileCharacterData(root);
        var registry = generated.Artifacts.Single(item => item.RelativePath == "Skills/GeneratedSkillRegistry.cs").Source;

        var acDoubleLine = registry.Split('\n').Single(line => line.Contains("\"AC_DOUBLE\"", StringComparison.Ordinal));
        Assert.Contains("[-9, -9, -9, -9, -9, -9, -9, -9, -9, -9]", acDoubleLine);
        Assert.EndsWith("new(true, false, false, false, false)),", acDoubleLine.TrimStart());

        // AC_VULTURE itself carries no range-altering flags of its own.
        var acVultureLine = registry.Split('\n').Single(line => line.Contains("\"AC_VULTURE\"", StringComparison.Ordinal));
        Assert.EndsWith("new(false, false, false, false, false)),", acVultureLine.TrimStart());
    }

    [Fact]
    public void CharacterDataCompiler_ResolvesMissingBaseHpSpThroughPinnedFormulaFallback()
    {
        // Synthetic fixture: Swordman's job_basepoints.yml block declares BaseHp/BaseSp only
        // for levels 1-2 (a sparse table, mirroring how the 24 real fourth-job classes in
        // db/re/job_basepoints.yml declare none at all), and its job_stats.yml block sets
        // explicit HpFactor/HpIncrease/SpFactor/SpIncrease. Every level the table omits must
        // resolve through JobDatabase::calc_basehp/calc_basesp (src/map/pc.cpp), not error
        // out and not silently stay zero.
        var root = Path.Combine(FindRepositoryRoot(), "legacy/rathena");
        var sources = ReadCharacterData(root);
        // Strip every real "Swordman: true" membership line first so only the synthetic
        // block below governs Swordman - the real job_basepoints.yml/job_stats.yml both
        // declare additional Swordman blocks later in the file that would otherwise overlay
        // (last-row-wins) on top of this fixture's rows.
        var isolatedBasePoints = sources.JobBasePoints.Replace("      Swordman: true\n", "", StringComparison.Ordinal);
        var isolatedStats = sources.JobStats.Replace("      Swordman: true\n", "", StringComparison.Ordinal);
        var sparseBasePoints = ReplaceFirst(isolatedBasePoints,
            "  - Jobs:\n      Novice: true",
            "  - Jobs:\n      Swordman: true\n    BaseHp:\n      - Level: 1\n        Hp: 999\n    BaseSp:\n      - Level: 1\n        Sp: 999\n  - Jobs:\n      Novice: true");
        var explicitFactors = ReplaceFirst(isolatedStats,
            "  - Jobs:\n      Novice: true",
            "  - Jobs:\n      Swordman: true\n    HpFactor: 100\n    HpIncrease: 200\n    SpFactor: 10\n    SpIncrease: 50\n  - Jobs:\n      Novice: true");
        var generated = CharacterDataCompiler.Compile(sources with { JobBasePoints = sparseBasePoints, JobStats = explicitFactors }, "commit");
        var progression = generated.Artifacts.Single(item => item.RelativePath == "Progression/GeneratedProgressionData.cs").Source;
        var swordmanLine = progression.Split('\n').Single(line => line.Contains("Swordman = new(JobClass.Swordman, ", StringComparison.Ordinal));

        // Level 1 keeps the explicit table row (999) - table always wins over formula.
        // Level 2 has no table row, so it must resolve via calc_basehp/calc_basesp:
        //   base_hp = 35 + floor(2*200/100) + floor(100/100*2+0.5) = 35+4+2 = 41
        //   base_sp = 10 + floor(2*50/100) + floor(10/100*2+0.5)   = 10+1+0 = 11
        Assert.Contains("[0, 999, 41,", swordmanLine);
        Assert.Contains("[0, 999, 11,", swordmanLine);
    }

    [Fact]
    public void CharacterDataCompiler_JobStatsFactorsOnlyResetToDefaultOnFirstBlockForJob()
    {
        // Pinned JobDatabase::parseBodyNode's "exists" flag (src/map/pc.cpp): a job_id's
        // hp_factor/hp_increase/sp_factor/sp_increase only reset to the rAthena defaults
        // (0/500/0/100) the FIRST time that job_id is seen across job_stats.yml's repeated
        // Jobs blocks. A LATER block that omits a field must leave the earlier explicit value
        // untouched, not silently reset it. This fixture gives Swordman an explicit HpFactor
        // in its first block, then a second block (shared with Novice) that sets only
        // SpFactor - HpFactor set earlier must survive into the second block untouched.
        var root = Path.Combine(FindRepositoryRoot(), "legacy/rathena");
        var sources = ReadCharacterData(root);
        var isolatedBasePoints = sources.JobBasePoints.Replace("      Swordman: true\n", "", StringComparison.Ordinal);
        var isolatedStats = sources.JobStats.Replace("      Swordman: true\n", "", StringComparison.Ordinal);
        var sparseBasePoints = ReplaceFirst(isolatedBasePoints,
            "  - Jobs:\n      Novice: true",
            "  - Jobs:\n      Swordman: true\n    BaseHp:\n      - Level: 1\n        Hp: 1\n    BaseSp:\n      - Level: 1\n        Sp: 1\n  - Jobs:\n      Novice: true");
        var twoBlockFactors = ReplaceFirst(isolatedStats,
            "  - Jobs:\n      Novice: true",
            "  - Jobs:\n      Swordman: true\n    HpFactor: 300\n  - Jobs:\n      Swordman: true\n    SpFactor: 20\n  - Jobs:\n      Novice: true");
        var generated = CharacterDataCompiler.Compile(sources with { JobBasePoints = sparseBasePoints, JobStats = twoBlockFactors }, "commit");
        var progression = generated.Artifacts.Single(item => item.RelativePath == "Progression/GeneratedProgressionData.cs").Source;
        var swordmanLine = progression.Split('\n').Single(line => line.Contains("Swordman = new(JobClass.Swordman, ", StringComparison.Ordinal));

        // Level 2 (no table row): base_hp = 35 + floor(2*500/100) + floor(300/100*2+0.5) = 35+10+6 = 51
        // (HpIncrease keeps its untouched default of 500; HpFactor 300 survives from block 1).
        // base_sp = 10 + floor(2*100/100) + floor(20/100*2+0.5) = 10+2+0 = 12
        // (SpIncrease keeps its untouched default of 100; SpFactor 20 set in block 2).
        Assert.Contains("[0, 1, 51,", swordmanLine);
        Assert.Contains("[0, 1, 12,", swordmanLine);
    }

    [Fact]
    public void CharacterDataCompiler_UnknownSkillFailsWithContext()
    {
        var repository = FindRepositoryRoot();
        var root = Path.Combine(repository, "legacy/rathena");
        var sources = ReadCharacterData(root) with { SkillTree = File.ReadAllText(Path.Combine(root, "db/re/skill_tree.yml")).Replace("NV_BASIC", "NV_NOT_REAL", StringComparison.Ordinal) };
        var error = Assert.Throws<ArgumentException>(() => CharacterDataCompiler.Compile(sources, "commit"));
        Assert.Contains("NV_NOT_REAL", error.Message); Assert.Contains("Novice", error.Message);
    }

    [Fact]
    public void CharacterDataCompiler_UnknownInheritedJobFailsWithContext()
    {
        var repository = FindRepositoryRoot();
        var root = Path.Combine(repository, "legacy/rathena");
        var sources = ReadCharacterData(root) with { SkillTree = File.ReadAllText(Path.Combine(root, "db/re/skill_tree.yml")).Replace("      Novice: true", "      Missing_Job: true", StringComparison.Ordinal) };
        var error = Assert.Throws<ArgumentException>(() => CharacterDataCompiler.Compile(sources, "commit"));
        Assert.Contains("inherits unknown job 'Missing_Job'", error.Message);
    }

    [Fact]
    public void CharacterDataCompiler_DuplicateSkillIdFailsLoudly()
    {
        var root = Path.Combine(FindRepositoryRoot(), "legacy/rathena");
        var sources = ReadCharacterData(root) with { SkillDatabase = ReplaceFirst(File.ReadAllText(Path.Combine(root, "db/re/skill_db.yml")), "  - Id: 2\n", "  - Id: 1\n") };
        Assert.Contains("duplicate skill ID 1", Assert.Throws<ArgumentException>(() => CharacterDataCompiler.Compile(sources, "commit")).Message);
    }

    [Fact]
    public void CharacterDataCompiler_InheritanceCycleFailsWithJobsInDiagnostic()
    {
        var root = Path.Combine(FindRepositoryRoot(), "legacy/rathena");
        var sources = ReadCharacterData(root) with { SkillTree = ReplaceFirst(File.ReadAllText(Path.Combine(root, "db/re/skill_tree.yml")), "  - Job: Swordman\n    Inherit:\n      Novice: true", "  - Job: Swordman\n    Inherit:\n      Knight: true") };
        var message = Assert.Throws<ArgumentException>(() => CharacterDataCompiler.Compile(sources, "commit")).Message;
        Assert.Contains("inheritance cycle", message); Assert.Contains("Swordman", message); Assert.Contains("Knight", message);
    }

    private static CharacterDataCompilation CompileCharacterData(string root) => CharacterDataCompiler.Compile(ReadCharacterData(root), "e985006171d2eb320ee512a653f4c83aea3d81b6");
    private static CharacterDataSources ReadCharacterData(string root) => new(
        File.ReadAllText(Path.Combine(root, "src/common/mmo.hpp")), File.ReadAllText(Path.Combine(root, "src/map/script_constants.hpp")),
        File.ReadAllText(Path.Combine(root, "db/re/job_exp.yml")), File.ReadAllText(Path.Combine(root, "db/re/job_basepoints.yml")),
        File.ReadAllText(Path.Combine(root, "db/re/job_stats.yml")), File.ReadAllText(Path.Combine(root, "db/re/statpoint.yml")),
        File.ReadAllText(Path.Combine(root, "db/re/skill_db.yml")), File.ReadAllText(Path.Combine(root, "db/re/skill_tree.yml")),
        File.ReadAllText(Path.Combine(root, "conf/battle/player.conf")));
    private static string ReplaceFirst(string source, string oldValue, string newValue)
    {
        var index = source.IndexOf(oldValue, StringComparison.Ordinal);
        if (index < 0) throw new InvalidOperationException($"Fixture text '{oldValue}' was not found.");
        return source[..index] + newValue + source[(index + oldValue.Length)..];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Athena.NET repository root was not found.");
    }
}
