using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayX.Payment.Repositories.Migrations
{
    /// <inheritdoc />
    public partial class IdempotencyLockAndRecoveryPoint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "locked_until",
                table: "idempotency_keys",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payment_id",
                table: "idempotency_keys",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "locked_until",
                table: "idempotency_keys");

            migrationBuilder.DropColumn(
                name: "payment_id",
                table: "idempotency_keys");
        }
    }
}
