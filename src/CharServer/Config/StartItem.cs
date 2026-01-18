namespace Athena.Net.CharServer.Config;

public readonly struct StartItem
{
    public StartItem(uint itemId, ushort amount, uint equipPosition)
    {
        ItemId = itemId;
        Amount = amount;
        EquipPosition = equipPosition;
    }

    public uint ItemId { get; }
    public ushort Amount { get; }
    public uint EquipPosition { get; }
}
