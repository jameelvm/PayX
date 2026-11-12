-- Module 7.2: Ledger's runtime role, for a data volume created before Phase 7.
-- (A fresh volume gets it from init/01-databases-and-roles.sql.) Idempotent:
--
--   docker exec -i payx-postgres psql -U payx_admin -d payx_admin < infra/postgres/upgrade/07-ledger-app-role.sql
--
-- payx_ledger owns the tables and runs migrations; the running service logs
-- in as payx_ledger_app, which the migrations grant SELECT/INSERT only. An
-- owner can ALTER TABLE ... DISABLE TRIGGER, so the service must never run
-- as the owner: the append-only trigger would be one statement away.
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'payx_ledger_app') THEN
        CREATE ROLE payx_ledger_app LOGIN PASSWORD 'payx_ledger_app_dev';
    END IF;
END $$;

GRANT CONNECT ON DATABASE payx_ledger TO payx_ledger_app;
