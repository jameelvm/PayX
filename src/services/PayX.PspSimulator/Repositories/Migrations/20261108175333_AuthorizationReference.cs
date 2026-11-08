using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayX.PspSimulator.Repositories.Migrations
{
    /// <inheritdoc />
    public partial class AuthorizationReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "reference",
                table: "authorizations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_authorizations_reference",
                table: "authorizations",
                column: "reference");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_authorizations_reference",
                table: "authorizations");

            migrationBuilder.DropColumn(
                name: "reference",
                table: "authorizations");
        }
    }
}
