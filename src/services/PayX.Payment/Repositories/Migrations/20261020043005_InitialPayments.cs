using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayX.Payment.Repositories.Migrations
{
    /// <inheritdoc />
    public partial class InitialPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payments",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    card_token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    psp_authorization_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    authorization_code = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: true),
                    decline_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    captured_amount_minor = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payments", x => x.id);
                    table.CheckConstraint("ck_payments_amount_positive", "amount_minor > 0");
                    table.CheckConstraint("ck_payments_captured_amount", "(status = 'Captured') = (captured_amount_minor IS NOT NULL)");
                    table.CheckConstraint("ck_payments_captured_within_amount", "captured_amount_minor IS NULL OR (captured_amount_minor > 0 AND captured_amount_minor <= amount_minor)");
                    table.CheckConstraint("ck_payments_psp_reference", "status NOT IN ('Authorized', 'Captured', 'Voided') OR psp_authorization_id IS NOT NULL");
                    table.CheckConstraint("ck_payments_status", "status IN ('Created', 'RiskApproved', 'RiskBlocked', 'Authorized', 'Declined', 'Captured', 'Voided')");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payments");
        }
    }
}
