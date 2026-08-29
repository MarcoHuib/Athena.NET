namespace Athena.Net.MapServer.Net;

internal static class IroCoordinatePacking
{
    internal static void WritePosition(Span<byte> buffer, ushort x, ushort y, byte direction)
    {
        buffer[0] = (byte)(x >> 2);
        buffer[1] = (byte)((x << 6) | ((y >> 4) & 0x3f));
        buffer[2] = (byte)((y << 4) | (direction & 0x0f));
    }

    internal static void WriteMovement(Span<byte> buffer, ushort fromX, ushort fromY, ushort toX, ushort toY, byte subX = 8, byte subY = 8)
    {
        buffer[0] = (byte)(fromX >> 2);
        buffer[1] = (byte)((fromX << 6) | ((fromY >> 4) & 0x3f));
        buffer[2] = (byte)((fromY << 4) | ((toX >> 6) & 0x0f));
        buffer[3] = (byte)((toX << 2) | ((toY >> 8) & 0x03));
        buffer[4] = (byte)toY;
        buffer[5] = (byte)((subX << 4) | (subY & 0x0f));
    }
}
