-- One database per owning service, and one login role per service that can
-- connect to its own database and nothing else.
--
-- JameX had one database per service but every service connected as the same
-- superuser, so "no service reads another's store" was a convention. Here
-- Postgres enforces it: the payment service's credentials are rejected by the
-- ledger database. In a payment system this is least privilege in practice
-- (PCI DSS req. 7): a bug or a leaked password in one service exposes one
-- service's data, not every balance in the system.
--
-- Runs once, on the first start of an empty data volume
-- (docker-entrypoint-initdb.d). Change it, then `docker compose down -v`.
--
-- Dev-only passwords. In AWS these become per-service secrets in Secrets
-- Manager (or IAM database authentication), never values in a file.
--
--   database        role             owner service     why its own store
--   payx_identity   payx_identity    Identity          credentials; smallest blast radius
--   payx_payments   payx_payments    Payment           payment state, idempotency keys, outbox
--   payx_ledger     payx_ledger      Ledger            the books; append-only from Phase 7
--   payx_recon      payx_recon       Reconciliation    runs + discrepancies
--   payx_disputes   payx_disputes    Dispute           chargeback lifecycle
--   payx_psp        payx_psp         PSP simulator     the "outside world" keeps its own records,
--                                                      which is exactly what reconciliation compares against

DO $$
DECLARE
    svc text;
BEGIN
    FOREACH svc IN ARRAY ARRAY['identity', 'payments', 'ledger', 'recon', 'disputes', 'psp'] LOOP
        EXECUTE format('CREATE ROLE %I LOGIN PASSWORD %L', 'payx_' || svc, 'payx_' || svc || '_dev');
    END LOOP;
END $$;

-- CREATE DATABASE can't run inside a DO block (it refuses to run in a
-- transaction), hence one statement per database.
CREATE DATABASE payx_identity OWNER payx_identity;
CREATE DATABASE payx_payments OWNER payx_payments;
CREATE DATABASE payx_ledger   OWNER payx_ledger;
CREATE DATABASE payx_recon    OWNER payx_recon;
CREATE DATABASE payx_disputes OWNER payx_disputes;
CREATE DATABASE payx_psp      OWNER payx_psp;

-- By default every role may CONNECT to every database (the PUBLIC grant).
-- Removing it is the line that turns the convention into a rule: after this,
-- only a database's owner (and the admin superuser) can open a connection.
REVOKE CONNECT ON DATABASE payx_identity FROM PUBLIC;
REVOKE CONNECT ON DATABASE payx_payments FROM PUBLIC;
REVOKE CONNECT ON DATABASE payx_ledger   FROM PUBLIC;
REVOKE CONNECT ON DATABASE payx_recon    FROM PUBLIC;
REVOKE CONNECT ON DATABASE payx_disputes FROM PUBLIC;
REVOKE CONNECT ON DATABASE payx_psp      FROM PUBLIC;
