using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayX.PspSimulator.Repositories.Migrations
{
    /// <inheritdoc />
    public partial class CaptureAndVoid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "captured_amount_minor",
                table: "authorizations",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "captured_at",
                table: "authorizations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "authorizations",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddCheckConstraint(
                name: "ck_authorizations_captured_within_authorized",
                table: "authorizations",
                sql: "captured_amount_minor IS NULL OR (captured_amount_minor > 0 AND captured_amount_minor <= amount_minor)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_authorizations_captured_within_authorized",
                table: "authorizations");

            migrationBuilder.DropColumn(
                name: "captured_amount_minor",
                table: "authorizations");

            migrationBuilder.DropColumn(
                name: "captured_at",
                table: "authorizations");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "authorizations");
        }
    }
}
