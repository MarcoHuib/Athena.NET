using Microsoft.EntityFrameworkCore;
using Athena.Net.CharServer.Db.Entities;

namespace Athena.Net.CharServer.Db;

public sealed class CharDbContext : DbContext
{
    private readonly CharDbTableNames _tableNames;

    public CharDbContext(DbContextOptions<CharDbContext> options, CharDbTableNames tableNames) : base(options)
    {
        _tableNames = tableNames;
    }

    public DbSet<CharCharacter> Characters => Set<CharCharacter>();
    public DbSet<CharInventory> Inventory => Set<CharInventory>();
    public DbSet<CharSkill> Skills => Set<CharSkill>();
    public DbSet<CharHotkey> Hotkeys => Set<CharHotkey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var character = modelBuilder.Entity<CharCharacter>();
        character.ToTable(_tableNames.CharTable);
        character.HasKey(c => c.CharId);
        character.HasIndex(c => c.Name).IsUnique();
        character.HasIndex(c => c.AccountId);
        character.HasIndex(c => c.PartyId);
        character.HasIndex(c => c.GuildId);
        character.HasIndex(c => c.Online);

        character.Property(c => c.CharId).HasColumnName("char_id").ValueGeneratedOnAdd();
        character.Property(c => c.AccountId).HasColumnName("account_id");
        character.Property(c => c.CharNum).HasColumnName("char_num");
        character.Property(c => c.Name).HasColumnName("name").HasMaxLength(30);
        character.Property(c => c.Class).HasColumnName("class");
        character.Property(c => c.BaseLevel).HasColumnName("base_level");
        character.Property(c => c.JobLevel).HasColumnName("job_level");
        character.Property(c => c.BaseExp).HasColumnName("base_exp");
        character.Property(c => c.JobExp).HasColumnName("job_exp");
        character.Property(c => c.Zeny).HasColumnName("zeny");
        character.Property(c => c.Str).HasColumnName("str");
        character.Property(c => c.Agi).HasColumnName("agi");
        character.Property(c => c.Vit).HasColumnName("vit");
        character.Property(c => c.Int).HasColumnName("int");
        character.Property(c => c.Dex).HasColumnName("dex");
        character.Property(c => c.Luk).HasColumnName("luk");
        character.Property(c => c.Pow).HasColumnName("pow");
        character.Property(c => c.Sta).HasColumnName("sta");
        character.Property(c => c.Wis).HasColumnName("wis");
        character.Property(c => c.Spl).HasColumnName("spl");
        character.Property(c => c.Con).HasColumnName("con");
        character.Property(c => c.Crt).HasColumnName("crt");
        character.Property(c => c.MaxHp).HasColumnName("max_hp");
        character.Property(c => c.Hp).HasColumnName("hp");
        character.Property(c => c.MaxSp).HasColumnName("max_sp");
        character.Property(c => c.Sp).HasColumnName("sp");
        character.Property(c => c.MaxAp).HasColumnName("max_ap");
        character.Property(c => c.Ap).HasColumnName("ap");
        character.Property(c => c.StatusPoint).HasColumnName("status_point");
        character.Property(c => c.SkillPoint).HasColumnName("skill_point");
        character.Property(c => c.TraitPoint).HasColumnName("trait_point");
        character.Property(c => c.Option).HasColumnName("option");
        character.Property(c => c.Karma).HasColumnName("karma");
        character.Property(c => c.Manner).HasColumnName("manner");
        character.Property(c => c.PartyId).HasColumnName("party_id");
        character.Property(c => c.GuildId).HasColumnName("guild_id");
        character.Property(c => c.PetId).HasColumnName("pet_id");
        character.Property(c => c.HomunId).HasColumnName("homun_id");
        character.Property(c => c.ElementalId).HasColumnName("elemental_id");
        character.Property(c => c.Hair).HasColumnName("hair");
        character.Property(c => c.HairColor).HasColumnName("hair_color");
        character.Property(c => c.ClothesColor).HasColumnName("clothes_color");
        character.Property(c => c.Body).HasColumnName("body");
        character.Property(c => c.Weapon).HasColumnName("weapon");
        character.Property(c => c.Shield).HasColumnName("shield");
        character.Property(c => c.HeadTop).HasColumnName("head_top");
        character.Property(c => c.HeadMid).HasColumnName("head_mid");
        character.Property(c => c.HeadBottom).HasColumnName("head_bottom");
        character.Property(c => c.Robe).HasColumnName("robe");
        character.Property(c => c.LastMap).HasColumnName("last_map").HasMaxLength(11);
        character.Property(c => c.LastX).HasColumnName("last_x");
        character.Property(c => c.LastY).HasColumnName("last_y");
        character.Property(c => c.LastInstanceId).HasColumnName("last_instanceid");
        character.Property(c => c.SaveMap).HasColumnName("save_map").HasMaxLength(11);
        character.Property(c => c.SaveX).HasColumnName("save_x");
        character.Property(c => c.SaveY).HasColumnName("save_y");
        character.Property(c => c.PartnerId).HasColumnName("partner_id");
        character.Property(c => c.Online).HasColumnName("online");
        character.Property(c => c.Father).HasColumnName("father");
        character.Property(c => c.Mother).HasColumnName("mother");
        character.Property(c => c.Child).HasColumnName("child");
        character.Property(c => c.Fame).HasColumnName("fame");
        character.Property(c => c.Rename).HasColumnName("rename");
        character.Property(c => c.DeleteDate).HasColumnName("delete_date");
        character.Property(c => c.Moves).HasColumnName("moves");
        character.Property(c => c.UnbanTime).HasColumnName("unban_time");
        character.Property(c => c.Font).HasColumnName("font");
        character.Property(c => c.UniqueItemCounter).HasColumnName("uniqueitem_counter");
        character.Property(c => c.Sex).HasColumnName("sex").HasMaxLength(1);
        character.Property(c => c.HotkeyRowShift).HasColumnName("hotkey_rowshift");
        character.Property(c => c.HotkeyRowShift2).HasColumnName("hotkey_rowshift2");
        character.Property(c => c.ClanId).HasColumnName("clan_id");
        character.Property(c => c.LastLogin).HasColumnName("last_login");
        character.Property(c => c.TitleId).HasColumnName("title_id");
        character.Property(c => c.ShowEquip).HasColumnName("show_equip");
        character.Property(c => c.InventorySlots).HasColumnName("inventory_slots");
        character.Property(c => c.BodyDirection).HasColumnName("body_direction");
        character.Property(c => c.DisableCall).HasColumnName("disable_call");
        character.Property(c => c.DisablePartyInvite).HasColumnName("disable_partyinvite");
        character.Property(c => c.DisableShowCostumes).HasColumnName("disable_showcostumes");

