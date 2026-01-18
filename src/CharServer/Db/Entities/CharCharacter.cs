namespace Athena.Net.CharServer.Db.Entities;

public sealed class CharCharacter
{
    public uint CharId { get; set; }
    public uint AccountId { get; set; }
    public byte CharNum { get; set; }
    public string Name { get; set; } = string.Empty;
    public ushort Class { get; set; }
    public ushort BaseLevel { get; set; }
    public ushort JobLevel { get; set; }
    public ulong BaseExp { get; set; }
    public ulong JobExp { get; set; }
    public uint Zeny { get; set; }
    public ushort Str { get; set; }
    public ushort Agi { get; set; }
    public ushort Vit { get; set; }
    public ushort Int { get; set; }
    public ushort Dex { get; set; }
    public ushort Luk { get; set; }
    public ushort Pow { get; set; }
    public ushort Sta { get; set; }
    public ushort Wis { get; set; }
    public ushort Spl { get; set; }
    public ushort Con { get; set; }
    public ushort Crt { get; set; }
    public uint MaxHp { get; set; }
    public uint Hp { get; set; }
    public uint MaxSp { get; set; }
    public uint Sp { get; set; }
    public uint MaxAp { get; set; }
    public uint Ap { get; set; }
    public uint StatusPoint { get; set; }
    public uint SkillPoint { get; set; }
    public uint TraitPoint { get; set; }
    public uint Option { get; set; }
    public byte Karma { get; set; }
    public short Manner { get; set; }
    public uint PartyId { get; set; }
    public uint GuildId { get; set; }
    public uint PetId { get; set; }
    public uint HomunId { get; set; }
    public uint ElementalId { get; set; }
    public byte Hair { get; set; }
    public ushort HairColor { get; set; }
    public ushort ClothesColor { get; set; }
    public ushort Body { get; set; }
    public ushort Weapon { get; set; }
    public ushort Shield { get; set; }
    public ushort HeadTop { get; set; }
    public ushort HeadMid { get; set; }
    public ushort HeadBottom { get; set; }
    public ushort Robe { get; set; }
    public string LastMap { get; set; } = string.Empty;
    public ushort LastX { get; set; }
    public ushort LastY { get; set; }
    public uint LastInstanceId { get; set; }
    public string SaveMap { get; set; } = string.Empty;
    public ushort SaveX { get; set; }
    public ushort SaveY { get; set; }
    public uint PartnerId { get; set; }
    public byte Online { get; set; }
    public uint Father { get; set; }
    public uint Mother { get; set; }
    public uint Child { get; set; }
    public uint Fame { get; set; }
    public ushort Rename { get; set; }
    public uint DeleteDate { get; set; }
    public uint Moves { get; set; }
    public uint UnbanTime { get; set; }
    public byte Font { get; set; }
    public uint UniqueItemCounter { get; set; }
    public string Sex { get; set; } = "M";
    public byte HotkeyRowShift { get; set; }
    public byte HotkeyRowShift2 { get; set; }
    public uint ClanId { get; set; }
    public DateTime? LastLogin { get; set; }
    public uint TitleId { get; set; }
    public ushort ShowEquip { get; set; }
    public short InventorySlots { get; set; }
    public byte BodyDirection { get; set; }
    public ushort DisableCall { get; set; }
    public byte DisablePartyInvite { get; set; }
    public byte DisableShowCostumes { get; set; }
}
