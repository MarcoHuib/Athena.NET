using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LoginServer.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "global_acc_reg_num",
                columns: table => new
                {
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    key = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false, defaultValue: ""),
                    index = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    value = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_global_acc_reg_num", x => new { x.account_id, x.key, x.index });
                });

            migrationBuilder.CreateTable(
                name: "global_acc_reg_str",
                columns: table => new
                {
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    key = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false, defaultValue: ""),
                    index = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    value = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false, defaultValue: "0")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_global_acc_reg_str", x => new { x.account_id, x.key, x.index });
                });

            migrationBuilder.CreateTable(
                name: "ipbanlist",
                columns: table => new
                {
                    list = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false, defaultValue: ""),
                    btime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    rtime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    reason = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false, defaultValue: "")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ipbanlist", x => new { x.list, x.btime });
                });

            migrationBuilder.CreateTable(
                name: "login",
                columns: table => new
                {
                    account_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "2000000, 1"),
                    userid = table.Column<string>(type: "nvarchar(23)", maxLength: 23, nullable: false, defaultValue: ""),
                    user_pass = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false, defaultValue: ""),
                    sex = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false, defaultValue: "M"),
                    email = table.Column<string>(type: "nvarchar(39)", maxLength: 39, nullable: false, defaultValue: ""),
                    group_id = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    state = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    unban_time = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    expiration_time = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    logincount = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    lastlogin = table.Column<DateTime>(type: "datetime2", nullable: true),
                    last_ip = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, defaultValue: ""),
                    birthdate = table.Column<DateTime>(type: "date", nullable: true),
                    character_slots = table.Column<byte>(type: "tinyint", nullable: false, defaultValue: (byte)0),
                    pincode = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false, defaultValue: ""),
                    pincode_change = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    vip_time = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    old_group = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    web_auth_token = table.Column<string>(type: "nvarchar(17)", maxLength: 17, nullable: true),
                    web_auth_token_enabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_login", x => x.account_id);
                });

            migrationBuilder.CreateTable(
                name: "loginlog",
                columns: table => new
                {
                    time = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ip = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false, defaultValue: ""),
                    user = table.Column<string>(type: "nvarchar(23)", maxLength: 23, nullable: false, defaultValue: ""),
                    rcode = table.Column<byte>(type: "tinyint", nullable: false, defaultValue: (byte)0),
                    log = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false, defaultValue: "")
                });

            migrationBuilder.CreateIndex(
                name: "name",
                table: "login",
                column: "userid");

            migrationBuilder.CreateIndex(
                name: "ip",
                table: "loginlog",
                column: "ip");

            migrationBuilder.CreateIndex(
                name: "web_auth_token_key",
                table: "login",
                column: "web_auth_token",
                unique: true,
                filter: "[web_auth_token] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "account_id",
                table: "global_acc_reg_num",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "account_id",
                table: "global_acc_reg_str",
                column: "account_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "global_acc_reg_num");

            migrationBuilder.DropTable(
                name: "global_acc_reg_str");

            migrationBuilder.DropTable(
                name: "ipbanlist");

            migrationBuilder.DropTable(
                name: "login");

            migrationBuilder.DropTable(
                name: "loginlog");
        }
    }
}
