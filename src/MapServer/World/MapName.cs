using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.World;

public static class MapName
{
    public static string NormalizeWorld(string name) => WorldMapId.Normalize(name);
    public static string ToClient(string name) => name.EndsWith(".gat", StringComparison.OrdinalIgnoreCase) ? name : name + ".gat";
}
