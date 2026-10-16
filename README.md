# PayX — a card payment system, built to learn its design

Teaching documentation for PayX, extended at the end of every phase. It
explains what was built, why, how to verify it, and the design talking points
each phase demonstrates. See `DESIGN.md` for the decision register and
`PROGRESS.md` for live build state.

**Start here for the big picture:** [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md),
with the component map, one payment step by step, and the payment state machine.

## Phase 0 — Design

Nothing runnable yet. The design is in `DESIGN.md`; the one idea to carry
into every later phase:

> A payment system is not a throughput problem (≈1,400 TPS at peak is small).
> It is a **correctness-under-partial-failure** problem. Every phase answers
> some version of "the response was lost — did money move?"

---

## Phase 1 — Local substrate

**What the phase delivers:** every piece of infrastructure the later phases
need, running locally with the same software and APIs as production, plus
the shared building blocks every service uses: one error shape, health
probes, a gateway, and an exact money type. No payment logic yet.

```
            Gateway :7080 (YARP, round robin, health-checked)
                 │
       ┌─────────┴─────────┐
   Payment :7082       Payment :7092        ← two instances, one database
       │                   │
       └──── payx_payments ┘   Postgres :5434 (6 databases, 6 roles)
             Redis :6381 · LocalStack :4568 (S3, DynamoDB, KMS) · Kafka :9094 + UI :7090
```

How to run all of it: [`RUNNING.md`](RUNNING.md).

### 1.1 Solution skeleton: dependency direction

**Built:** `PayX.slnx`, `PayX.Contracts` (no dependencies at all),
`PayX.ServiceDefaults` (shared host wiring), and a Payment service with a
temporary ping endpoint.

**Why:** dependencies only point one way: service → ServiceDefaults →
Contracts. Contracts holds only what crosses a service boundary; if it pulled
in the AWS SDK or Npgsql, every service would inherit them and a change to a
shared shape would force every service to redeploy. ServiceDefaults is where
cross-cutting concerns (errors, health, later tracing) are written once.
Every error leaves every service as RFC 7807 ProblemDetails, because a
payment client deciding "is this safe to retry?" needs one error shape.

**Verify:** `dotnet build PayX.slnx`; an unknown route returns 404
`application/problem+json`.

**Talking point:** a shared library is a coupling point; keep the one every
service depends on free of infrastructure.

### 1.2 Postgres: one database per service, enforced

**Built:** six databases (`payx_identity`, `payx_payments`, `payx_ledger`,
`payx_recon`, `payx_disputes`, `payx_psp`), each owned by its own login role,
with `CONNECT` revoked from `PUBLIC`
([`infra/postgres/init`](infra/postgres/init/01-databases-and-roles.sql)).

**Why:** JameX had a database per service but one shared superuser, so
"don't read another service's data" was a convention. Here Postgres refuses
the connection. That is least privilege (PCI DSS req. 7): a bug or leaked
password in one service exposes one service's data. The PSP simulator gets
a database too, because the provider's own records are what reconciliation
compares against in Phase 8.

**Verify:** connect as `payx_payments` to `payx_ledger` → `permission denied
… CONNECT privilege`.

**Talking points:** `REVOKE CONNECT … FROM PUBLIC` is the line that turns a
convention into a rule. Init scripts run only on an empty volume
(`docker compose down -v` to re-run). Locally one server, in AWS one RDS
instance per service; no cross-database joins means the split needs no code
change.

### 1.3 Redis + LocalStack: local AWS, real SDK

**Built:** Redis (`noeviction`) and LocalStack with an idempotent bootstrap
([`infra/localstack/init`](infra/localstack/init/01-bootstrap.sh)): KMS key
`alias/payx-card-vault`, DynamoDB `payx-card-vault` and
`payx-risk-decisions`, S3 `payx-settlement-files`.

**Why:** the code will call the genuine AWS SDK; LocalStack answers it
locally, so moving to AWS is a config change. Redis refuses writes when full
instead of evicting, because it will hold fraud velocity counters: silently
evicting one resets a card's "payments this hour" to zero, a fraud hole.
DynamoDB holds the key-value records (token → encrypted card, payment →
risk decision); Postgres holds anything needing multi-row transactions.

**Verify:** KMS encrypt → decrypt round trip of `4242424242424242`.

**Talking points:** "healthy" means "fully provisioned" (the healthcheck
waits for the last resource). Keys are referenced by alias, never id.
LocalStack's free tier doesn't persist state: a restart creates a *new* KMS
key, and anything encrypted with the old one is unreadable. That is
crypto-shredding, and a reminder that losing a key means losing the data.

### 1.4 Kafka: a log, not a queue

