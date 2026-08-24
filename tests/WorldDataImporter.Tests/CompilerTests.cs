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
        var world=new LoweredWorld([new(new("warp:a:x"),"#x",new("a"),1,2,1,1,new("b"),3,4)]);
        var first=CSharpWorldEmitter.Emit(world,"abc"); var second=CSharpWorldEmitter.Emit(world,"abc");
        Assert.Equal(first,second); Assert.Contains("WorldBuildInfo",first); Assert.Contains("RathenaCommit = \"abc\"",first); Assert.Contains("readonly record struct WarpData",first);
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
    public async Task RealIntroToIzlude_GenerationIsDeterministicAndMatchesCompiledSource()
    {
        var repository = FindRepositoryRoot();
        var first = Path.Combine(Path.GetTempPath(), $"intro-{Guid.NewGuid():N}.g.cs");
        var second = Path.Combine(Path.GetTempPath(), $"intro-{Guid.NewGuid():N}.g.cs");
        try
        {
            string[] Arguments(string output) => ["compile-script", "--source-root", Path.Combine(repository, "legacy/rathena/npc/re/warps"), "--source-file", "cities/izlude.txt", "--map", "int_land04", "--name", "#intro_to_izlude_d", "--kind", "warp", "--output", output];
            Assert.Equal(0, await WorldDataImporterCli.RunAsync(Arguments(first)));
            Assert.Equal(0, await WorldDataImporterCli.RunAsync(Arguments(second)));
            Assert.Equal(await File.ReadAllBytesAsync(first), await File.ReadAllBytesAsync(second));
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(repository, "src/MapServer/Generated/World/Izlude/IntroToIzlude.g.cs")), await File.ReadAllBytesAsync(first));
        }
        finally
        {
            File.Delete(first); File.Delete(second);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Athena.NET repository root was not found.");
    }
}
