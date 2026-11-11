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

---

## Phase 3 — PSP Simulator + Vault

**What the phase delivers:** the outside world PayX pays through, and the
one place card numbers live. The PSP simulator (:7088) plays Stripe/Adyen,
the card network and the issuing bank in one service: it authorizes,
captures and voids, dedupes retries by idempotency key, and can be told to
fail in exactly the ways real networks do. The Vault (:7084) turns a card
number into a token with KMS envelope encryption, so no other service ever
handles the number.

```
 checkout ─► Vault: tokenize ─► KMS data key ─► AES-GCM ─► DynamoDB payx-card-vault
                 └─ returns tok_…, brand, last4 (all anyone else ever sees)

 Phase 4:  Payment ─(tok_…)─► Vault: detokenize ─► PSP simulator
                                                     ├─ CardNetwork: Luhn, brand
                                                     ├─ IssuerBank: test cards, expiry, credit
                                                     ├─ hold on available credit (row lock)
                                                     └─ capture / void (two-phase)
           every POST carries an Idempotency-Key; X-Psp-Fault can break any call on purpose
```

### 3.1 The outside world: authorize with test cards

**Built:** `PayX.PspSimulator`, `POST /api/psp/authorizations`: Luhn check
and brand from the BIN (card network), then Stripe-style test cards for
generic decline, insufficient funds, expired card and incorrect CVC (issuer).

**Why:** every reliability lesson later in the build needs a counterparty
we control. The simulator deliberately uses none of PayX's own types, not
even `Money`: a real provider's API is its contract, not ours. A decline is
a **200** with `status: declined` and a code, because the request was
understood and the answer was "no"; a malformed card is a 400.

**Verify:** 13 scenarios (each test card, bad Luhn, unsupported brand, past
expiry); no full card number in the log.

**Talking point:** decline vs error is the first retry decision. An error
(5xx, timeout) may succeed on retry; a decline never will: the same card
gets the same answer, and hammering the issuer looks like card testing.

### 3.2 An authorization is a hold on available credit

**Built:** the simulator's own database `payx_psp`: `card_accounts` (an HMAC
fingerprint of the card, never the number, plus a credit limit) and
`authorizations`. Available credit = limit − live holds, computed, never
stored. The check and the hold happen in one transaction under
`SELECT … FOR UPDATE` on the card account.

**Why:** authorizing reserves money without moving it, the first half of
the doc's two-phase card flow. Two simultaneous authorizations each check
"is there enough?", both see yes, and both hold. The row lock makes the
check and the hold one atomic step per card (decision 22).

**Verify:** 25 truly simultaneous $1,000 authorizations on a $5,000 card:
without the lock $6,001 and $10,001 held, with it $4,001 every time.
(Parallel `curl` processes on Windows start too slowly to race; threads
released by a barrier do.)

**Talking point:** a stored "available balance" column is a second copy of
the truth that can drift; derive it from the holds, and lock while deciding.

### 3.3 Capture and void: two-phase card payments

**Built:** `POST …/{id}/capture {amount}` and `…/{id}/void`. Capture moves
the money (partial capture allowed, never more than authorized, the unused
part is released); void releases the hold. Legal transitions live on the
`Authorization` entity; Postgres `xmin` is the optimistic concurrency token
(decision 23); a check constraint keeps captured ≤ authorized.

**Why:** shops authorize at checkout and capture at shipping. Capture and
void racing on one authorization is rare, so instead of locking every
capture, both proceed and the database lets exactly one UPDATE win.

**Verify:** 20 simultaneous capture-vs-void rounds: one winner each time,
11 captured + 9 voided, never both. Capturing twice, voiding after capture
and over-capturing are all 409.

**Talking point:** pessimistic locking for hot, contended rows (a busy card
account, 3.2); optimistic for rare conflicts on one record (this). Know
which you have.

### 3.4 Status lookup and provider-side idempotency

**Built:** `GET /api/psp/authorizations/{id}`, and `[Idempotent]` on every
POST. The filter claims the `Idempotency-Key` (INSERT on its primary key),
runs the action and stores the status and body, **in one transaction**
(decision 24). A duplicate waits on the unique index, then replays the
stored answer with `Idempotent-Replayed: true`. The same key with a
different request (HMAC of method, path, arguments) is 422; no key is 400.

