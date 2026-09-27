using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LoginServer.Migrations
{
    /// <inheritdoc />
    public partial class DropLoginAccountTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "login");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "login",
                columns: table => new
                {
                    account_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "2000000, 1"),
                    birthdate = table.Column<DateTime>(type: "date", nullable: true),
                    character_slots = table.Column<byte>(type: "tinyint", nullable: false, defaultValue: (byte)0),
                    email = table.Column<string>(type: "nvarchar(39)", maxLength: 39, nullable: false, defaultValue: ""),
                    expiration_time = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    group_id = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    last_ip = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, defaultValue: ""),
                    lastlogin = table.Column<DateTime>(type: "datetime2", nullable: true),
                    logincount = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    old_group = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    pincode = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false, defaultValue: ""),
                    pincode_change = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    sex = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false, defaultValue: "M"),
                    state = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    unban_time = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    userid = table.Column<string>(type: "nvarchar(23)", maxLength: 23, nullable: false, defaultValue: ""),
                    user_pass = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false, defaultValue: ""),
                    vip_time = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    web_auth_token = table.Column<string>(type: "nvarchar(17)", maxLength: 17, nullable: true),
                    web_auth_token_enabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_login", x => x.account_id);
                });

            migrationBuilder.CreateIndex(
                name: "name",
                table: "login",
                column: "userid");

            migrationBuilder.CreateIndex(
                name: "web_auth_token_key",
                table: "login",
                column: "web_auth_token",
                unique: true,
                filter: "[web_auth_token] IS NOT NULL");
        }
    }
}
