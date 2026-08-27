using Athena.WorldCompiler.Rathena.Syntax;

namespace Athena.WorldCompiler.Semantics;

internal enum CompilerSupportStage { Unparsed, Parsed, SemanticallyResolved, Lowerable, Generatable, RuntimeSupported, FullySupported }
internal sealed record CommandDefinition(string Name, int MinimumArity, int? MaximumArity, bool CanSuspend, bool RequiresPlayer, bool RuntimeSupported);
internal static class RathenaCommandCatalog
{
    private static readonly IReadOnlyDictionary<string,CommandDefinition> Commands = new CommandDefinition[]
    {
        new("mes",1,1,false,true,true), new("next",0,0,true,true,true), new("select",1,null,true,true,true),
        new("close",0,0,false,true,true),new("close2",0,0,false,true,true),new("end",0,0,false,false,true),
        new("setquest",1,1,false,true,true),new("completequest",1,1,false,true,true),new("isbegin_quest",1,1,false,true,true),
        new("warp",3,3,false,true,true),new("savepoint",3,5,true,true,true),new("strnpcinfo",1,1,false,false,true),new("strcharinfo",1,1,false,true,true),new("replacestr",3,3,false,false,true),new("cutin",2,2,false,true,true),
        new("heal",2,2,false,true,true),new("specialeffect2",1,2,false,true,true),new("skilleffect",2,2,false,true,true),new("sc_start",3,9,false,true,true),new("getexp",2,2,false,true,true),
        new("goto",1,1,false,false,false),new("callsub",1,null,true,false,false),new("callfunc",1,null,true,false,false)
    }.ToDictionary(x=>x.Name,StringComparer.OrdinalIgnoreCase);
    public static bool TryResolve(string name,out CommandDefinition definition)=>Commands.TryGetValue(name,out definition!);
}

internal sealed record SemanticOccurrence(string Name,string Kind,CompilerSupportStage Stage,SourceSpan Span,string? BlockingReason);
internal sealed record SemanticAnalysis(IReadOnlyList<SemanticOccurrence> Occurrences,IReadOnlyList<CompilerDiagnostic> Diagnostics);
internal static class SemanticAnalyzer
{
    public static SemanticAnalysis Analyze(CompilationUnitSyntax unit)
    {
        var found=new List<SemanticOccurrence>(); var diagnostics=new List<CompilerDiagnostic>();
        foreach(var statement in Descendants(unit.Statements))
        {
            if(statement is CommandStatementSyntax command) Resolve(command.Name,"Command",command.Arguments.Count,command.Span);
            foreach(var call in Expressions(statement).OfType<CallExpressionSyntax>())
                if(call.Target is IdentifierExpressionSyntax id) Resolve(id.Name,"Function",call.Arguments.Count,call.Span);
        }
        return new(found,diagnostics);
        void Resolve(string name,string kind,int arity,SourceSpan span)
        {
            if (name is "if" or "else" or "switch" or "case" or "default" or "while" or "for" or "break" or "continue" or "return") return;
            if(!RathenaCommandCatalog.TryResolve(name,out var definition)) { found.Add(new(name,kind,CompilerSupportStage.Parsed,span,"Unknown command/function semantic")); diagnostics.Add(new("RAT3001","Warning",$"Command or function '{name}' is parsed but has no semantic definition.",span,name)); return; }
            if(arity<definition.MinimumArity || definition.MaximumArity is int max && arity>max) { found.Add(new(name,kind,CompilerSupportStage.Parsed,span,"Invalid arity")); diagnostics.Add(new("RAT3002","Error",$"'{name}' does not accept {arity} argument(s).",span,name)); return; }
            found.Add(new(name,kind,definition.RuntimeSupported?CompilerSupportStage.FullySupported:CompilerSupportStage.SemanticallyResolved,span,definition.RuntimeSupported?null:"Runtime/lowering capability missing"));
        }
    }
    private static IEnumerable<StatementSyntax> Descendants(IEnumerable<StatementSyntax> values)
    {
        foreach(var value in values){yield return value; foreach(var child in value switch { BlockStatementSyntax b=>Descendants(b.Statements),IfStatementSyntax i=>Descendants(i.Else is null?[i.Then]:[i.Then,i.Else]),WhileStatementSyntax w=>Descendants([w.Body]),ForStatementSyntax f=>Descendants([f.Body]),SwitchStatementSyntax s=>Descendants(s.Clauses.SelectMany(c=>c.Statements)),_=>[]})yield return child;}
    }
    private static IEnumerable<ExpressionSyntax> Expressions(StatementSyntax s)
    {
        var roots=s switch {ExpressionStatementSyntax e=>[e.Expression],CommandStatementSyntax c=>c.Arguments,IfStatementSyntax i=>[i.Condition],WhileStatementSyntax w=>[w.Condition],ForStatementSyntax f=>new[]{f.Initializer,f.Condition,f.Increment}.OfType<ExpressionSyntax>().ToArray(),SwitchStatementSyntax sw=>[sw.Expression],ReturnStatementSyntax r when r.Expression is not null=>[r.Expression],_=>[]};
        return roots.SelectMany(ExpressionTree);
    }
    private static IEnumerable<ExpressionSyntax> ExpressionTree(ExpressionSyntax e){yield return e; foreach(var child in e switch {UnaryExpressionSyntax u=>[u.Operand],BinaryExpressionSyntax b=>[b.Left,b.Right],AssignmentExpressionSyntax a=>[a.Target,a.Value],CallExpressionSyntax c=>new[]{c.Target}.Concat(c.Arguments),IndexExpressionSyntax i=>[i.Target,i.Index],PostfixExpressionSyntax p=>[p.Operand],ConditionalExpressionSyntax c=>[c.Condition,c.WhenTrue,c.WhenFalse],_=>[]})foreach(var nested in ExpressionTree(child))yield return nested;}
}
