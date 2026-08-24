using Athena.WorldCompiler.Generation;
using Athena.WorldCompiler.Rathena;
using Athena.WorldCompiler.Rathena.Syntax;
using Athena.WorldCompiler.Semantics;

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
}