**Built:** a single Kafka 4.1 broker in KRaft mode (no ZooKeeper), a
one-shot `kafka-init` that creates `payx.payments` (6 partitions, key =
`payment_id`, 7-day retention, auto-create off), and Kafka UI.

**Why:** the doc's "transaction completion" relies on Kafka. Kafka orders
messages only within a partition, and the key picks the partition, so all
of one payment's events stay in order. That is why there is **one topic per
aggregate**, not one per event type: separate topics are separate logs, and
the Ledger could read a capture before its authorization. Unlike SQS, Kafka
keeps events after they are read, so a consumer can replay history to
rebuild state. Full comparison and AWS options (MSK, Kinesis, EventBridge):
`DESIGN.md`, decision 4.

**Verify:** `pay_A`'s authorized/captured/refunded all landed in partition 4
at offsets 0-2; a restarted consumer group resumed from its committed
offsets; a second group read all events independently.

**Talking points:** partitions cap consumer parallelism (6 partitions → at
most 6 consumers per group); you can add partitions but never remove them,
and adding them remaps keys. Two listeners (container vs host address),
because clients reconnect to the *advertised* address.

### 1.5 Health checks: liveness vs readiness

**Built:** `/health/live` (runs no checks) and `/health/ready` (runs checks
tagged `ready`) in ServiceDefaults, with a JSON body naming each check;
`AddPostgresReadiness("Payments")` checks the service's own database with a
2-second timeout and fails startup if the connection string is missing.

**Why:** liveness answers "should the orchestrator restart me?",
readiness "should the load balancer send me traffic?". A database in
liveness turns a database blip into a restart storm across every instance;
readiness just takes instances out of rotation until it recovers. Readiness
checks only what the instance can't work without (its own store), never
other services, or one failure cascades "not ready" up the call chain.

**Verify:** stop Postgres → live 200, ready 503 naming `postgres:Payments`;
start it → ready 200 again with no restart.

**Talking point:** health checks are a routing optimisation, not a
correctness guarantee. When the database itself is down every instance
fails together, and the service must fail fast with a 503 before any money
moves, so clients can retry idempotently.

### 1.6 Gateway: single entry point and load balancer

**Built:** a YARP Gateway on :7080: route `/api/payments/**` → cluster
`payment` with two instances (7082, 7092), round robin, active health checks
on `/health/ready` every 5 s (unhealthy after 2 failures), and an explicit
fail-open policy.

**Why:** clients know one address; which service and instance answers is
hidden. Routes say *which* requests, clusters say *where* (route ≈ ALB
listener rule, cluster ≈ target group, destination ≈ target).

**Verify:** responses alternate 7092/7082; kill 7092 → 502 on its turns for
~10 s, then all traffic goes to 7082.

**Talking points:** detection lag (interval × threshold) means some
requests always hit a dead instance first. When *every* instance is
unhealthy, YARP was failing open by default; that is now an explicit
decision (18), matching AWS ALB. In AWS this becomes ALB → Gateway tasks →
Service Connect; Amazon API Gateway was considered and not chosen
(decision 19). The production topology is in `DESIGN.md`.

### 1.7 Money: exact amounts

**Built:** `Money` in Contracts, a sealed record class of `long AmountMinor`
+ ISO-4217 `Currency`, with 15 tests.

**Why:** `double` can't represent 0.10 exactly (ten 10-cent payments sum to
0.9999999999999999), and a ledger that drifts never reconciles. Each
currency has its own decimals (USD 2, JPY 0, KWD 3), unknown currencies are
rejected, sub-minor amounts are refused rather than rounded, arithmetic is
same-currency and checked, and `Allocate` splits $10.00 three ways as
3.34 + 3.33 + 3.33, never losing a cent. Amounts are capped at 2^53 − 1, the
largest integer a JavaScript client reads exactly.

**Verify:** `dotnet test PayX.slnx` → 15 passed.

**Talking points:** deserializers, ORMs and `default(T)` can create objects
without running your constructor. As a record struct, System.Text.Json
built a `Money` with a null currency; the class closes that hole. Known
limits (remainder rule, currency table, FX) are recorded under decision 17.

### Phase 1 in one paragraph

Each service owns its data, and Postgres enforces it. AWS services run
locally through the real SDK. Kafka is a replayable, per-payment-ordered
log. Every service reports liveness and readiness, and the Gateway routes
around unready instances, failing open like an ALB when all of them fail.
Every amount is an exact integer with its currency. Nothing yet moves
money; Phase 2 adds who the users are.

---

## Phase 2 — Identity

**What the phase delivers:** the doc's `registerUser` and `authenticateUser`
APIs, and authentication enforced at the edge: every request after login
carries a signed token that the Gateway checks without calling Identity or
any database.

