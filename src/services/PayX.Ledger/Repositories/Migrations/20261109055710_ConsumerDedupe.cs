using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayX.Ledger.Repositories.Migrations
{
    /// <inheritdoc />
    public partial class ConsumerDedupe : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "processed_events",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    entry_id = table.Column<long>(type: "bigint", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processed_events", x => x.event_id);
                    table.ForeignKey(
                        name: "fk_processed_events_journal_entries_entry_id",
                        column: x => x.entry_id,
                        principalTable: "journal_entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_journal_entries_one_per_step",
                table: "journal_entries",
                columns: new[] { "payment_id", "kind" },
                unique: true,
                filter: "kind IN ('Authorization', 'Capture', 'Void')");

            migrationBuilder.CreateIndex(
                name: "ix_processed_events_entry_id",
                table: "processed_events",
                column: "entry_id");

            // The service's role may record and read processed events, never
            // remove one: forgetting an event would let its redelivery book twice.
            migrationBuilder.Sql("GRANT SELECT, INSERT ON processed_events TO payx_ledger_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "processed_events");

            migrationBuilder.DropIndex(
                name: "ix_journal_entries_one_per_step",
                table: "journal_entries");
        }
    }
}
