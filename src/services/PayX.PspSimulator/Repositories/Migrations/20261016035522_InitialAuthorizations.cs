using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayX.PspSimulator.Repositories.Migrations
{
    /// <inheritdoc />
    public partial class InitialAuthorizations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "card_accounts",
                columns: table => new
                {
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    limit_minor = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_card_accounts", x => x.fingerprint);
                });

            migrationBuilder.CreateTable(
                name: "authorizations",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    card_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    last4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    brand = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    merchant_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    authorization_code = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: true),
                    decline_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_authorizations", x => x.id);
                    table.CheckConstraint("ck_authorizations_amount_positive", "amount_minor > 0");
                    table.ForeignKey(
                        name: "fk_authorizations_card_accounts_card_fingerprint",
                        column: x => x.card_fingerprint,
                        principalTable: "card_accounts",
                        principalColumn: "fingerprint",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_authorizations_card_fingerprint_status",
                table: "authorizations",
                columns: new[] { "card_fingerprint", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "authorizations");

            migrationBuilder.DropTable(
                name: "card_accounts");
        }
    }
}
