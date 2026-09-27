using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LoginServer.Migrations.Identity
{
    /// <inheritdoc />
    public partial class AddWebAuthTokenUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_AthenaGameAccounts_WebAuthToken",
                table: "AthenaGameAccounts",
                column: "WebAuthToken",
                unique: true,
                filter: "[WebAuthToken] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AthenaGameAccounts_WebAuthToken",
                table: "AthenaGameAccounts");
        }
    }
}
