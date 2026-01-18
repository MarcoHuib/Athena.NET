namespace Athena.Net.CharServer.Config;

public readonly struct StartPoint
{
    public StartPoint(string map, ushort x, ushort y)
    {
        Map = map;
        X = x;
        Y = y;
    }

    public string Map { get; }
    public ushort X { get; }
    public ushort Y { get; }
}
