using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayX.Ledger.Repositories.Migrations
{
    /// <inheritdoc />
    public partial class HistoryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_journal_entries_customer_id_id",
                table: "journal_entries",
                columns: new[] { "customer_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_journal_entries_merchant_id_id",
                table: "journal_entries",
                columns: new[] { "merchant_id", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_journal_entries_customer_id_id",
                table: "journal_entries");

            migrationBuilder.DropIndex(
                name: "ix_journal_entries_merchant_id_id",
                table: "journal_entries");
        }
    }
}