**Why:** this is the doc's "never charge twice" on the provider side. A
caller whose capture timed out must be able to retry safely: without
idempotency, the retry got 409 "already captured" (3.3) and the caller
couldn't tell success from failure. Storing the key in a separate commit
would leave a "work done, key not saved" window; one transaction closes it.

**Verify:** 10 simultaneous identical authorizations → 1 row, 9 replays; a
retried capture → the original 200, replayed; a stored 409 replays as 409;
a validation 400 isn't stored, so the fixed request runs.

**Talking points:** idempotency stores the *answer*, not just the key,
including refusals. `jsonb` re-sorts keys, so replays weren't byte-identical
until the column became `text` (found in this module).

### 3.5 Failure injection

**Built:** an `X-Psp-Fault` header (`latency=ms`, `error=5xx`,
`timeout-after-success`, combinable), honoured only when
`Psp:FaultInjection:Enabled` (Development).

**Why:** the failures this system is shaped around are rare and random on
real networks. Where each fault sits is the lesson (decision 25): `latency`
and `error` run **before** `[Idempotent]`, so an injected 5xx claims no key
and does no work; `timeout-after-success` is a result filter **after** the
commit, so the hold is real and the reply never arrives.

**Verify:** timeout-after-success with `curl -m 3` → curl gives up; status
lookup shows `authorized`; the same key replays the 200; a $2,000.01
follow-up on the $5,000 card declines, proving one $3,000 hold, not two.
`error=503` → a same-key retry runs for real (no replay header).

**Talking point:** "the network dropped the response — was the customer
charged?" now has a demonstrable answer: ask by status, or retry with the
same key. If the 503 sat inside the idempotency filter, it would be stored
and every retry would replay the failure forever.

### 3.6 Vault: envelope encryption with KMS

**Built:** `PayX.Vault` and `CardEncryptor`: KMS `GenerateDataKey` gives a
fresh AES-256 key per card, in the clear and wrapped; the card is
encrypted locally with AES-256-GCM; only the ciphertext, the wrapped key and
the wrapping key's ARN are kept. The record id (the token) is bound in as
KMS encryption context and GCM associated data (decision 26).
`AddAwsClient<T>()` in ServiceDefaults points the SDK at LocalStack.

**Why:** the doc asks where card details are encrypted. The master key never
leaves KMS and no card number is ever sent to it; a stolen table is
ciphertext plus wrapped keys, useless without `kms:Decrypt`, which IAM
grants to Vault only and CloudTrail logs per call. GCM, unlike CBC, detects
tampering.

**Verify:** `dotnet test tests/PayX.Vault.Tests` (LocalStack running): round
trip; stored form has no card number; same card twice → new key and
ciphertext; one flipped bit throws; a blob moved to another record is
refused by KMS.

**Talking point:** "why not just call KMS Encrypt on the card?" Every byte
would go to KMS, there is a 4 KB limit, and everything shares one key.
Envelope encryption keeps data local and keys central.

### 3.7 Vault: tokenization

**Built:** `POST /api/vault/cards` (number + expiry → 201 with token, brand,
last4, expiry), `GET /api/vault/cards/{token}` (the same, never the number),
a DynamoDB item per token written with `attribute_not_exists`, and
`DetokenizeAsync`, a C# method only: no HTTP endpoint returns a card
number. KMS and DynamoDB readiness checks (decision 27).

**Why:** tokenization is how PCI DSS scope stays one service wide. The
token is random (128 bits), not derived from the number, so it reveals
nothing and can't be recomputed. The only consumer of the real number will
be Vault's own call to the PSP (Phase 4: Payment → Vault → PSP). The
request DTO has no CVC field and refuses unknown fields, so a client
sending `cvc` gets a 400 instead of believing it was kept: PCI DSS forbids
storing it, even encrypted. *(Changed in 4.2a: the CVC is now accepted and
held encrypted in Redis until the first authorization, never stored with
the card.)*

**Verify:** tokenize → 201 + `Location`; lookup → brand/last4 only; `cvc`
in the body → 400; bad Luhn → 400 `invalid_number` (number not echoed);
`docker stop payx-localstack` → ready 503 naming both checks, live still
200; zero card numbers in the service log.

