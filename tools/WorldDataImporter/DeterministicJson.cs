using System.Text.Json;
using System.Text.Json.Serialization;

internal static class DeterministicJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
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
