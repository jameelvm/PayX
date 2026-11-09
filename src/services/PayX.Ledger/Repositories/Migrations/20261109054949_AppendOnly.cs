using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayX.Ledger.Repositories.Migrations
{
    /// <summary>
    /// The ledger is append-only (7.2; DESIGN decision 9; doc ch.2, "immutable
    /// entry … auditable trail"). Two independent locks:
    ///
    /// 1. <b>A trigger</b> refuses UPDATE, DELETE and TRUNCATE on the journal,
    ///    for everyone, the table owner included.
    /// 2. <b>Grants</b>: the running service logs in as payx_ledger_app,
    ///    which may only SELECT and INSERT. It can't even reach the trigger.
    ///
    /// Why both: the owner (payx_ledger, which runs these migrations) could
    /// disable the trigger, so the service must not run as the owner; and
    /// grants alone would let anyone who ever gets the owner's password
    /// "fix" a posting quietly. A mistake is corrected the accountant's way,
    /// with a new reversing entry, never by editing history (Phase 9).
    /// </summary>
    public partial class AppendOnly : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION ledger_refuse_change() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'the ledger is append-only: % on % is not allowed; post a reversing entry instead', TG_OP, TG_TABLE_NAME
                        USING ERRCODE = 'restrict_violation';
                END $$;

                -- Row level for UPDATE/DELETE; TRUNCATE skips row triggers, so it gets its own.
                CREATE TRIGGER journal_entries_append_only BEFORE UPDATE OR DELETE ON journal_entries
                    FOR EACH ROW EXECUTE FUNCTION ledger_refuse_change();
                CREATE TRIGGER journal_entries_no_truncate BEFORE TRUNCATE ON journal_entries
                    FOR EACH STATEMENT EXECUTE FUNCTION ledger_refuse_change();
                CREATE TRIGGER postings_append_only BEFORE UPDATE OR DELETE ON postings
                    FOR EACH ROW EXECUTE FUNCTION ledger_refuse_change();
                CREATE TRIGGER postings_no_truncate BEFORE TRUNCATE ON postings
                    FOR EACH STATEMENT EXECUTE FUNCTION ledger_refuse_change();

                -- Accounts gain a balance that changes (7.5), so they can't be
                -- frozen whole; but an account with history is never removed.
                CREATE TRIGGER accounts_no_delete BEFORE DELETE ON accounts
                    FOR EACH ROW EXECUTE FUNCTION ledger_refuse_change();
                CREATE TRIGGER accounts_no_truncate BEFORE TRUNCATE ON accounts
                    FOR EACH STATEMENT EXECUTE FUNCTION ledger_refuse_change();

                -- The service's role: read and append, nothing else. No
                -- UPDATE, DELETE or TRUNCATE privilege on any ledger table.
                -- (payx_ledger_app is created by the Postgres init script, or
                -- infra/postgres/upgrade/07-ledger-app-role.sql.)
                GRANT USAGE ON SCHEMA public TO payx_ledger_app;
                GRANT SELECT, INSERT ON accounts, journal_entries, postings TO payx_ledger_app;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                REVOKE ALL ON accounts, journal_entries, postings FROM payx_ledger_app;
                DROP TRIGGER accounts_no_truncate ON accounts;
                DROP TRIGGER accounts_no_delete ON accounts;
                DROP TRIGGER postings_no_truncate ON postings;
                DROP TRIGGER postings_append_only ON postings;
                DROP TRIGGER journal_entries_no_truncate ON journal_entries;
                DROP TRIGGER journal_entries_append_only ON journal_entries;
                DROP FUNCTION ledger_refuse_change();
                """);
        }
    }
}