**Talking points:** a token vs a fingerprint: the token is random and
identifies one stored record; the PSP's fingerprint (3.2) is an HMAC built
to *match* the same card across records. Without paid persistence,
restarting LocalStack recreates the KMS key and an empty table, so old
tokens are gone (seen in this module; real KMS keys and tables persist).

### Phase 3 in one paragraph

Card payments are two-phase: authorize reserves credit under a lock,
capture moves it, void releases it, and the database, not application
code, guarantees two racing requests can't both win. The provider
remembers every answer under its idempotency key, so a retry after a lost
response replays what happened instead of doing it again, and failure
injection makes "lost response" reproducible on demand. Card numbers live
only in the Vault, each under its own KMS data key, bound to its token;
everything else sees a random token, the brand and the last four digits.
Phase 4 builds PayX's own payment service on top: a state machine,
orchestration across Vault and the PSP, and idempotency of its own.

---

## Phase 4 — Payment core

**What the phase delivers:** PayX's own payment service. A customer pays by
card token, a merchant captures, both can check a payment's status, and
every money-moving call is safe to repeat: a double-click, a retry after an
outage, or a retry after a crash continues the same payment instead of
paying twice. Every scenario, with its API calls and backend logic, is in
[`docs/PAYMENT-CORE.md`](docs/PAYMENT-CORE.md).

```
 Priya ─ POST /api/payments (Idempotency-Key) ─► Gateway ─► Payment
          save Created ─► risk (stand-in) ─► save RiskApproved
          ─► Vault (key pay_…:authorize: card + CVC added) ─► PSP ─► save Authorized / Declined
 Shop  ─ POST /api/payments/{id}/capture ──────► Payment ─► PSP (key pay_…:capture) ─► save Captured
 Both  ─ GET  /api/payments/{id} ──────────────► Payment (its own row)
```

### 4.1 The payment and its state machine

**Built:** the `payments` table and a `Payment` entity whose status changes
only through methods (`ApproveRisk`, `Authorize`, `Capture`…), each checking
one table of legal moves. `Money` maps to two columns; `xmin` guards
against two workers' conflicting moves; check constraints refuse
inconsistent rows (decision 28).

**Why:** an orchestrated saga (decision 3) needs one place that says how far
a payment got and what may happen next. No public setter means "make it
Captured" has to go through `Capture`, which asks the table first.

**Verify:** 48 tests, every from → to pair against a separately written
spec; capture vs void from two contexts → the second save fails; five bad
rows refused in psql.

**Talking point:** constraints check rows, not moves. A raw `UPDATE` from
Created to Voided with a made-up PSP reference passed: shown, and recorded
as the known gap.

### 4.2a–c Authorize: through Vault, by token

**Built:** Vault holds the CVC encrypted in Redis until its authorization
completes (4.2a); Vault authorizes by token, adding card and CVC and
relaying the PSP's answer (4.2b); Payment orchestrates it all behind
`POST /api/payments` (4.2c). Token validation moved to ServiceDefaults and
is now enforced by Payment too (customer = `sub`).

**Why:** Payment never sees a card number or CVC, so only Vault is in PCI
scope. The payment is saved **before** any outside call: if the process dies
after the bank said yes, a row already shows the attempt (decision 31). A
decline is a 201 with status `Declined`, not an error: the request worked;
the bank said no.

**Verify:** approved → 201 Authorized; decline card → 201 Declined;
Merchant token → 403; no token straight to Payment → 401; unknown currency
→ 400 (it was a 500: `Money` threw while the JSON was read).

**Talking point:** split from one planned module into three because it held
three ideas (sensitive data with a lifetime; a proxy that adds card data;
orchestration).

### 4.3 Capture

**Built:** `POST /api/payments/{id}/capture`, Merchant only and only its own
payments, partial or full, straight to the PSP (no card data, so no Vault
hop: decision 32), key `{paymentId}:capture`.

**Why:** every refusal we can predict (state, amount) is checked **before**
the PSP call, so the bank never captures something we'd refuse to record.

**Verify:** partial $30 of $50 → PSP agrees; capture again → 409; another
shop → 404; PSP down → 502, payment stays Authorized; the same capture after
recovery → Captured, one capture at the PSP. The tests caught that PSP
refusals crashed Payment: `status` is text in a success and a number in a
ProblemDetails.