```
 register ─► Gateway ─(anonymous)─► Identity ─► payx_identity.users  (PBKDF2 hash, never the password)
 login    ─► Gateway ─(anonymous)─► Identity ─► 15-min ES256 JWT  (signed with Identity's private key)
 pay      ─► Gateway: verify signature with Identity's PUBLIC key (JWKS), iss, aud, exp, role
                 ├─ bad / missing token → 401        ├─ wrong role → 403
                 └─ ok → forwarded to Payment
```

### 2.1 Users table and the first migration

**Built:** the Identity service (:7081), a `User` entity, `IdentityDbContext`,
and the `InitialUsers` migration applied at startup as role `payx_identity`.

**Why:** the service owns its schema as versioned migrations in source
control; `__EFMigrationsHistory` makes each run exactly once. The database is
the last line of defence: check constraints refuse a mixed-case username or
email (so uniqueness is case-insensitive) and any role outside Customer,
Merchant, Ops. Roles are stored as text, ids are UUID v7 (time-ordered, so
inserts append to the index instead of scattering).

**Verify:** a mixed-case email, a duplicate username and role `Admin` are all
refused by Postgres; the second start logs "No migrations were applied".

**Talking point:** migrating at startup is a local convenience; in production
migrations run once as a separate deploy step, or every instance races to
migrate and a failed migration crash-loops all of them.

### 2.2 registerUser: password hashing

**Built:** `POST /api/users`: validate, normalise (trim, lower-case), refuse
`Ops` self-registration, hash, insert.

**Why:** the stored value is a PBKDF2-HMAC-SHA512 hash with a random 128-bit
salt and 100,000 iterations. Slow on purpose: each guess against a leaked
table costs 100,000 hash computations, where SHA-256 allows billions per
second on a GPU. The salt makes identical passwords hash differently. There
is no "does it exist?" query before inserting: two racing sign-ups would
both see "no". The unique index decides, and its violation becomes a 409.

**Verify:** same password for two users gives two different hashes; two
simultaneous sign-ups for one username give one 201 and one 409.

**Talking point:** check-then-insert is a race; let the constraint decide.

### 2.3 authenticateUser: signed tokens

**Built:** `POST /api/users/login` returns a 15-minute JWT signed with ES256.

**Why:** the token is the session (stateless authentication). Asymmetric
signing means only Identity can issue tokens; verifiers hold just the public
key, so a leaking verifier can't mint an Ops token (decision 20). Unknown
users get the same 401 body and, through a dummy hash check, the same
response time as a wrong password, so neither the message nor a stopwatch
reveals which usernames exist. Weaker stored hashes are upgraded on login,
the only moment the plain password is available.

**Verify:** wrong password and unknown user both 401 in ~40 ms; a
10,000-iteration hash becomes 100,000 after one login.

**Talking points:** a JWT is signed, not encrypted (never put secrets in it).
Each claim blocks something: `iss` other issuers, `aud` tokens meant for
other apps, `exp` stolen tokens living forever, the signature tampering,
`sub` acting as someone else. Short lifetime because a stateless token
can't be revoked early.

### 2.4 The Gateway validates tokens

**Built:** Identity publishes OIDC discovery and JWKS (public key only); the
Gateway validates signature, `iss`, `aud`, `exp` and ES256-only, denies by
default, opens `/api/users/**` explicitly, and limits `/api/payments/**` to
Customer or Merchant.

**Why:** bad tokens are rejected at the edge. Keys are discovered, not
copied: an unknown `kid` triggers a refetch, so key rotation needs no
redeploy. Accepting only ES256 blocks `alg: none` and algorithm-switching
forgeries. Deny-by-default means a route someone forgets to protect is
locked, not open. Fine-grained rules stay in the owning service
(decision 21).

**Verify:** no token 401; Customer and Merchant 200; a valid Ops token 403;
a tampered payload, `alg: none` and garbage all 401 ("The signature is
invalid").

**Talking points:** 401 means "who are you?", 403 means "I know who you
are, and no". Middleware order is part of the contract: the error formatter
must run before authentication, or 401/403 leave with empty bodies (found
and fixed in this module).

### Phase 2 in one paragraph

Passwords are never stored, only slow salted hashes, and uniqueness is
decided by the database, not by a racy check. A login yields a short-lived
token that only Identity can sign and anyone can verify. The Gateway checks
every token at the edge, refuses anything it can't verify, locks every
route by default, and separates "who are you?" (401) from "you may not"
(403). Phase 3 builds the outside world: the payment provider, card
network and bank, and the vault that keeps card numbers out of everything
else.
