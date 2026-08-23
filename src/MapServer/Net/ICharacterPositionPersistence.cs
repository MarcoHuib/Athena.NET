namespace Athena.Net.MapServer.Net;

public interface ICharacterPositionPersistence
{
    Task<bool> SavePositionAsync(
        uint accountId,
        uint charId,
        string mapName,
        ushort x,
        ushort y,
        CancellationToken cancellationToken);

    Task<bool> SavePointAsync(uint accountId, uint charId, string mapName, ushort x, ushort y, CancellationToken cancellationToken)
        => Task.FromResult(false);
}