### 4.4 checkPaymentStatus

**Built:** `GET /api/payments/{id}` for the payment's customer or merchant;
anyone else gets the same 404 as a missing id.

**Talking point:** the 201's `Location` named the internal instance
(`localhost:7082`) through the Gateway; relative URLs are always right
behind a proxy.

### 4.5 Payment's own Idempotency-Key

**Built:** `[Idempotent]` on authorize and capture: key scoped to the
caller, claimed with an INSERT **committed before the work**, definitive
answers stored and replayed (`Idempotent-Replayed`), a different request
with the same key → 422, a duplicate of a running request → 409
`request_in_progress`.

**Why:** unlike the PSP (3.4), the claim can't share one transaction with
the work, because the work calls Vault and the PSP, and a transaction must
never stay open across network calls. And a 5xx is never stored: it isn't
an outcome, and replaying it would stop the caller ever finding out.

**Verify:** double-click → one payment; two requests at the same moment →
201 + 409; 10 simultaneous claims → 1 winner.

### 4.6 Unfinished attempts resume the same payment

**Built:** a 30 s lock on each claim, released on a 5xx, lapsing on a
crash; a retry takes the key over with one conditional UPDATE (1 winner of
10). The key stores its **payment id in the same transaction that creates
the payment**, and authorize runs each step only from its starting state,
so a retry resumes the same payment (decision 33).

**Why:** deleting a failed key would let the retry create a second payment
with a new PSP key; if the first attempt had reached the bank, Priya would
carry two holds.

**Verify:** PSP down → 502 → retry → the same payment resumed (1 row).

### 4.7 Keys end to end

**Built:** Vault keeps the CVC until the PSP's answer is definitive, then
forgets it, and remembers that answer per token + key for 24 h. The full
chain: caller key → payment → `{paymentId}:authorize` → Vault → PSP.

**Why:** in 4.2b the CVC was used up before the call, so after a timeout or
a lost answer, a retry got 409 `cvc_unavailable` instead of the outcome.

**Verify:** PSP down → 502 (CVC kept) → PSP back, same key → **201
Authorized, one payment, one authorization at the PSP**; CVC gone
afterwards; Vault's stored answer replays without calling the PSP.

**Talking point:** "read once" was the right instinct for a secret, but it
fights retries. The CVC's life now ends when its *job* ends (a definitive
answer), not when it is first read.

### Phase 4 in one paragraph

PayX now owns its payments: a state machine that only allows real moves,
an orchestrator that writes before it calls, and the card kept behind
Vault. Every money-moving call carries an idempotency key, and each hop
turns it into a stable key of its own, so a double-click, an outage or a
crash ends with one payment and one hold. What remains is everything that
happens *without* a retry: Phase 5 replaces the risk stand-in, and Phase 6
adds events (outbox, Kafka), retries with backoff, and the `UNKNOWN` state
with a resolver for payments nobody comes back for.

## Phase 5 — Risk

**What the phase delivers:** the doc's "fraud detection + risk check" as a
real service, consulted before every authorization, plus everything that
keeps checkout working when it misbehaves. A risky payment is challenged
or blocked before the bank is ever asked; a slow Risk costs a checkout at
most 2 seconds; a dead Risk is skipped quickly, and small payments keep
flowing while large ones wait. The whole design in one place (architecture,
flow, states, risk logic, circuit breaker, every scenario):
[`docs/PAYMENT-CORE-AND-RISK.md`](docs/PAYMENT-CORE-AND-RISK.md).

```
 Priya ─ Pay ─► Payment: save Created
                  ├─ Vault: GET card → country (from the BIN)
                  └─ FallbackRiskCheck ─► RiskClient ─[circuit breaker ─ 2 s timeout]─► Risk
                                                                                     ├ velocity (Redis sliding window)
                                                                                     ├ rules → score 0-100 + reasons
                                                                                     └ thresholds → Allow / Challenge / Block
                  Allow     → save RiskApproved → Vault → PSP (as in Phase 4)
                  Challenge → save Challenged   (bank never asked)
                  Block     → save RiskBlocked  (bank never asked)
                  no answer → small amount: Allow (marked fallback) · else 502/503/504, payment stays Created
```

