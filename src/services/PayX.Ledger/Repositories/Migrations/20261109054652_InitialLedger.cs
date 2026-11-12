using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PayX.Ledger.Repositories.Migrations
{
    /// <inheritdoc />
    public partial class InitialLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "accounts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    type = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounts", x => x.id);
                    table.UniqueConstraint("ak_accounts_id_currency", x => new { x.id, x.currency });
                    table.CheckConstraint("ck_accounts_type", "type IN ('PspClearing', 'MerchantPending', 'MerchantAvailable', 'CustomerHolds', 'MerchantAuthorized')");
                });

            migrationBuilder.CreateTable(
                name: "journal_entries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    payment_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_journal_entries", x => x.id);
                    table.CheckConstraint("ck_journal_entries_amount_positive", "amount_minor > 0");
                    table.CheckConstraint("ck_journal_entries_kind", "kind IN ('Authorization', 'Capture', 'Void')");
                });

            migrationBuilder.CreateTable(
                name: "postings",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    entry_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_postings", x => x.id);
                    table.CheckConstraint("ck_postings_amount_nonzero", "amount_minor <> 0");
                    table.ForeignKey(
                        name: "fk_postings_accounts_account_id_currency",
                        columns: x => new { x.account_id, x.currency },
                        principalTable: "accounts",
                        principalColumns: new[] { "id", "currency" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_postings_journal_entries_entry_id",
                        column: x => x.entry_id,
                        principalTable: "journal_entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_accounts_type_owner_id_currency",
                table: "accounts",
                columns: new[] { "type", "owner_id", "currency" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_journal_entries_payment_id",
                table: "journal_entries",
                column: "payment_id");

            migrationBuilder.CreateIndex(
                name: "ix_postings_account_id_currency",
                table: "postings",
                columns: new[] { "account_id", "currency" });

            migrationBuilder.CreateIndex(
                name: "ix_postings_account_id_id",
                table: "postings",
                columns: new[] { "account_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_postings_entry_id",
                table: "postings",
                column: "entry_id");

            // Double entry, enforced by the database (7.1). Checked at COMMIT
            // (DEFERRABLE INITIALLY DEFERRED): an entry and its postings are
            // inserted one row at a time, so mid-transaction the sum is
            // briefly non-zero; only the finished entry must balance. Fires
            // for every inserted posting and every inserted entry, so an
            // entry with no postings at all is refused as well.
            migrationBuilder.Sql("""
                CREATE FUNCTION ledger_assert_entry_balanced() RETURNS trigger
                LANGUAGE plpgsql AS $$
                DECLARE
                    v_entry bigint;
                    v_legs  int;
                    v_bad   record;
                BEGIN
                    IF TG_TABLE_NAME = 'postings' THEN
                        v_entry := NEW.entry_id;
                    ELSE
                        v_entry := NEW.id;
                    END IF;

                    SELECT count(*) INTO v_legs FROM postings WHERE entry_id = v_entry;
                    IF v_legs < 2 THEN
                        RAISE EXCEPTION 'journal entry % has % posting(s); double entry needs at least 2', v_entry, v_legs
                            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_entry_balanced';
                    END IF;

                    SELECT currency, sum(amount_minor) AS total INTO v_bad
                    FROM postings WHERE entry_id = v_entry
                    GROUP BY currency HAVING sum(amount_minor) <> 0
                    LIMIT 1;
                    IF FOUND THEN
                        RAISE EXCEPTION 'journal entry % does not balance: % postings sum to %', v_entry, v_bad.currency, v_bad.total
                            USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_entry_balanced';
                    END IF;

                    RETURN NULL;
                END $$;

                CREATE CONSTRAINT TRIGGER ck_entry_balanced
                    AFTER INSERT ON postings
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION ledger_assert_entry_balanced();

                CREATE CONSTRAINT TRIGGER ck_entry_has_postings
                    AFTER INSERT ON journal_entries
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION ledger_assert_entry_balanced();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER ck_entry_has_postings ON journal_entries;
                DROP TRIGGER ck_entry_balanced ON postings;
                DROP FUNCTION ledger_assert_entry_balanced();
                """);

            migrationBuilder.DropTable(
                name: "postings");

            migrationBuilder.DropTable(
                name: "accounts");

            migrationBuilder.DropTable(
                name: "journal_entries");
        }
    }
}
