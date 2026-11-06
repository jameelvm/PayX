using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayX.Payment.Repositories.Migrations
{
    /// <inheritdoc />
    public partial class RiskAssessment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_payments_status",
                table: "payments");

            migrationBuilder.AddColumn<string>(
                name: "risk_reasons",
                table: "payments",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "risk_score",
                table: "payments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_payments_risk_score",
                table: "payments",
                sql: "risk_score IS NULL OR risk_score BETWEEN 0 AND 100");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payments_status",
                table: "payments",
                sql: "status IN ('Created', 'RiskApproved', 'RiskBlocked', 'Authorized', 'Declined', 'Captured', 'Voided', 'Challenged')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_payments_risk_score",
                table: "payments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payments_status",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "risk_reasons",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "risk_score",
                table: "payments");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payments_status",
                table: "payments",
                sql: "status IN ('Created', 'RiskApproved', 'RiskBlocked', 'Authorized', 'Declined', 'Captured', 'Voided')");
        }
    }
}
