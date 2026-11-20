using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayX.PspSimulator.Repositories.Migrations
{
    /// <inheritdoc />
    public partial class SettlementBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "settlement_batch_id",
                table: "authorizations",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "settlement_batches",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    business_date = table.Column<DateOnly>(type: "date", nullable: false),
                    cutoff_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    object_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    line_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_settlement_batches", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_authorizations_settlement_batch_id",
                table: "authorizations",
                column: "settlement_batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_authorizations_unsettled",
                table: "authorizations",
                column: "captured_at",
                filter: "status = 'Captured' AND settlement_batch_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_settlement_batches_not_uploaded",
                table: "settlement_batches",
                column: "created_at",
                filter: "uploaded_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_settlement_batches_object_key",
                table: "settlement_batches",
                column: "object_key",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_authorizations_settlement_batches_settlement_batch_id",
                table: "authorizations",
                column: "settlement_batch_id",
                principalTable: "settlement_batches",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_authorizations_settlement_batches_settlement_batch_id",
                table: "authorizations");

            migrationBuilder.DropTable(
                name: "settlement_batches");

            migrationBuilder.DropIndex(
                name: "ix_authorizations_settlement_batch_id",
                table: "authorizations");

            migrationBuilder.DropIndex(
                name: "ix_authorizations_unsettled",
                table: "authorizations");

            migrationBuilder.DropColumn(
                name: "settlement_batch_id",
                table: "authorizations");
        }
    }
}
