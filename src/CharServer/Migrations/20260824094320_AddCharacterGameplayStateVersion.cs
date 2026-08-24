using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CharServer.Migrations
{
    /// <inheritdoc />
    public partial class AddCharacterGameplayStateVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "gameplay_state_version",
                table: "char",
                type: "decimal(20,0)",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "gameplay_state_version",
                table: "char");
        }
    }
}
