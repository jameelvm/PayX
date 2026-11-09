using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayX.Ledger.Repositories.Migrations
{
    /// <inheritdoc />
    public partial class AccountBalances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "balance_minor",
                table: "accounts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Balances for the history booked before this column existed: the
            // projection starts from the truth, the postings.
            migrationBuilder.Sql("""
                UPDATE accounts a
                SET balance_minor = coalesce((SELECT sum(p.amount_minor) FROM postings p WHERE p.account_id = a.id), 0);
                """);

            // The service may change exactly one column of one table: the
            // balance, next to the postings it reflects (7.5). A column grant,
            // so not even the service can retype an account or move it to
            // another owner. Postings and entries stay SELECT/INSERT only (7.2).
            migrationBuilder.Sql("GRANT UPDATE (balance_minor) ON accounts TO payx_ledger_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("REVOKE UPDATE (balance_minor) ON accounts FROM payx_ledger_app;");
            migrationBuilder.DropColumn(
                name: "balance_minor",
                table: "accounts");
        }
    }
}
