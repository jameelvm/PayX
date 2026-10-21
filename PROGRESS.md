# PayX — build progress

## ▶ How to resume

Say this to Claude at the start of the next session:

> Read PROGRESS.md and CLAUDE.md in C:\System Design\Payment System\App,
> then continue from "Next up". Build in short modules, pausing after each
> one so I can review before you continue.

---

**Purpose of this file:** if a session is lost, this is the single place that
says where the build stopped and what happens next. Update the *Progress
tracker*, *Current state* and
*Next up* at the end of every session.

**Standing conventions**

1. At the end of every phase, update `README.md` with teaching-style
   documentation of what that phase built — architecture, each module, the
   reasoning, verification commands and design talking points.
2. Also extend `DESIGN.md` — decisions, failure-mode rows (marked verified),
   Q&A entries, doc-to-code map.
3. Every phase leaves `dotnet build PayX.slnx` green.
4. Build in short modules, one concept each, pausing after every module.
5. Tick the module in the *Progress tracker* below: 🔍 when built, ✅ when
   the owner approves and it is committed.
6. **Immediately before every commit:** `dotnet build PayX.slnx
   --no-incremental` (0 warnings, 0 errors) and `dotnet test PayX.slnx`,
   (needs LocalStack up: Vault's tests call its KMS), and **check its exit
   code**, not just the summary lines: a test DLL that fails to load prints
   no "Failed!" line (seen in 4.2c, Smart App Control),
   then `git diff --cached --stat` to check that only expected files are
   staged. Added after commit `bd1a26f` shipped a broken build: an editor
   save removed Identity's JWT package reference 26 s before the commit, and
   `git add -A` picked it up without a rebuild.

**Module size rule (owner's requirement — the point is mastery, not speed)**

- **One concept per module.** If explaining it needs the word "and" between
  two design ideas, it's two modules.
- **Small enough to read in one sitting:** typically 1–4 files, roughly
  ≤150 lines of meaningful code (excluding generated/boilerplate files, which
  are called out as such).
- **Every module ends with a review note** in chat: *what to read* (files in
  reading order), *the logic* (why each piece exists, which doc section it
  comes from), *how to run/verify it*, and *what to notice* (the design
  lesson, plus what breaks if you remove it).
- **No module depends on code the owner hasn't reviewed yet.** No jumping ahead
  "while I'm here".

---

## Progress tracker

**Overall: 25 / 70 modules done.** Phase 10 and 11 counts are estimates and may change when we get there.

Legend: ✅ done and committed · 🔍 built, awaiting owner review · 🔄 phase in progress · ⬜ not started

| Phase | Name | Doc concept | Done | Status |
|---|---|---|---|---|
| 0 | [Design](#phase-0-design) | requirements, estimates (+ corrections), HLD | 2 / 2 | ✅ |
| 1 | [Local substrate](#phase-1-local-substrate) | — | 7 / 7 | ✅ |
| 2 | [Identity](#phase-2-identity) | `registerUser`, `authenticateUser` | 4 / 4 | ✅ |
| 3 | [PSP Simulator + Vault](#phase-3-psp-simulator--vault) | payment gateway, card network, issuer; card-data encryption | 7 / 7 | ✅ |
| 4 | [Payment core](#phase-4-payment-core) | payment service, idempotency | 5 / 9 | 🔄 |
| 5 | [Risk](#phase-5-risk) | fraud detection, risk check, fallback | 0 / 5 | ⬜ |
| 6 | [Reliability](#phase-6-reliability) | transaction completion, transient failures | 0 / 6 | ⬜ |
| 7 | [Ledger + wallets](#phase-7-ledger--wallets) | wallet, ledger, history, balance | 0 / 7 | ⬜ |
| 8 | [Settlement + Reconciliation](#phase-8-settlement--reconciliation) | reconciliation system | 0 / 4 | ⬜ |
| 9 | [Refunds + disputes](#phase-9-refunds--disputes) | dispute management | 0 / 3 | ⬜ |
| 10 | [Frontend](#phase-10-frontend) | merchant's online store, mobile access | 0 / 5 | ⬜ |
| 11 | [Evaluation](#phase-11-evaluation) | NFRs | 0 / 11 | ⬜ |

### Phase 0: Design

| | Module | What it builds | Concept |
|---|---|---|---|
| ✅ | 0.1 | Design draft: CLAUDE / DESIGN / PROGRESS / ARCHITECTURE diagrams | — |
| ✅ | 0.2 | Owner confirms open decisions (DESIGN §1: 3, 4, 10, 11, 13, 14): 3, 13, 14 accepted 2026-10-20; 4, 10, 11 confirmed at the start of Phases 6, 7, 5 | — |

### Phase 1: Local substrate

| | Module | What it builds | Concept |
|---|---|---|---|
| ✅ | 1.1 | Solution skeleton: `PayX.slnx`, Contracts, ServiceDefaults, hello service | project layout & dependency direction |
| ✅ | 1.2 | Postgres: one database + one login role per service | database-per-service, enforced |
| ✅ | 1.3 | Redis + LocalStack (S3, DynamoDB, KMS) + bootstrap script | local AWS emulation, same SDK as prod |
| ✅ | 1.4 | Kafka (KRaft) + Kafka UI; topics, keys, partitions, offsets from the CLI | log vs queue, per-key ordering |
| ✅ | 1.5 | ServiceDefaults: health checks + config wiring | liveness vs readiness |
| ✅ | 1.6 | Gateway with YARP routing | single entry point |
| ✅ | 1.7 | `Money` value type + tests | integer minor units |

### Phase 2: Identity

| | Module | What it builds | Concept |
|---|---|---|---|
| ✅ | 2.1 | Users table + EF Core migration | schema ownership |
| ✅ | 2.2 | `registerUser` with password hashing | never store passwords |
| ✅ | 2.3 | `authenticateUser` → JWT | stateless auth |
| ✅ | 2.4 | Gateway validates JWT; customer / merchant / ops roles | auth at the edge |

### Phase 3: PSP Simulator + Vault

| | Module | What it builds | Concept |
|---|---|---|---|
| ✅ | 3.1 | PSP sim: `authorize` with test cards | the outside world |
| ✅ | 3.2 | PSP sim remembers authorizations as holds on available credit | an authorization is a reservation; races on balances |
| ✅ | 3.3 | `capture` + `void` | two-phase card payments |
| ✅ | 3.4 | `status` lookup + idempotency on the PSP side | provider-side dedupe |
| ✅ | 3.5 | Failure injection (latency, 5xx, timeout-after-success) | testing partial failure |
| ✅ | 3.6 | Vault: KMS envelope encryption of one card number | envelope encryption |
| ✅ | 3.7 | Vault: tokenize / detokenize API, CVV never stored | tokenization, PCI scope |

### Phase 4: Payment core

| | Module | What it builds | Concept |
|---|---|---|---|
| ✅ | 4.1 | `payments` table + state machine with legal transitions | explicit state machine |
| ✅ | 4.2a | Vault holds the CVC until the first authorization | sensitive data: encrypted, TTL, read once |
| ✅ | 4.2b | Vault authorizes at the PSP by token | the vault as the only path to card data |
| ✅ | 4.2c | `authorizePayment` happy path (risk stand-in) | orchestration |
| ✅ | 4.3 | `capturePayment` | authorize vs capture |
| ⬜ | 4.4 | `checkPaymentStatus` | status API |
| ⬜ | 4.5 | Idempotency-Key table: insert or return stored response | never charge twice |
| ⬜ | 4.6 | Concurrent duplicate requests (in-progress lock) | races on retries |
| ⬜ | 4.7 | Propagating idempotency keys to the PSP (incl. the 4.2b gap: a same-key retry through Vault must replay, not 409) | end-to-end idempotency |

### Phase 5: Risk

| | Module | What it builds | Concept |
|---|---|---|---|
| ⬜ | 5.1 | Rule-based score (amount, card country) | risk scoring |
| ⬜ | 5.2 | Velocity counter in Redis (sliding window) | real-time signals |
| ⬜ | 5.3 | Score → allow / challenge / block | decision vs detection |
| ⬜ | 5.4 | Payment calls Risk with a Polly timeout | timeouts |
| ⬜ | 5.5 | Circuit breaker + amount-threshold fallback | fallback |

### Phase 6: Reliability

| | Module | What it builds | Concept |
|---|---|---|---|
| ⬜ | 6.1 | Outbox table, written in the payment's transaction | no dual writes |
| ⬜ | 6.2 | Outbox relay → Kafka | never lose an event |
| ⬜ | 6.3 | Retries with exponential backoff + jitter on PSP calls | retry strategy |
| ⬜ | 6.4 | `UNKNOWN` state on timeout | timeout ambiguity |
| ⬜ | 6.5 | Resolver job | resolving the unknown |
| ⬜ | 6.6 | Poison messages → dead-letter topic | unblocking a partition |

### Phase 7: Ledger + wallets

| | Module | What it builds | Concept |
|---|---|---|---|
| ⬜ | 7.1 | Accounts + journal + postings; sum-to-zero check | double-entry |
| ⬜ | 7.2 | Append-only enforcement (trigger + grants) | immutability |
| ⬜ | 7.3 | Kafka consumer; commit offset after the DB commit | at-least-once delivery |
| ⬜ | 7.4 | Consumer dedupe (`processed_events`) | idempotent consumers |
| ⬜ | 7.5 | Balances updated in the same transaction (wallet) | consistent projections |
| ⬜ | 7.6 | `getTransactionHistory` | history |
| ⬜ | 7.7 | Balance API (available vs pending) | balance management |

### Phase 8: Settlement + Reconciliation

| | Module | What it builds | Concept |
|---|---|---|---|
| ⬜ | 8.1 | PSP sim writes the end-of-day settlement file to S3 | settlement |
| ⬜ | 8.2 | Recon reads and parses it | batch input |
| ⬜ | 8.3 | Matching against the ledger | reconciliation |
| ⬜ | 8.4 | Discrepancy classification + report | handling mismatches |

### Phase 9: Refunds + disputes

| | Module | What it builds | Concept |
|---|---|---|---|
| ⬜ | 9.1 | Refund flow + reversing entries | corrections without edits |
| ⬜ | 9.2 | PSP webhooks: signature check + dedupe | trusting inbound events |
| ⬜ | 9.3 | Chargeback lifecycle | disputes |

### Phase 10: Frontend

| | Module | What it builds | Concept |
|---|---|---|---|
| ⬜ | 10.1 | Checkout page | — |
| ⬜ | 10.2 | Double-click / retry demo | idempotency, visibly |
| ⬜ | 10.3 | Customer transaction history | — |
| ⬜ | 10.4 | Merchant dashboard (balances) | — |
| ⬜ | 10.5 | Ops console (failure injection, recon results) | — |

### Phase 11: Evaluation

| | Module | What it builds | Concept |
|---|---|---|---|
| ⬜ | 11.1 | Drill: double-click Pay | idempotency |
| ⬜ | 11.2 | Drill: PSP times out after authorizing | UNKNOWN + resolver |
| ⬜ | 11.3 | Drill: Payment crashes after commit, before publish | outbox |
| ⬜ | 11.4 | Drill: Ledger consumer crashes mid-processing | offsets + dedupe |
| ⬜ | 11.5 | Drill: Kafka down | outbox buffering |
| ⬜ | 11.6 | Drill: Risk service down | circuit breaker + fallback |
| ⬜ | 11.7 | Drill: duplicate / out-of-order webhooks | state machine guards |
| ⬜ | 11.8 | Drill: settlement file disagrees with ledger | reconciliation |
| ⬜ | 11.9 | Drill: authorized but never captured | capture deadline |
| ⬜ | 11.10 | k6 load test vs the 1,400 TPS estimate | performance |
| ⬜ | 11.11 | End-to-end traces (OpenTelemetry) | observability |

---

## Current state

**Last updated:** 2026-10-19
**Phase 0 — design. Draft written, awaiting owner review.** Source PDFs read
(the schema/detailed-design figures are blank in the PDF export, so the
storage schema in `DESIGN.md` is inferred from the APIs and text). Stack,
service boundaries and 17 proposed decisions are in `DESIGN.md` §1.
**Phase 1 — complete: all seven modules built, verified and committed.** Wrap-up done: README Phase 1 teaching section, RUNNING.md, DESIGN §5 doc-to-code map.
- 1.1 solution skeleton: `dotnet build PayX.slnx` green; `GET :7082/api/ping`
  → 200; unknown route → 404 `application/problem+json`. Committed.
- 1.2 Postgres (`payx-postgres`, host port 5434): six databases, each owned by
  its own login role, `CONNECT` revoked from `PUBLIC`. Verified: each role
  connects to its own database; `payx_payments` → `payx_ledger` and
  `payx_identity` → `payx_payments` are both refused with `permission denied
  … CONNECT privilege`. Committed.
- 1.3 Redis (`payx-redis`, 6381, `noeviction`) + LocalStack (`payx-localstack`,
  4568, `s3,dynamodb,kms`) with an idempotent bootstrap script: S3
  `payx-settlement-files`, DynamoDB `payx-card-vault` / `payx-risk-decisions`,
  KMS `alias/payx-card-vault`. Verified: all resources listed, KMS
  encrypt → decrypt round trip of a test card number, script re-runs cleanly
  on restart. **Found:** LocalStack state is not persisted (no paid
  persistence) — a restart recreates everything, including a *new* KMS key
  under the same alias. Harmless now; matters in Phase 3 (anything encrypted
  before a restart becomes undecryptable). Committed.
- 1.4 Kafka 4.1 single KRaft broker (`payx-kafka`; containers `kafka:9092`,
  host `localhost:9094`), one-shot `kafka-init` creating `payx.payments`
  (6 partitions, RF 1, 7-day retention, auto-create off), Kafka UI on 7090.
  Verified from the CLI: same key → same partition in produce order (pay_A's
  authorized/captured/refunded all partition 4, offsets 0-2); no ordering
  across partitions; consumer-group committed offsets + lag; a restarted
  group resumes from its offsets; a second group reads everything
  independently. Design fix recorded: one topic per aggregate, not per event
  type (DESIGN decision 4). Committed.
- 1.5 Health checks in ServiceDefaults: `/health/live` (runs no checks) and
  `/health/ready` (runs checks tagged `ready`), JSON body naming each check.
  `AddPostgresReadiness("Payments")` reads `ConnectionStrings:Payments`
  (fails at startup if missing), 2 s timeout. Payment wired to
  `payx_payments` as role `payx_payments`. Verified: Postgres up → both 200;
  `docker stop payx-postgres` → live 200, ready 503 naming
  `postgres:Payments`; restarted → ready 200 again with no app restart.
  Committed.
- 1.6 Gateway (YARP, port 7080): route `/api/payments/{**rest}` → cluster
  `payment` with two destinations (7082, 7092), round robin, active health
  checks on `/health/ready` every 5 s, unhealthy after 2 failures,
  `AvailableDestinationsPolicy: HealthyOrPanic` (fail open, decision 18).
  Ping moved to `/api/payments/ping` and reports which port answered.
  Verified: round robin alternates 7092/7082; killing 7092 gives 502 on its
  turns for ~10 s, then all traffic goes to 7082; with Postgres down (all
  unhealthy) `HealthyAndUnknown` → Gateway 503 every time, `HealthyOrPanic`
  → routes anyway (200 from the live instance, 502 from the dead one).
  Committed, together with the AWS production topology and decision 19 in DESIGN.md.
- 1.7 `Money` (Contracts): a `sealed record class` with `long AmountMinor` +
  ISO-4217 `Currency`, per-currency exponent (USD 2, JPY 0, KWD 3; unknown
  currencies rejected), capped at ±(2^53 − 1) so JavaScript clients read
  every amount exactly, `FromMajor` refuses sub-minor amounts instead of
  rounding, checked same-currency arithmetic, signed amounts, `Allocate` that
  never loses a minor unit. Test project `tests/PayX.Contracts.Tests`
  (xUnit): 15 tests green. **Found:** as a record struct, System.Text.Json
  built it through the hidden parameterless constructor (null currency,
  validation skipped); after owner review it became a class, which closes
  that and the `default(Money)` hole. Known limits recorded under DESIGN
  decision 17. Committed.

**Phase 2 — Identity. Complete: all four modules built, verified and committed.** Wrap-up done: README Phase 2 section, RUNNING.md (Identity + token flow), DESIGN §5 rows.
- 2.1 `PayX.Identity` (port 7081): `User` entity (UUID v7 id, lower-case
  username/email, password-hash column, role as text), `IdentityDbContext`
  with check constraints (lower-case username/email, role in the three
  values) and unique indexes, snake_case naming, migration
  `InitialUsers` applied at startup as role `payx_identity`. Verified:
  migration applied on first start, "No migrations were applied" on the
  second; `users` and `__EFMigrationsHistory` owned by `payx_identity`;
  mixed-case email, duplicate username and role `Admin` all refused by
  Postgres; `payx_payments` still can't connect to `payx_identity`.
  Readiness checks `postgres:Identity`. Committed.
- 2.2 `POST /api/users` (also via Gateway route `/api/users/**` → cluster
  `identity`): DataAnnotations validation (username pattern, email,
  password 12-128 chars), normalise (trim + lower-case), refuse `Ops`
  self-registration, hash with ASP.NET Core `PasswordHasher` (PBKDF2-
  HMAC-SHA512, 100,000 iterations, 128-bit salt, decoded from a stored
  hash), insert and map unique-index violations to 409 (no check-then-
  insert). Enums as JSON strings in ServiceDefaults. Verified: 201 with
  normalised values and no hash in the response; 409 for username and
  email differing only in case; 400 for `Ops` and for validation; same
  password → different hashes; two racing sign-ups → one 201, one 409.
  Committed.
- 2.3 `POST /api/users/login` → `{accessToken, tokenType: Bearer,
  expiresIn: 900}`. `UserAuthentication` verifies the hash, runs a dummy
  verification for unknown users (equal timing), and re-hashes on
  `SuccessRehashNeeded`. `TokenIssuer` signs ES256 JWTs (iss
  `payx-identity`, aud `payx-api`, sub, unique_name, role, jti, 15 min) with
  a P-256 key in gitignored `App/.keys/identity-signing-key.pem` (created on
  first login, path resolved from the content root); `kid` = RFC 7638
  thumbprint. Decision 20 recorded. Verified: 200 + decoded token
  (alg ES256, kid, claims, 64-byte signature); wrong password and unknown
  user both 401 with the same body in ~40 ms; a 10,000-iteration hash was
  upgraded to 100,000 on login. Committed.
- 2.4 Identity publishes `/.well-known/openid-configuration` and
  `/.well-known/jwks.json` (public x/y only, no `d`). Gateway: JwtBearer
  with `MetadataAddress` (keys fetched, cached, refetched on unknown kid),
  `ValidIssuer`/`ValidAudience`, `ValidAlgorithms` = ES256 only, 30 s clock
  skew, `MapInboundClaims` off; `FallbackPolicy` = authenticated (deny by
  default); route policies: `/api/users/**` anonymous, `/api/payments/**`
  `payments` (Customer or Merchant). Health endpoints `AllowAnonymous`.
  Middleware order fixed so 401/403 are ProblemDetails. Decision 21 + two
  Q&A entries. Verified: no token 401; Customer 200; Merchant 200; Ops
  (valid) 403; tampered payload, `alg: none`, garbage all 401 ("The
  signature is invalid"); login and health anonymous; unknown route 401.
  Committed.

**Phase 3 — PSP Simulator + Vault. Complete: all seven modules built, verified and committed.** Wrap-up done: README Phase 3 section, RUNNING.md (PSP simulator + Vault), DESIGN §3 Q&A and §5 rows.
- 3.1 `PayX.PspSimulator` (port 7088, no Gateway route, uses none of
  PayX.Contracts' types): `POST /api/psp/authorizations` (amount in minor
  units + currency, card, merchantId). `CardNetwork`: Luhn check + brand
  from BIN (Visa 4, Mastercard 51–55; others `card_not_supported`).
  `IssuerBank`: Stripe-style test cards (…0002 generic decline, …9995
  insufficient funds, …0069 expired, …0127 incorrect CVC), expiry through
  the last day of the month, 5,000.00 default credit limit. Declines are
  200 with `status: declined` + `declineCode` (a business answer, never
  worth retrying), malformed cards 400. Response has last 4 only. No
  storage yet (3.2). Verified: 13 scenarios, all as expected; no full card
  number in the service log. Committed.
- 3.2 PSP simulator gets its own database (`payx_psp`, migration
  `InitialAuthorizations`): `card_accounts` (HMAC fingerprint of the card
  number → credit limit; no card numbers stored) and `authorizations`
  (approved = a live hold, declined stored too). `Authorizer`: in one
  transaction, open the account (`ON CONFLICT DO NOTHING`), lock it
  (`SELECT … FOR UPDATE`), available = limit − live holds (derived, never
  stored), decide, insert, commit. Decision 22 + failure-mode row.
  Verified: $3,000 ✅ → $3,000 ❌ insufficient → $2,000 ✅ → $0.01 ❌.
  25 truly simultaneous $1,000 requests (threads released by a barrier):
  without the lock $6,001 and $10,001 held on a $5,000 card; with it,
  $4,001 both times. (Parallel `curl` processes on Windows start too
  slowly to race; they showed nothing either way.) Committed.
- 3.3 `POST /api/psp/authorizations/{id}/capture {amount}` and
  `/{id}/void`. State rules on `Authorization` (`Capture` / `Void` from
  Authorized only; partial capture allowed, never above the authorized
  amount, unused part released), new statuses Captured/Voided,
  `captured_amount_minor` + `captured_at` (settlement will batch by it),
  check constraint `captured ≤ authorized`, `xmin` as optimistic
  concurrency token (Npgsql skips it in the migration SQL, verified).
  Available credit now counts charges as well as holds. Refusals are 409
  with a code, unknown id 404. Decision 23 + failure-mode row. Verified on
  fresh cards: partial capture $2,500 of $3,000 → $2,500 available after;
  capture twice / void after capture / capture after void / capture a
  decline / over-capture all 409; void frees the full hold. 20 truly
  simultaneous capture-vs-void rounds: exactly one winner each time,
  final states 11 captured + 9 voided, never both. (First run reused cards
  exhausted by 3.2's tests; their authorizations were declined, so that
  run only proved declines can't be captured.) Committed.
- 3.4 `GET /api/psp/authorizations/{id}` (status lookup, no key needed) and
  `[Idempotent]` on authorize / capture / void: an action filter requiring
  `Idempotency-Key` that, in one transaction, claims the key (INSERT on the
  `idempotency_keys` primary key), runs the action, stores status + body,
  commits. Duplicate key → wait on the unique index, then replay the stored
  answer with `Idempotent-Replayed: true`; same key + different request
  (HMAC of method, path, arguments) → 422; missing key → 400. `Authorizer`
  joins the caller's transaction; a losing capture/void is detached so the
  stored 409 can be saved. Decision 24; failure-mode row updated. Verified:
  replay byte-identical; capture retried with its key → 200 replayed (the
  3.3 gotcha), with a new key → 409; a stored 409 replays as 409; a
  validation 400 isn't stored (same key then works); declines replay; 10
  truly simultaneous identical authorizations → 1 row, 9 replays;
  capture-vs-void race still one winner (9 rounds). **Found:** `jsonb`
  re-sorted keys, so replays weren't byte-identical; column switched to
  `text` (uncommitted migration rolled back and regenerated). Committed.
- 3.5 `X-Psp-Fault` header on every authorizations endpoint
  (`[FaultInjectable]`, `Api/FaultInjection.cs`), honoured only when
  `Psp:FaultInjection:Enabled` (true in `appsettings.Development.json`).
  `latency=ms` (≤ 60 s) and `error=5xx` run in an action filter ordered
  *before* `[Idempotent]`; `timeout-after-success` is a result filter that
  runs *after* the commit, hangs until the caller gives up (cap 60 s) and
  drops the connection. Values combine (`latency=1000, error=502`); bad
  values → 400 `invalid_fault`. Decision 25; two failure-mode rows.
  Verified: timeout-after-success with `curl -m 3` → curl exit 28, status
  lookup shows `authorized`, same-key retry → 200 `Idempotent-Replayed`, a
  $2,000.01 follow-up on the $5,000 card declines (one $3,000 hold, not
  two); same on capture → `captured`, retry replays; `error=503` → 503, then
  the same key runs for real (200, no replay header); `latency=2000` →
  2.02 s; `latency=1000, error=502` → 502 in 1.01 s; four bad values → 400;
  GET with 503 → 503; no client timeout → dropped at 60.03 s (curl 56);
  flag off → header ignored (200). Committed.
- 3.6 New service `PayX.Vault` (port 7084, no endpoints yet) and
  `CardEncryptor`: `GenerateDataKey` (AES_256) per card → AES-256-GCM
  locally (12-byte nonce, 16-byte tag) → store ciphertext + encrypted data
  key + the wrapping key's ARN (`EncryptedCard`); clear key zeroed after
  use. Decrypt: KMS `Decrypt` with the stored ARN, then GCM. Record id
  bound as KMS encryption context and GCM associated data.
  `AddAwsClient<T>()` in ServiceDefaults (LocalStack via `AWS:ServiceURL`,
  dummy credentials only when an override is set). New test project
  `PayX.Vault.Tests`, integration tests against LocalStack KMS. Decision 26,
  two failure-mode rows. Verified: 5/5 tests green (round trip; stored
  form has no card number, length = 12 + 16 + 16, key recorded as an
  `arn:aws:kms:` ARN not the alias; same card twice → different data key
  and ciphertext; one flipped bit → `AuthenticationTagMismatchException`;
  blob decrypted as another record → KMS `InvalidCiphertextException`);
  host starts on 7084, live/ready 200 (ready has no checks yet: KMS and
  DynamoDB readiness come with 3.7). Committed.
- 3.7 Tokenization. `POST /api/vault/cards` {number, expMonth, expYear} →
  201 + `Location` {token, brand, last4, expMonth, expYear};
  `GET /api/vault/cards/{token}` same shape, 404 `card_not_found`.
  `CardVault`: token (`tok_` + 128 random bits) created first, card
  encrypted bound to it (3.6), DynamoDB `PutItem` with
  `attribute_not_exists(token)`; `DetokenizeAsync` is a C# method only (no
  HTTP endpoint returns a card number). `TokenizeRequest` has no CVC and
  `JsonUnmappedMemberHandling.Disallow`. `CardNumber` (Luhn, display brand).
  Readiness `kms:card-vault` (DescribeKey via alias, enabled) and
  `dynamodb:card-vault` (DescribeTable, ACTIVE), 2 s timeout. No Gateway
  route. Decision 27, failure-mode row, two open questions (CVC path, card
  fingerprint). Verified: 4 new integration tests (tokenize → detokenize
  round trip; stored item has exactly the 9 expected attributes, no number,
  no CVC; same card twice → two tokens; unknown token → null), 9/9 Vault
  tests; over HTTP: 201 + Location, lookup 200, unknown 404, `cvc` → 400,
  bad Luhn → 400 `invalid_number` (number not echoed), Mastercard brand;
  `docker stop payx-localstack` → ready 503 naming both checks, live 200,
  back to 200 after restart; 0 card numbers in the log. **Found:** after
  the LocalStack restart the key id changed and the table was empty, so the
  earlier token → 404 (the 1.3 caveat, now observed; documented in
  RUNNING.md). Phase 3 wrap-up: README Phase 3 section, RUNNING.md (PSP +
  Vault), DESIGN §3 Q&A (5) and §5 rows. Committed.

**Phase 4 — Payment core. In progress.** Decisions 3 (orchestrated saga),
13 (Vault) and 14 (PSP simulator) accepted by the owner on 2026-10-20; 4,
10 and 11 are confirmed at the start of Phases 6, 7 and 5.
- 4.1 `payments` table (migration `InitialPayments`) and the state machine.
  `PaymentStatus`: Created, RiskApproved, RiskBlocked, Authorized,
  Declined, Captured, Voided (Challenged, Unknown, Settled, Refunded,
  Disputed arrive with their modules). `PaymentStateMachine`: one table of
  legal moves; anything unlisted is final; `IllegalTransitionException`
  otherwise. `Payment`: no public setters, one method per move (capture
  checks currency and 0 < amount ≤ authorized), `Money` as an EF complex
  type (two columns), `xmin` concurrency. Check constraints: status list
  (generated from the enum), amount > 0, PSP reference once Authorized /
  Captured / Voided, captured amount iff Captured and ≤ amount. EF pinned
  to 10.0.12 (Npgsql pulled 10.0.4; the test project couldn't load the
  mismatch). New test project `PayX.Payment.Tests`. Decision 28, two
  failure-mode rows. Verified: 48 transition tests (every from → to pair
  vs a separately written spec; refused moves change nothing), final
  states, nothing returns to Created, partial capture, over-capture and
  wrong currency refused; 2 Postgres tests (Money round trip; capture vs
  void from two contexts → second save `DbUpdateConcurrencyException`,
  row stays Captured). In psql each bad row refused by its named
  constraint. **Found:** constraints check rows, not moves: a raw `UPDATE`
  Created → Voided with a made-up PSP reference passes (recorded as the
  known gap in decision 28). Committed.
- Owner, 2026-10-21: CVC option (b), Vault holds it; risk stand-in OK.
  4.2 split into three modules (one concept each): 4.2a CVC hold, 4.2b
  Vault → PSP authorize by token, 4.2c Payment orchestration. Lettered so
  later module numbers don't change.
- 4.2a `CvcStore` (Vault): CVC accepted at tokenization (3–4 digits),
  envelope-encrypted with record id `{token}:cvc`, `SET vault:cvc:{token}`
  with TTL `Vault:CvcTtl` (10 min), taken once with `GETDEL`; held before
  the card is stored. `CardVault.TakeCvcAsync` (no HTTP endpoint).
  `AddRedis()` + `RedisHealthCheck` in ServiceDefaults (one multiplexer,
  `abortConnect=false`, readiness `redis`). CLAUDE.md: Vault owns
  `vault:cvc:*`. Decision 29 (decision 27 and README 3.7 annotated),
  failure-mode row, §4 question closed. Tests moved onto a shared
  `TestVault` helper. Verified: 14/14 Vault tests (5 new: taken once then
  null; Redis value is only the envelope — 3 fields, ciphertext 12+3+16
  bytes — with TTL 9–10 min; 1 s TTL expires; no CVC → none held; 10
  simultaneous takes → exactly one gets it; the "stored item has no CVC"
  test now tokenizes *with* one). HTTP: 201; `TTL` 599 s in redis-cli;
  3-digit-only rule (`12` → 400); misspelt `cvv` → 400; ready lists
  redis + kms + dynamodb; Redis stopped → ready 503, live 200, tokenize
  with CVC 500; back to 200 after restart; CVC and card number 0 times in
  the log. Committed.
- 4.2b `POST /api/vault/cards/{token}/authorizations` {amount, currency,
  merchantId} + required `Idempotency-Key`. `CardAuthorizer`: find card
  (404 `card_not_found`) → take CVC (409 `cvc_unavailable`) → decrypt
  number → `PspClient` POSTs the PSP's authorize body with the key
  forwarded as given → relay status, body and `Idempotent-Replayed`
  verbatim; PSP timeout → 504 `psp_timeout`, connection failure → 502
  `psp_unreachable`. Typed `HttpClient` (`Psp:BaseUrl`, `Psp:Timeout` 10 s;
  Polly later). `CardVault.RevealNumberAsync`. Decision 30, two
  failure-mode rows (one an open gap), doc-to-code row; 4.7's row now
  names the gap. Verified: 4 new tests with a stub PSP (exact outbound
  body: number, expiry, CVC, amount, merchant, key; answer relayed; second
  authorization → 409 without calling the PSP; unknown token → 404, PSP
  untouched; slow PSP → 504), 18/18 Vault tests. End to end with the
  simulator: approved 200 (last4 only); generic-decline and incorrect-CVC
  cards relayed as 200 `declined`; no key → 400; unknown token → 404; PSP
  stopped → 502; 0 card numbers or CVCs in either service's log. **Found
  (the gap):** a same-key retry after success → 409 `cvc_unavailable`,
  not the PSP's replay; and with the PSP down the CVC was already used up,
  so even a "nothing happened" failure can't be retried. Recorded for 4.7.
  Committed.
- 4.2c `POST /api/payments` {merchantId, amount {amountMinor, currency},
  cardToken}, Customer role only, customer = token `sub`.
  `PaymentAuthorization`: save Created → `IRiskCheck` (stand-in
  `AllowAllRiskCheck`) → save RiskApproved / RiskBlocked → `VaultClient`
  POST `/api/vault/cards/{token}/authorizations` with key
  `{paymentId}:authorize` → save Authorized (PSP id + auth code) /
  Declined (code). 201 for every business outcome; otherwise the payment
  stays RiskApproved and the caller gets 422 (Vault 4xx) or the 5xx, with
  the payment id in `detail`. Token validation moved from the Gateway to
  ServiceDefaults `AddPayXJwtAuthentication()` unchanged (JwtBearer package
  moved too); Payment uses it with a deny-by-default fallback policy.
  `Vault:Timeout` 15 s > Vault's 10 s PSP timeout. Decision 31; decision 21
  updated; three failure-mode rows; doc-to-code row; RUNNING.md end-to-end
  example and the Smart App Control note. Verified: 6 new tests against
  Postgres with a stub Vault (approved → Authorized with PSP reference;
  request by token with key `{id}:authorize` and the right body; declined
  → Declined with code; risk block → RiskBlocked, Vault never called;
  Vault 409 / 504 → stays RiskApproved with the error), 56/56 Payment
  tests. End to end through the Gateway with all five services: approved
  201 Authorized; decline card 201 Declined; reused card token → 422
  `cvc_unavailable`; Merchant token → 403 (Payment's own rule); no token
  straight to 7082 → 401; unknown card token → 422 `card_not_found`; PSP
  down → 502 `psp_unreachable`; JPY 500 → Authorized; rows in psql match;
  0 card numbers in Payment's log. **Found:** unknown currency → 500
  (`Money`'s constructor throws during JSON reading, which MVC doesn't turn
  into a 400; amount 0 would do the same via `Payment`): request now takes
  plain validated fields, `XYZ` and `0` → 400. **Found:** Smart App
  Control blocked freshly built PSP/Payment binaries when five services
  were built in parallel, then the Payment test DLL itself; the owner
  turned Smart App Control off (RUNNING.md). Final run: build 0/0, 89
  tests green (15 + 56 + 18), `dotnet test` exit 0; Payment's `.exe`
  starts normally. Committed.
- 4.3 `POST /api/payments/{id}/capture` {amountMinor?} (default: full
  amount), Merchant role, only the payment's own merchant (else 404
  `payment_not_found`). `PaymentCapture`: load → `Payment.CaptureRefusal`
  (new; state → 409 `payment_not_capturable`, currency / amount → 422)
  checked before any PSP call → `PspClient.CaptureAsync` (new typed client,
  Payment → PSP directly, `Psp:BaseUrl`, 10 s) with key `{paymentId}:capture`
  → `Capture` + save. PSP refusal → 409 with its code, timeout 504,
  unreachable 502; payment stays Authorized. `PaymentResponse` gains
  `capturedAmountMinor`. Decision 32, two failure-mode rows, doc-to-code
  row, RUNNING.md example. Verified: 8 new tests (full capture by default,
  URL quotes the PSP authorization id, key `{id}:capture`; partial; other
  merchant → 404, PSP untouched; declined → 409, PSP untouched; over-capture
  → 422, PSP untouched; PSP 504 / 409 → stays Authorized; slow PSP →
  timeout, stays Authorized), 64/64 Payment tests. End to end through the
  Gateway: full capture → Captured, PSP agrees (5,000); partial 3,000 → PSP
  agrees; capture again → 409; other shop → 404; customer → 403;
  over-capture → 422; declined → 409; **PSP down → 502, stays Authorized;
  same capture after PSP restart → Captured, PSP shows one capture of
  5,000** (the retry authorize can't do yet, 4.7). **Found by the tests:**
  reading PSP refusals with one record for both shapes threw on every 409
  (`status` is a number in ProblemDetails); now read separately. Committed.

## Environment notes

- **Git Bash rewrites container paths.** `docker exec payx-kafka /opt/kafka/bin/…`
  becomes `C:/Program Files/Git/opt/…` and fails. Prefix with
  `MSYS_NO_PATHCONV=1` (or use PowerShell).

## Next up

**Phase 4, Module 4.4 — `checkPaymentStatus`** (`GET /api/payments/{id}`:
the customer or the merchant of a payment asks where it is; nobody else
can see it).
Design decisions in `DESIGN.md` §1 (esp. 3, 4, 10, 11, 13, 14) still awaiting
owner confirmation; nothing up to 1.7 depends on them.