### 5.1 Risk service: a rule-based score with reasons

**Built:** `PayX.Risk` (port 7083, internal: no Gateway route),
`POST /api/risk/assessments`. `RiskRules.Score` is a pure function: amount
bands per currency (unconfigured currency = `amount_unrated`, not safe),
card country (unknown / high-risk / cross-border). Score 0-100, capped,
with every rule that fired as a reason. Decision 11: fraud detection and
risk check are one service.

**Why:** a score with reasons can be explained to an analyst, a customer
and a dispute; a bare number can't. Weights are code (they define what a
score means); thresholds are configuration read through `IOptionsMonitor`,
so a policy edit applies to the next request.

**Verify:** 13 tests. `curl` the endpoint: $50 US → 0; $1,500 GB → 35
(`amount_elevated` + `cross_border`); $9,000 XR → 100.

**Talking point:** a pure scoring function can be re-run on yesterday's
payments to test a new threshold before it goes live.

### 5.2 Velocity: a sliding window in Redis

**Built:** `SlidingWindowCounter`: a sorted set per customer
(`risk:velocity:customer:{id}`), one Lua script that trims entries older
than the window, adds the payment with `ZADD NX`, sets `PEXPIRE` and counts.
≥3 payments in 10 minutes → +15, ≥5 → +40.

**Why:** card testing (a fraudster trying stolen cards) shows up as bursts.
A fixed window (`INCR` per 10-minute bucket) resets on the clock, so 5 at
10:09 and 5 at 10:10 look like "5 per window" twice. The sliding window
always looks back the full 10 minutes.

**Verify:** 11 tests, 5 against the real Redis (counts, a repeated payment
id counted once, expiry, separate customers, key TTL).

**Talking point:** the member is the payment id, so a retried assessment
doesn't count twice; the time comes from Redis's `TIME`, so instances with
skewed clocks agree.

### 5.3 Score → allow / challenge / block

**Built:** `RiskPolicy.Decide` with `Risk:Decision` thresholds
(ChallengeAt 40, BlockAt 80), separate from the scoring rules and validated
at startup (contradictory thresholds refuse to start).

