namespace Athena.Net.CharServer.Db.Entities;

public sealed class CharInventory
{
    public uint Id { get; set; }
    public uint CharId { get; set; }
    public uint NameId { get; set; }
    public uint Amount { get; set; }
    public uint Equip { get; set; }
    public short Identify { get; set; }
    public byte Refine { get; set; }
    public byte Attribute { get; set; }
    public uint Card0 { get; set; }
    public uint Card1 { get; set; }
    public uint Card2 { get; set; }
    public uint Card3 { get; set; }
    public short OptionId0 { get; set; }
    public short OptionVal0 { get; set; }
    public byte OptionParm0 { get; set; }
    public short OptionId1 { get; set; }
    public short OptionVal1 { get; set; }
    public byte OptionParm1 { get; set; }
    public short OptionId2 { get; set; }
    public short OptionVal2 { get; set; }
    public byte OptionParm2 { get; set; }
    public short OptionId3 { get; set; }
    public short OptionVal3 { get; set; }
    public byte OptionParm3 { get; set; }
    public short OptionId4 { get; set; }
    public short OptionVal4 { get; set; }
    public byte OptionParm4 { get; set; }
    public uint ExpireTime { get; set; }
    public byte Favorite { get; set; }
    public byte Bound { get; set; }
    public ulong UniqueId { get; set; }
    public uint EquipSwitch { get; set; }
    public byte EnchantGrade { get; set; }
}
