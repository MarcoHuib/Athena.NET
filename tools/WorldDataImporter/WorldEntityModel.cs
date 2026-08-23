using System.Text.Json.Serialization;

internal sealed record WorldEntityDefinition(int SchemaVersion, string Id, string Kind, WorldActorComponent? Actor, IReadOnlyList<WorldTriggerDefinition> Triggers, WorldSourceInfo Source);
internal sealed record WorldActorComponent(string Name, string Map, ushort X, ushort Y, byte Direction, ushort Class);
internal sealed record WorldTriggerDefinition(string Type, string Map, ushort X, ushort Y, ushort RadiusX, ushort RadiusY, IReadOnlyList<WorldActionDefinition> Actions);
[JsonPolymorphic(TypeDiscriminatorPropertyName = "Type")]
[JsonDerivedType(typeof(SetSavePointAction), "SetSavePoint")]
[JsonDerivedType(typeof(WarpAction), "Warp")]
internal abstract record WorldActionDefinition;
internal sealed record WarpAction(string Map, ushort X, ushort Y) : WorldActionDefinition;
internal sealed record SetSavePointAction(string Map, ushort X, ushort Y) : WorldActionDefinition;
internal sealed record WorldSourceInfo(string Repository, string Commit, string File, int Line);
internal static class DeterministicId
{
    public static string For(string kind, string map, string name) => $"{kind.ToLowerInvariant()}:{map.ToLowerInvariant()}:{name.TrimStart('#').ToLowerInvariant()}";
    public static string FileName(string id) => id[(id.LastIndexOf(':') + 1)..];
}
