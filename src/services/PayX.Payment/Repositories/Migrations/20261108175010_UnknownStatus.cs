using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayX.Payment.Repositories.Migrations
{
    /// <inheritdoc />
    public partial class UnknownStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_payments_status",
                table: "payments");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payments_status",
                table: "payments",
                sql: "status IN ('Created', 'RiskApproved', 'RiskBlocked', 'Authorized', 'Declined', 'Captured', 'Voided', 'Challenged', 'Unknown')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_payments_status",
                table: "payments");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payments_status",
                table: "payments",
                sql: "status IN ('Created', 'RiskApproved', 'RiskBlocked', 'Authorized', 'Declined', 'Captured', 'Voided', 'Challenged')");
        }
    }
}