**Why:** detection ("how suspicious?") and decision ("how much risk do we
accept?") change for different reasons and belong to different people:
fraud analysts tune rules, the business sets its appetite. Lowering
ChallengeAt to 30 turned a score of 35 into Challenge with no rule touched.

**Talking point:** three outcomes, not two. Between "fine" and "fraud" is a
band where blocking loses good customers and allowing lets fraud through; a
challenge asks for something a fraudster with only card details can't give.

### 5.4 Card country from the BIN

**Built:** `CardNumber.CountryOf` in Vault: the leading digits looked up in
a BIN table, 8 digits first, then 6. Stored with the card like brand and
last 4, returned as `country`. Test cards: 4242… US, 4000 0082 6000 0000 GB,
4000 0099 0000 0004 XR.

**Why:** Risk needs the issuing country, and only Vault ever sees the
number. Derived once at tokenization so reading it later costs no KMS call.

**Talking point:** BINs have been 8 digits since 2022; the GB and XR test
cards share their first 6 digits with the US range, so a 6-digit lookup
would get them wrong. (A test caught a typo in the GB prefix.)

### 5.5 Payment asks Risk before the bank

**Built:** `RiskClient` replaces the allow-everything stand-in. It asks
Vault for the card's country, then Risk. New final status `Challenged`;
`risk_score` and `risk_reasons` columns on `payments`.

**Why:** the country comes from Vault, not the browser, because a fraudster
would send "US". Score and reasons are stored for analysts and disputes
and never returned to the customer, who would learn which rule to dodge.

**Verify:** through the Gateway: $50 US → Authorized; $6,000 GB →
RiskBlocked (velocity added points); $50 XR → Challenged. Risk stopped →
502, payment Created; Risk back, same key → the same payment Authorized.

### 5.6 Timeout

**Built:** the Risk client's `HttpClient` runs a Polly v8 resilience
pipeline (`Microsoft.Extensions.Http.Resilience`) with a 2 s timeout.

**Why:** a slow dependency is worse than a dead one. Every waiting checkout
holds a thread, a DB connection and an idempotency lock; enough of them and
Payment falls over although Payment did nothing wrong.

**Verify:** `docker pause payx-risk`, then pay: 504 `risk_timeout` in 2.1 s
(was up to 100 s); unpause, same key → the same payment Authorized.

### 5.7 Circuit breaker and fallback

**Built:** a circuit breaker outside the timeout (≥5 calls in 30 s, half
failed → open 15 s, then one trial call), and `FallbackRiskCheck` around the
client (decision 12): Risk without an answer and an amount under about $100
→ Allow, stored with no score and reason `risk_unavailable_fallback`;
anything larger → the retryable error, payment left Created.

**Why:** fail open makes a Risk outage a fraud window; fail closed makes it
a checkout outage. A threshold keeps cheap payments flowing and holds
expensive ones. The breaker stops spending 2 s per checkout on a service
that is known to be down, and stops piling load on it while it recovers.

**Verify:** the outage drill (Risk paused): four $500 payments 504 after
~2 s each, the 5th and 6th 503 `risk_circuit_open` in 0.03 s, a $50 payment
Authorized via the fallback; unpaused, after the break everything is normal.

**Talking point:** order inside the pipeline matters. The breaker wraps the
timeout, so timeouts count as failures; the other way round, a hung Risk
would never trip the breaker.

### Phase 5 in one paragraph

Payments are now judged before money is asked for: Risk scores each one
from its amount, the card's country and the customer's recent burst of
payments, explains the score, and turns it into allow, challenge or block
by thresholds the business owns. Payment calls it with a 2-second limit,
stops calling it while it is failing, and lets only small payments through
unchecked when it can't answer. The bank never sees a challenged or blocked
payment. Still open: the challenge itself (3-D Secure) isn't built, Risk
doesn't keep an audit record of decisions, and velocity is per customer,
not per card. Phase 6 makes payments announce themselves (outbox, Kafka)
and adds retries and the `UNKNOWN` state.

## Phase 6 — Reliability

**What the phase delivers:** correctness when things fail after the customer
clicked Pay. Every payment status change is announced on Kafka, exactly as
it happened and never lost (outbox + relay); calls to the bank survive
blips (keyed retries); a bank that goes silent no longer leaves a payment
pretending nothing happened (`Unknown`), and a resolver finds out what
really happened for payments nobody comes back for. Consumers get a loop
that one bad message can't block (dead-letter topic). The full design, with
diagrams and every scenario: [`docs/RELIABILITY.md`](docs/RELIABILITY.md).

```
 Pay ─► Payment ──[ one transaction: payment row + outbox row ]
           │                         └─► relay (one at a time) ─► Kafka payx.payments ─► consumers
           │                                                                  └ can't process ─► payx.payments.dlq
           └─► Vault ─[ retries: 3 s attempts, backoff + jitter, 10 s total ]─► PSP
                 no answer ─► Unknown (202) ─► retry with same key, or the resolver asks the PSP by reference
```

### 6.1 Transactional outbox

**Built:** every `Payment.MoveTo` records a numbered status change;
`PaymentsDbContext.SaveChangesAsync` writes each as an `outbox_messages` row
(`PaymentEvent` JSON, key = payment id) in the same transaction. The event
contract lives in `PayX.Contracts/Events`.

**Why:** saving and publishing are two writes to two systems; a crash
between them loses or invents an event. One transaction makes them one
fact. No code path writes events itself, so none can forget one.

**Verify:** pay + capture → three outbox rows in order; a save the database
refuses writes neither the status nor the event (test).

### 6.2 Outbox relay

**Built:** `Jobs/OutboxRelay` runs `OutboxPublisher` passes: advisory lock
(one relay across all instances), oldest unpublished first, produce to
`payx.payments` with key = payment id, wait for Kafka's confirmation, mark
published. Producer with `acks=all` and idempotence.

**Why:** at-least-once delivery in order. Marking after confirming may
publish twice (consumers dedupe); marking first could lose an event.

**Verify:** stop Kafka, pay and capture: both work, events wait; start
Kafka: they are published within seconds.

**Talking point:** the request path never touches Kafka, so a Kafka outage
delays announcements, never payments.

### 6.3 Retries with backoff and jitter

**Built:** `AddKeyedRetries` (ServiceDefaults): total 10 s ⊃ 2 retries
(5xx, 408, 429, no answer; waits from 200 ms, doubling, jittered) ⊃ 3 s per
attempt. On Vault → PSP and Payment → PSP; not on Payment → Vault.

**Why:** a blip shouldn't fail a payment, and the same key makes repeating
a POST safe. One retry layer only: nested retries multiply (27 attempts).

**Verify:** PSP frozen 4 s mid-payment → attempt cut at 3 s, retried, 201
in 4.4 s, the PSP holds one key.

### 6.4 The Unknown state

**Built:** status `Unknown` (RiskApproved → Unknown → Authorized / Declined).
A timeout makes the payment Unknown and the answer **202 Accepted**, which
is not stored under the key, so a retry asks the bank again with the same
key.

**Why:** a timeout hides three cases (never arrived, failed, succeeded with
the answer lost). Calling it failed risks a double charge; calling it
success books money the bank never approved.

**Verify:** PSP frozen 12 s → 202 Unknown; same key afterwards → 201
Authorized, same payment.

### 6.5 Resolver

**Built:** the payment id travels to the PSP as a merchant reference; the
PSP can be asked by it. `StuckPaymentResolver` (every 15 s) takes payments
at Unknown or RiskApproved for over 60 s and records the PSP's answer:
Authorized, Declined, or the new final status `Failed` (no record, nothing
held). An unreachable PSP proves nothing.

**Why:** customers close tabs and processes die; a hold must never sit on a
card with no record of it on our side.

**Verify:** nobody retrying: PSP stopped → Failed after 60 s; a lost answer
(the PSP holds the authorization) → Authorized with the PSP's id.

### 6.6 Dead-letter topic

**Built:** `DeadLetteringConsumer<THandler>` (ServiceDefaults): commit after
handling; malformed → `payx.payments.dlq` at once; other failures retried in
place with doubling waits, then dead-lettered; the copy carries why and
where, and is confirmed before the commit.

**Why:** Kafka commits per partition, so one message that always fails
blocks a sixth of all payments, or gets skipped and lost.

**Verify:** test against real Kafka: good / poison / good → both good
handled, poison in the DLQ, committed offset 3.

### Phase 6 in one paragraph

A payment's every move is now written with its announcement in one
transaction and published to Kafka in order, at least once, whatever
crashes in between. Calls to the bank retry safely because they carry the
same key, within budgets that nest. When the bank goes silent, PayX says
`Unknown` instead of guessing, and either the customer's retry or the
resolver gets the bank's real answer. Consumers can't be blocked by one bad
message. What is still missing is the consumer itself: Phase 7 builds the
Ledger, which books these events into a double-entry journal, dedupes them,
and keeps merchant balances.

## Phase 7 — Ledger and wallets

**What the phase delivers:** the books. Every payment event from Phase 6 is
booked into a double-entry, append-only journal; balances (the doc's
"wallet") change in the same transaction as the postings they sum; customers
and merchants read their history and balances from those books. The doc's
rule "a message is only marked as consumed after the transaction is …
recorded in the wallet and ledger" is now real end to end. Full design,
diagrams, scenarios and findings: [`docs/LEDGER.md`](docs/LEDGER.md).

```
 Kafka payx.payments ─► Ledger consumer (group payx-ledger, dead-lettering loop)
      │                     └─[ one transaction: lock accounts ─► entry + postings (sum 0)
      │                                          ─► balances += postings ─► processed_events ]
      │                     └─► then commit the offset
      └ can't book ─► payx.payments.dlq
 GET /api/ledger/transactions · GET /api/ledger/balance ─► the same tables, per caller
```

### 7.1 Double entry

**Built:** new service `PayX.Ledger` (7085, `payx_ledger`). Accounts per
(type, owner, currency): `PspClearing`, `MerchantPending`,
`MerchantAvailable`, and memo accounts `CustomerHolds` /
`MerchantAuthorized` for holds. A journal entry = signed postings summing
to zero per currency, checked by `JournalEntry.EnsureBalanced` and by a
deferred constraint trigger at COMMIT; a composite FK keeps each posting
in its account's currency.

**Why:** money can't appear or vanish unnoticed; if a bug tries, the sum
isn't zero and the database refuses, whoever wrote the SQL.

**Verify:** raw SQL +5,000 / −4,999 → refused at commit (`ck_entry_balanced`).

### 7.2 Append-only

**Built:** trigger `ledger_refuse_change` refuses UPDATE / DELETE /
TRUNCATE on the journal for every role, owner included; the service runs as
`payx_ledger_app` (SELECT/INSERT), migrations as the owner.

**Why:** the doc's "immutable entry … auditable trail". Two locks because
the owner can disable a trigger, and grants alone protect nothing from the
owner. Corrections are reversing entries (Phase 9).

**Verify:** app role UPDATE → `permission denied`; owner UPDATE →
`restrict_violation` from the trigger.

**Talking point:** "enforced in the database, not just by convention" means
the credential in every request's path can't change history even if the
code (or an injected statement) tries.

### 7.3 Consumer

**Built:** `PaymentEventHandler` inside the 6.6 `DeadLetteringConsumer`.
`PostingRules` (pure): Authorized → hold; Captured → release the whole hold
and book the captured amount (PSP owes us / we owe the merchant); Voided →
release. Events are validated (missing fields, unknown currency, captured >
authorized → poison). A capture without a booked authorization is parked,
never guessed. `PaymentEvent.Json` is now shared by producer and consumer.

**Why:** commit the offset only after the booking commits: at least once.

**Verify:** the new group booked all 1,776 retained events, lag 0, DLQ
empty, every entry balanced; a $50 auth / $30 capture matches exactly.

### 7.4 Idempotent consumer

**Built:** `processed_events` (event id, payment, entry) inserted in the
same transaction as the entry; a partial unique index allows one
Authorization / Capture / Void per payment.

**Why:** at least once means duplicates are certain. The inbox catches a
redelivery (and a race: the primary key lets one of two commit); the
business key catches a replay with a new event id.

**Verify:** offsets reset to earliest and the topic replayed twice: entries
674 before and after, 0 duplicate steps.

**Found:** the 6.6 loop never called `Close()`, so every stop left the
group rebalancing for ~45 s (the reset was refused). Fixed.

### 7.5 Balances in the same transaction

**Built:** `accounts.balance_minor`, updated next to the postings after
locking the accounts `FOR UPDATE` in id order; column grant `UPDATE
(balance_minor)` only. `BalanceAudit`: balance ≠ postings, and the trial
balance per currency (must be 0).

**Why:** decision 10, a deliberate deviation from the doc, whose
wallet-then-ledger is two writes a crash can split. The lock prevents lost
updates; the order prevents deadlocks.

**Verify:** without the lock, 25 concurrent $10 captures to one merchant
left **$50 instead of $250**; with it, exact. Live: 0 mismatches, trial
balance 0.

### 7.6 getTransactionHistory

**Built:** `GET /api/ledger/transactions`: the caller's own entries, newest
first, optional `from`/`to`, keyset paging (`before` / `nextBefore`). JWT
validated by the Ledger itself; Gateway route `/api/ledger/**`.

**Why:** keyset paging costs the same on page 500 as on page 1 and can't
skip or repeat rows while new bookings land.

**Verify:** customer and merchant both see Capture 30.00, Authorization
50.00; another merchant sees nothing; no token 401.

### 7.7 Balance API

**Built:** `GET /api/ledger/balance`: merchant `authorized` / `pending` /
`available` per currency, customer `onHold`, from the stored balances.

**Why:** the doc's "available vs pending": captured money isn't payable
until the PSP settles it (Phase 8 moves pending → available).

**Verify:** pay $50, capture $30 → merchant 0 / 30.00 / 0, customer 0 on hold.

### Phase 7 in one paragraph

Payment events now land in books that can't lie: every entry balances to
the cent (checked again by the database at commit), nothing in history can
be changed by anyone, and every balance moves in the same transaction as the
postings it sums, under a lock, so it equals its history by construction.
The consumer acknowledges an event only after the booking commits and books
each event exactly once however often Kafka delivers it, which a full
replay of the topic proved. Customers and merchants read history and
balances straight from those books. What the ledger can't know yet is
whether the PSP agrees: Phase 8 brings the settlement file, moves pending
money to available, and reconciles the books against the PSP's own record.
