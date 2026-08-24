using Athena.WorldCompiler.Rathena;
using Athena.WorldCompiler.Semantics;

internal sealed record CapabilityLocation(string File, int Line, int Column = 1);
internal sealed record CommandCapability(string Command, int Count, string Status, IReadOnlyList<CapabilityLocation> Locations, string? BlockingReason = null);
internal sealed record ConversionCapabilityReport(int Files, int NpcDefinitions, int Duplicates, int Scripts, IReadOnlyList<CommandCapability> Commands, IReadOnlyList<string> ParseErrors);

internal static class CapabilityReporter
{
    public static ConversionCapabilityReport Scan(IEnumerable<string> roots)
    {
        var rootArray=roots.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var files=rootArray.SelectMany(r=>Directory.EnumerateFiles(r,"*.txt",SearchOption.AllDirectories)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var declarations=RathenaSourceParser.Parse(rootArray); var occurrences=new List<SemanticOccurrence>(); var errors=new List<string>();
        foreach(var script in declarations.Where(d=>d.Directive=="script"&&d.ScriptBody.Length>0))
        {
            var source=script.ScriptBody.TrimEnd();
            if(source.EndsWith('}')) source=source[..^1]; // declaration wrapper; nested blocks remain syntax
            var unit=new RathenaParser(source,script.Source.File,script.Source.Line+1).ParseCompilationUnit();
            errors.AddRange(unit.Diagnostics.Where(d=>d.Severity=="Error").Select(d=>$"{d.Span.Start.File}:{d.Span.Start.Line}:{d.Span.Start.Column} {d.Code}: {d.Message}"));
            occurrences.AddRange(SemanticAnalyzer.Analyze(unit).Occurrences);
        }
        var commands=occurrences.GroupBy(x=>x.Name,StringComparer.OrdinalIgnoreCase).Select(group=>
        {
            var stage=group.Min(x=>x.Stage); var locations=group.Where(x=>x.Stage!=CompilerSupportStage.FullySupported).Take(5).Select(x=>new CapabilityLocation(x.Span.Start.File,x.Span.Start.Line,x.Span.Start.Column)).ToArray();
            return new CommandCapability(group.Key.ToLowerInvariant(),group.Count(),stage.ToString().ToUpperInvariant(),locations,group.Select(x=>x.BlockingReason).FirstOrDefault(x=>x is not null));
        }).OrderByDescending(x=>x.Count).ThenBy(x=>x.Command,StringComparer.Ordinal).ToArray();
        return new(files.Length,declarations.Count,declarations.Count(d=>d.Directive.StartsWith("duplicate(",StringComparison.Ordinal)),declarations.Count(d=>d.Directive=="script"),commands,errors);
    }
}
