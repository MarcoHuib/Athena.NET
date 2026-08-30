using Athena.WorldCompiler.Lowering;
using Athena.WorldCompiler.Generation;
using Athena.WorldCompiler.Rathena;
using Athena.WorldCompiler.Rathena.Syntax;
using Athena.WorldCompiler.Semantics;

namespace Athena.WorldCompiler;

internal sealed record RathenaEventCompilation(
    string EventName, LoweredNpcScript? Script,
    IReadOnlyList<SemanticOccurrence> Features,
    IReadOnlyList<CompilerDiagnostic> Diagnostics)
{
    public bool Success => Script is not null && Diagnostics.All(item => item.Severity != "Error");
}

/// <summary>The event-compilation boundary shared by generation and compatibility analysis.</summary>
internal static class RathenaEventCompiler
{
    public static (CompilationUnitSyntax Syntax, SemanticAnalysis Semantics) Parse(string rawScriptBody, WorldSourceInfo source)
    {
        var text = rawScriptBody.TrimEnd();
        if (text.EndsWith('}')) text = text[..^1];
        var syntax = new RathenaParser(text, source.File, source.Line + 1).ParseCompilationUnit();
        return (syntax, SemanticAnalyzer.Analyze(syntax));
    }

    public static RathenaEventCompilation Compile(CompilationUnitSyntax syntax, SemanticAnalysis semantics, string eventName)
    {
        var lowered = RathenaScriptLowerer.LowerEvent(syntax, eventName);
        var labels = syntax.Statements.OfType<LabelStatementSyntax>().Where(item => item.IsEvent).ToArray();
        var label = labels.FirstOrDefault(item => item.Name.Equals(eventName, StringComparison.OrdinalIgnoreCase));
        var start = label?.Span.Start.Offset ?? (eventName.Equals("OnClick", StringComparison.OrdinalIgnoreCase) ? syntax.Span.Start.Offset : int.MaxValue);
        var end = labels.Where(item => item.Span.Start.Offset > start).Select(item => item.Span.Start.Offset).DefaultIfEmpty(syntax.Span.End.Offset + 1).Min();
        bool InEvent(SourceSpan span) => span.Start.Offset >= start && span.Start.Offset < end;
        var generationDiagnostics = new List<CompilerDiagnostic>();
        var hasEarlierErrors = syntax.Diagnostics.Concat(semantics.Diagnostics).Any(item => item.Severity == "Error" && InEvent(item.Span));
        if (lowered.Success && !hasEarlierErrors)
        {
            try
            {
                _ = NpcScriptEmitter.EmitScriptClass(lowered.Script!,
                    new("Athena.WorldCompiler.Analysis", "CompatibilityProbe", "analysis", "analysis", syntax.Span.Start.File, syntax.Span.Start.Line, "analysis"),
                    new(eventName, syntax.Span.Start.File, syntax.Span.Start.Line, syntax.Span.Start.Line), "CompatibilityProbe");
            }
            catch (Exception exception)
            {
                generationDiagnostics.Add(new("RAT5001", "Error", exception.Message, lowered.Script!.Span, exception.GetType().Name));
            }
        }
        var diagnostics = syntax.Diagnostics.Concat(semantics.Diagnostics).Concat(lowered.Diagnostics).Concat(generationDiagnostics)
            .Where(item => item.Code is "RAT4001" or "RAT5001" || InEvent(item.Span))
            .DistinctBy(item => (item.Code, item.Message, item.Span.Start.File, item.Span.Start.Line, item.Span.Start.Column))
            .OrderBy(item => item.Span.Start.Line).ThenBy(item => item.Span.Start.Column).ThenBy(item => item.Code, StringComparer.Ordinal)
            .ToArray();
        return new(eventName, lowered.Script, semantics.Occurrences.Where(item => InEvent(item.Span)).ToArray(), diagnostics);
    }
}
