using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CharServer.Migrations
{
    /// <inheritdoc />
    public partial class InitialCharSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "char",
                columns: table => new
                {
                    char_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    char_num = table.Column<byte>(type: "tinyint", nullable: false),
                    name = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    @class = table.Column<int>(name: "class", type: "int", nullable: false),
                    base_level = table.Column<int>(type: "int", nullable: false),
                    job_level = table.Column<int>(type: "int", nullable: false),
                    base_exp = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    job_exp = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    zeny = table.Column<long>(type: "bigint", nullable: false),
                    str = table.Column<int>(type: "int", nullable: false),
                    agi = table.Column<int>(type: "int", nullable: false),
                    vit = table.Column<int>(type: "int", nullable: false),
                    @int = table.Column<int>(name: "int", type: "int", nullable: false),
                    dex = table.Column<int>(type: "int", nullable: false),
                    luk = table.Column<int>(type: "int", nullable: false),
                    pow = table.Column<int>(type: "int", nullable: false),
                    sta = table.Column<int>(type: "int", nullable: false),
                    wis = table.Column<int>(type: "int", nullable: false),
                    spl = table.Column<int>(type: "int", nullable: false),
                    con = table.Column<int>(type: "int", nullable: false),
                    crt = table.Column<int>(type: "int", nullable: false),
                    max_hp = table.Column<long>(type: "bigint", nullable: false),
                    hp = table.Column<long>(type: "bigint", nullable: false),
                    max_sp = table.Column<long>(type: "bigint", nullable: false),
                    sp = table.Column<long>(type: "bigint", nullable: false),
                    max_ap = table.Column<long>(type: "bigint", nullable: false),
                    ap = table.Column<long>(type: "bigint", nullable: false),
                    status_point = table.Column<long>(type: "bigint", nullable: false),
                    skill_point = table.Column<long>(type: "bigint", nullable: false),
                    trait_point = table.Column<long>(type: "bigint", nullable: false),
                    option = table.Column<long>(type: "bigint", nullable: false),
                    karma = table.Column<byte>(type: "tinyint", nullable: false),
                    manner = table.Column<short>(type: "smallint", nullable: false),
                    party_id = table.Column<long>(type: "bigint", nullable: false),
                    guild_id = table.Column<long>(type: "bigint", nullable: false),
                    pet_id = table.Column<long>(type: "bigint", nullable: false),
                    homun_id = table.Column<long>(type: "bigint", nullable: false),
                    elemental_id = table.Column<long>(type: "bigint", nullable: false),
                    hair = table.Column<byte>(type: "tinyint", nullable: false),
                    hair_color = table.Column<int>(type: "int", nullable: false),
                    clothes_color = table.Column<int>(type: "int", nullable: false),
                    body = table.Column<int>(type: "int", nullable: false),
                    weapon = table.Column<int>(type: "int", nullable: false),
                    shield = table.Column<int>(type: "int", nullable: false),
                    head_top = table.Column<int>(type: "int", nullable: false),
                    head_mid = table.Column<int>(type: "int", nullable: false),
                    head_bottom = table.Column<int>(type: "int", nullable: false),
                    robe = table.Column<int>(type: "int", nullable: false),
                    last_map = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: false),
                    last_x = table.Column<int>(type: "int", nullable: false),
                    last_y = table.Column<int>(type: "int", nullable: false),
                    last_instanceid = table.Column<long>(type: "bigint", nullable: false),
                    save_map = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: false),
                    save_x = table.Column<int>(type: "int", nullable: false),
                    save_y = table.Column<int>(type: "int", nullable: false),
                    partner_id = table.Column<long>(type: "bigint", nullable: false),
                    online = table.Column<byte>(type: "tinyint", nullable: false),
                    father = table.Column<long>(type: "bigint", nullable: false),
                    mother = table.Column<long>(type: "bigint", nullable: false),
                    child = table.Column<long>(type: "bigint", nullable: false),
                    fame = table.Column<long>(type: "bigint", nullable: false),
                    rename = table.Column<int>(type: "int", nullable: false),
                    delete_date = table.Column<long>(type: "bigint", nullable: false),
                    moves = table.Column<long>(type: "bigint", nullable: false),
                    unban_time = table.Column<long>(type: "bigint", nullable: false),
                    font = table.Column<byte>(type: "tinyint", nullable: false),
                    uniqueitem_counter = table.Column<long>(type: "bigint", nullable: false),
                    sex = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false),
                    hotkey_rowshift = table.Column<byte>(type: "tinyint", nullable: false),
                    hotkey_rowshift2 = table.Column<byte>(type: "tinyint", nullable: false),
                    clan_id = table.Column<long>(type: "bigint", nullable: false),
                    last_login = table.Column<DateTime>(type: "datetime2", nullable: true),
                    title_id = table.Column<long>(type: "bigint", nullable: false),
                    show_equip = table.Column<int>(type: "int", nullable: false),
                    inventory_slots = table.Column<short>(type: "smallint", nullable: false),
                    body_direction = table.Column<byte>(type: "tinyint", nullable: false),
                    disable_call = table.Column<int>(type: "int", nullable: false),
                    disable_partyinvite = table.Column<byte>(type: "tinyint", nullable: false),
                    disable_showcostumes = table.Column<byte>(type: "tinyint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_char", x => x.char_id);
                });

            migrationBuilder.CreateTable(
                name: "hotkey",
                columns: table => new
                {
                    char_id = table.Column<long>(type: "bigint", nullable: false),
                    hotkey = table.Column<byte>(type: "tinyint", nullable: false),
                    type = table.Column<byte>(type: "tinyint", nullable: false),
                    itemskill_id = table.Column<long>(type: "bigint", nullable: false),
                    skill_lvl = table.Column<byte>(type: "tinyint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hotkey", x => new { x.char_id, x.hotkey });
                });

            migrationBuilder.CreateTable(
                name: "inventory",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    char_id = table.Column<long>(type: "bigint", nullable: false),
                    nameid = table.Column<long>(type: "bigint", nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    equip = table.Column<long>(type: "bigint", nullable: false),
                    identify = table.Column<short>(type: "smallint", nullable: false),
                    refine = table.Column<byte>(type: "tinyint", nullable: false),
                    attribute = table.Column<byte>(type: "tinyint", nullable: false),
                    card0 = table.Column<long>(type: "bigint", nullable: false),
                    card1 = table.Column<long>(type: "bigint", nullable: false),
                    card2 = table.Column<long>(type: "bigint", nullable: false),
                    card3 = table.Column<long>(type: "bigint", nullable: false),
                    option_id0 = table.Column<short>(type: "smallint", nullable: false),
                    option_val0 = table.Column<short>(type: "smallint", nullable: false),
                    option_parm0 = table.Column<byte>(type: "tinyint", nullable: false),
                    option_id1 = table.Column<short>(type: "smallint", nullable: false),
                    option_val1 = table.Column<short>(type: "smallint", nullable: false),
                    option_parm1 = table.Column<byte>(type: "tinyint", nullable: false),
                    option_id2 = table.Column<short>(type: "smallint", nullable: false),
                    option_val2 = table.Column<short>(type: "smallint", nullable: false),
                    option_parm2 = table.Column<byte>(type: "tinyint", nullable: false),
                    option_id3 = table.Column<short>(type: "smallint", nullable: false),
                    option_val3 = table.Column<short>(type: "smallint", nullable: false),
                    option_parm3 = table.Column<byte>(type: "tinyint", nullable: false),
                    option_id4 = table.Column<short>(type: "smallint", nullable: false),
                    option_val4 = table.Column<short>(type: "smallint", nullable: false),
                    option_parm4 = table.Column<byte>(type: "tinyint", nullable: false),
                    expire_time = table.Column<long>(type: "bigint", nullable: false),
                    favorite = table.Column<byte>(type: "tinyint", nullable: false),
                    bound = table.Column<byte>(type: "tinyint", nullable: false),
                    unique_id = table.Column<decimal>(type: "decimal(20,0)", nullable: false),
                    equip_switch = table.Column<long>(type: "bigint", nullable: false),
                    enchantgrade = table.Column<byte>(type: "tinyint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "quest",
                columns: table => new
                {
                    char_id = table.Column<long>(type: "bigint", nullable: false),
                    quest_id = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false),
                    time = table.Column<long>(type: "bigint", nullable: false),
                    count1 = table.Column<long>(type: "bigint", nullable: false),
                    count2 = table.Column<long>(type: "bigint", nullable: false),
                    count3 = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quest", x => new { x.char_id, x.quest_id });
                });

            migrationBuilder.CreateTable(
                name: "skill",
                columns: table => new
                {
                    char_id = table.Column<long>(type: "bigint", nullable: false),
                    id = table.Column<int>(type: "int", nullable: false),
                    lv = table.Column<byte>(type: "tinyint", nullable: false),
                    flag = table.Column<byte>(type: "tinyint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skill", x => new { x.char_id, x.id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_char_account_id",
                table: "char",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_char_guild_id",
                table: "char",
                column: "guild_id");

            migrationBuilder.CreateIndex(
                name: "IX_char_name",
                table: "char",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_char_online",
                table: "char",
                column: "online");

            migrationBuilder.CreateIndex(
                name: "IX_char_party_id",
                table: "char",
                column: "party_id");

            migrationBuilder.CreateIndex(
                name: "IX_hotkey_char_id",
                table: "hotkey",
                column: "char_id");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_char_id",
                table: "inventory",
                column: "char_id");

            migrationBuilder.CreateIndex(
                name: "IX_skill_char_id",
                table: "skill",
                column: "char_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "char");

            migrationBuilder.DropTable(
                name: "hotkey");

            migrationBuilder.DropTable(
                name: "inventory");

            migrationBuilder.DropTable(
                name: "quest");

            migrationBuilder.DropTable(
                name: "skill");
        }
    }
}
