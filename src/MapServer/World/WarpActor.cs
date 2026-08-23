namespace Athena.Net.MapServer.World;

public sealed record WarpActor(
    uint ActorId,
    string Name,
    string MapName,
    ushort X,
    ushort Y,
    byte RadiusX,
    byte RadiusY,
    ushort SpriteClass = 45)
{
    public const ushort ClassId = 45;
    public const byte ObjectType = 6;
}
