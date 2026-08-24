namespace Athena.Net.MapServer.World;

public record WorldActor(
    uint ActorId,
    string Name,
    string MapName,
    ushort X,
    ushort Y,
    byte RadiusX,
    byte RadiusY,
    ushort SpriteClass = 45,
    byte Direction = 0,
    uint EffectState = 0,
    string? EntityId = null)
{
    public const ushort ClassId = 45;
    public const byte ObjectType = 6;
}

public sealed record WarpActor(uint ActorId, string Name, string MapName, ushort X, ushort Y, byte RadiusX, byte RadiusY, ushort SpriteClass = WorldActor.ClassId)
    : WorldActor(ActorId, Name, MapName, X, Y, RadiusX, RadiusY, SpriteClass);
