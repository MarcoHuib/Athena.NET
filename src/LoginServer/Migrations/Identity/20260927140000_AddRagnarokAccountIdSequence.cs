using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LoginServer.Migrations.Identity
{
    /// <inheritdoc />
    /// <remarks>
    /// Hand-written rather than scaffolded from a model diff: the sequence this
    /// migration creates is deliberately NOT part of the AthenaIdentityDbContext
    /// EF model (see the comment in AthenaIdentityDbContext.OnModelCreating) so
    /// that Database.EnsureCreated() - used by this project's SQLite-backed
    /// tests, since SQLite has no migrations provider and no sequence support at
    /// all - never tries to create it. SqlServerSequenceRagnarokAccountIdAllocator
    /// reads it with a raw "NEXT VALUE FOR" statement instead of a column
    /// default, so nothing in the model needs to reference it.
    /// </remarks>
    public partial class AddRagnarokAccountIdSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "AthenaGameAccountRagnarokIds",
                startValue: 2000000L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropSequence(
                name: "AthenaGameAccountRagnarokIds");
        }
    }
}
