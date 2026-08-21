namespace Athena.Net.MapServer.World;

public static class GridLineTraversal
{
    public static IEnumerable<(ushort X, ushort Y)> Enumerate(
        ushort fromX,
        ushort fromY,
        ushort toX,
        ushort toY)
    {
        var x = (int)fromX;
        var y = (int)fromY;
        var targetX = (int)toX;
        var targetY = (int)toY;
        var deltaX = Math.Abs(targetX - x);
        var stepX = x < targetX ? 1 : -1;
        var deltaY = -Math.Abs(targetY - y);
        var stepY = y < targetY ? 1 : -1;
        var error = deltaX + deltaY;

        while (true)
        {
            yield return ((ushort)x, (ushort)y);
            if (x == targetX && y == targetY)
            {
                yield break;
            }

            var doubledError = error * 2;
            if (doubledError >= deltaY)
            {
                error += deltaY;
                x += stepX;
            }

            if (doubledError <= deltaX)
            {
                error += deltaX;
                y += stepY;
            }
        }
    }
}
