using System.Text.Json;
using System.Text.Json.Serialization;

internal static class DeterministicJson
{
    // Priority 14 (ai/world-data.md): analysis-artifact JSON only (summary.json, blockers.json,
    // work-items.json, dependencies.json, domains/*.jsonl, compatible.jsonl/unsupported.jsonl) -
    // this writer has no consumer anywhere outside WorldDataImporter's own analyze command (see the
    // grep above this file's own usages), so switching enum members (DomainCompatibilityStatus,
    // CompatibilityStatus, FailureStage, DefinitionCompatibilityStatus, AnalysisScope, and any
    // future analysis-only enum) from numeric to stable textual JSON values here is safe and does
    // not touch any wire/persistence format used by src/MapServer or src/CharServer - those
    // projects have their own independent serialization, never routed through this type.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options) + "\n";
    public static string SerializeLine<T>(T value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(Options) { WriteIndented = false });
    public static async Task WriteFileAsync<T>(string path, T value)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, Serialize(value));
    }
}
