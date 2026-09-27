using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LoginServer.Migrations.Identity
{
    /// <inheritdoc />
    public partial class InitialIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AthenaIdentityRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AthenaIdentityRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AthenaIdentityUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SecurityStamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PhoneNumber = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "bit", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AthenaIdentityUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AthenaIdentityRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClaimValue = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AthenaIdentityRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AthenaIdentityRoleClaims_AthenaIdentityRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AthenaIdentityRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AthenaGameAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IdentityUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RagnarokAccountId = table.Column<long>(type: "bigint", nullable: false),
                    Sex = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false, defaultValue: "M"),
                    GroupId = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<long>(type: "bigint", nullable: false),
                    UnbanTime = table.Column<long>(type: "bigint", nullable: false),
                    ExpirationTime = table.Column<long>(type: "bigint", nullable: false),
                    LoginCount = table.Column<int>(type: "int", nullable: false),
                    LastLogin = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastIp = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, defaultValue: ""),
                    Birthdate = table.Column<DateTime>(type: "date", nullable: true),
                    CharacterSlots = table.Column<byte>(type: "tinyint", nullable: false),
                    Pincode = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false, defaultValue: ""),
                    PincodeChange = table.Column<long>(type: "bigint", nullable: false),
                    VipTime = table.Column<long>(type: "bigint", nullable: false),
                    OldGroup = table.Column<int>(type: "int", nullable: false),
                    WebAuthToken = table.Column<string>(type: "nvarchar(17)", maxLength: 17, nullable: true),
                    WebAuthTokenEnabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AthenaGameAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AthenaGameAccounts_AthenaIdentityUsers_IdentityUserId",
                        column: x => x.IdentityUserId,
                        principalTable: "AthenaIdentityUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AthenaIdentityUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClaimValue = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AthenaIdentityUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AthenaIdentityUserClaims_AthenaIdentityUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AthenaIdentityUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AthenaIdentityUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ProviderKey = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AthenaIdentityUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AthenaIdentityUserLogins_AthenaIdentityUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AthenaIdentityUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AthenaIdentityUserRoles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AthenaIdentityUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AthenaIdentityUserRoles_AthenaIdentityRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AthenaIdentityRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AthenaIdentityUserRoles_AthenaIdentityUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AthenaIdentityUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AthenaIdentityUserTokens",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LoginProvider = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AthenaIdentityUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AthenaIdentityUserTokens_AthenaIdentityUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AthenaIdentityUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AthenaGameAccounts_IdentityUserId",
                table: "AthenaGameAccounts",
                column: "IdentityUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AthenaGameAccounts_RagnarokAccountId",
                table: "AthenaGameAccounts",
                column: "RagnarokAccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AthenaIdentityRoleClaims_RoleId",
                table: "AthenaIdentityRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AthenaIdentityRoles",
                column: "NormalizedName",
                unique: true,
                filter: "[NormalizedName] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AthenaIdentityUserClaims_UserId",
                table: "AthenaIdentityUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AthenaIdentityUserLogins_UserId",
                table: "AthenaIdentityUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AthenaIdentityUserRoles_RoleId",
                table: "AthenaIdentityUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AthenaIdentityUsers",
                column: "NormalizedEmail",
                unique: true,
                filter: "[NormalizedEmail] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AthenaIdentityUsers",
                column: "NormalizedUserName",
                unique: true,
                filter: "[NormalizedUserName] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AthenaGameAccounts");

            migrationBuilder.DropTable(
                name: "AthenaIdentityRoleClaims");

            migrationBuilder.DropTable(
                name: "AthenaIdentityUserClaims");

            migrationBuilder.DropTable(
                name: "AthenaIdentityUserLogins");

            migrationBuilder.DropTable(
                name: "AthenaIdentityUserRoles");

            migrationBuilder.DropTable(
                name: "AthenaIdentityUserTokens");

            migrationBuilder.DropTable(
                name: "AthenaIdentityRoles");

            migrationBuilder.DropTable(
                name: "AthenaIdentityUsers");
        }
    }
}
