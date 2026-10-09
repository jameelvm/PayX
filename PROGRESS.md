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

**Overall: 6 / 67 modules done.** Phase 10 and 11 counts are estimates and may change when we get there.

Legend: ✅ done and committed · 🔍 built, awaiting owner review · 🔄 phase in progress · ⬜ not started

| Phase | Name | Doc concept | Done | Status |
|---|---|---|---|---|
| 0 | [Design](#phase-0-design) | requirements, estimates (+ corrections), HLD | 1 / 2 | 🔄 |
| 1 | [Local substrate](#phase-1-local-substrate) | — | 5 / 7 | 🔄 |
| 2 | [Identity](#phase-2-identity) | `registerUser`, `authenticateUser` | 0 / 4 | ⬜ |
| 3 | [PSP Simulator + Vault](#phase-3-psp-simulator--vault) | payment gateway, card network, issuer; card-data encryption | 0 / 6 | ⬜ |
| 4 | [Payment core](#phase-4-payment-core) | payment service, idempotency | 0 / 7 | ⬜ |
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
| ⬜ | 0.2 | Owner confirms open decisions (DESIGN §1: 3, 4, 10, 11, 13, 14) | — |

### Phase 1: Local substrate

| | Module | What it builds | Concept |
|---|---|---|---|
| ✅ | 1.1 | Solution skeleton: `PayX.slnx`, Contracts, ServiceDefaults, hello service | project layout & dependency direction |
| ✅ | 1.2 | Postgres: one database + one login role per service | database-per-service, enforced |
| ✅ | 1.3 | Redis + LocalStack (S3, DynamoDB, KMS) + bootstrap script | local AWS emulation, same SDK as prod |
| ✅ | 1.4 | Kafka (KRaft) + Kafka UI; topics, keys, partitions, offsets from the CLI | log vs queue, per-key ordering |
| ✅ | 1.5 | ServiceDefaults: health checks + config wiring | liveness vs readiness |
| ⬜ | 1.6 | Gateway with YARP routing | single entry point |
| ⬜ | 1.7 | `Money` value type + tests | integer minor units |

### Phase 2: Identity

| | Module | What it builds | Concept |
|---|---|---|---|
| ⬜ | 2.1 | Users table + EF Core migration | schema ownership |
| ⬜ | 2.2 | `registerUser` with password hashing | never store passwords |
| ⬜ | 2.3 | `authenticateUser` → JWT | stateless auth |
| ⬜ | 2.4 | Gateway validates JWT; customer / merchant / ops roles | auth at the edge |

### Phase 3: PSP Simulator + Vault

| | Module | What it builds | Concept |
|---|---|---|---|
| ⬜ | 3.1 | PSP sim: `authorize` with test cards | the outside world |
| ⬜ | 3.2 | `capture` + `void`, authorization holds | two-phase card payments |
| ⬜ | 3.3 | `status` lookup + idempotency on the PSP side | provider-side dedupe |
| ⬜ | 3.4 | Failure injection (latency, 5xx, timeout-after-success) | testing partial failure |
| ⬜ | 3.5 | Vault: KMS envelope encryption of one card number | envelope encryption |
| ⬜ | 3.6 | Vault: tokenize / detokenize API, CVV never stored | tokenization, PCI scope |

### Phase 4: Payment core

| | Module | What it builds | Concept |
|---|---|---|---|
| ⬜ | 4.1 | `payments` table + state machine with legal transitions | explicit state machine |
| ⬜ | 4.2 | `authorizePayment` happy path | orchestration |
| ⬜ | 4.3 | `capturePayment` | authorize vs capture |
| ⬜ | 4.4 | `checkPaymentStatus` | status API |
| ⬜ | 4.5 | Idempotency-Key table: insert or return stored response | never charge twice |
| ⬜ | 4.6 | Concurrent duplicate requests (in-progress lock) | races on retries |
| ⬜ | 4.7 | Propagating idempotency keys to the PSP | end-to-end idempotency |

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

**Last updated:** 2026-10-06
**Phase 0 — design. Draft written, awaiting owner review.** Source PDFs read
(the schema/detailed-design figures are blank in the PDF export, so the
storage schema in `DESIGN.md` is inferred from the APIs and text). Stack,
service boundaries and 17 proposed decisions are in `DESIGN.md` §1.
**Phase 1 — Modules 1.1–1.5 done, verified.**
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

## Environment notes

- **Git Bash rewrites container paths.** `docker exec payx-kafka /opt/kafka/bin/…`
  becomes `C:/Program Files/Git/opt/…` and fails. Prefix with
  `MSYS_NO_PATHCONV=1` (or use PowerShell).

## Next up

**Phase 1, Module 1.6 — Gateway with YARP routing to the Payment service (single entry point).**
Design decisions in `DESIGN.md` §1 (esp. 3, 4, 10, 11, 13, 14) still awaiting
owner confirmation; nothing up to 1.7 depends on them.