        var inventory = modelBuilder.Entity<CharInventory>();
        inventory.ToTable(_tableNames.InventoryTable);
        inventory.HasKey(i => i.Id);
        inventory.HasIndex(i => i.CharId);
        inventory.Property(i => i.Id).HasColumnName("id").ValueGeneratedOnAdd();
        inventory.Property(i => i.CharId).HasColumnName("char_id");
        inventory.Property(i => i.NameId).HasColumnName("nameid");
        inventory.Property(i => i.Amount).HasColumnName("amount");
        inventory.Property(i => i.Equip).HasColumnName("equip");
        inventory.Property(i => i.Identify).HasColumnName("identify");
        inventory.Property(i => i.Refine).HasColumnName("refine");
        inventory.Property(i => i.Attribute).HasColumnName("attribute");
        inventory.Property(i => i.Card0).HasColumnName("card0");
        inventory.Property(i => i.Card1).HasColumnName("card1");
        inventory.Property(i => i.Card2).HasColumnName("card2");
        inventory.Property(i => i.Card3).HasColumnName("card3");
        inventory.Property(i => i.OptionId0).HasColumnName("option_id0");
        inventory.Property(i => i.OptionVal0).HasColumnName("option_val0");
        inventory.Property(i => i.OptionParm0).HasColumnName("option_parm0");
        inventory.Property(i => i.OptionId1).HasColumnName("option_id1");
        inventory.Property(i => i.OptionVal1).HasColumnName("option_val1");
        inventory.Property(i => i.OptionParm1).HasColumnName("option_parm1");
        inventory.Property(i => i.OptionId2).HasColumnName("option_id2");
        inventory.Property(i => i.OptionVal2).HasColumnName("option_val2");
        inventory.Property(i => i.OptionParm2).HasColumnName("option_parm2");
        inventory.Property(i => i.OptionId3).HasColumnName("option_id3");
        inventory.Property(i => i.OptionVal3).HasColumnName("option_val3");
        inventory.Property(i => i.OptionParm3).HasColumnName("option_parm3");
        inventory.Property(i => i.OptionId4).HasColumnName("option_id4");
        inventory.Property(i => i.OptionVal4).HasColumnName("option_val4");
        inventory.Property(i => i.OptionParm4).HasColumnName("option_parm4");
        inventory.Property(i => i.ExpireTime).HasColumnName("expire_time");
        inventory.Property(i => i.Favorite).HasColumnName("favorite");
        inventory.Property(i => i.Bound).HasColumnName("bound");
        inventory.Property(i => i.UniqueId).HasColumnName("unique_id");
        inventory.Property(i => i.EquipSwitch).HasColumnName("equip_switch");
        inventory.Property(i => i.EnchantGrade).HasColumnName("enchantgrade");

        var skills = modelBuilder.Entity<CharSkill>();
        skills.ToTable(_tableNames.SkillTable);
        skills.HasKey(s => new { s.CharId, s.SkillId });
        skills.HasIndex(s => s.CharId);
        skills.Property(s => s.CharId).HasColumnName("char_id");
        skills.Property(s => s.SkillId).HasColumnName("id");
        skills.Property(s => s.SkillLevel).HasColumnName("lv");
        skills.Property(s => s.Flag).HasColumnName("flag");

        var hotkeys = modelBuilder.Entity<CharHotkey>();
        hotkeys.ToTable(_tableNames.HotkeyTable);
        hotkeys.HasKey(h => new { h.CharId, h.Hotkey });
        hotkeys.HasIndex(h => h.CharId);
        hotkeys.Property(h => h.CharId).HasColumnName("char_id");
        hotkeys.Property(h => h.Hotkey).HasColumnName("hotkey");
        hotkeys.Property(h => h.Type).HasColumnName("type");
        hotkeys.Property(h => h.ItemSkillId).HasColumnName("itemskill_id");
        hotkeys.Property(h => h.SkillLevel).HasColumnName("skill_lvl");
    }
}
