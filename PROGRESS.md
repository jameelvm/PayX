# PayX — build progress

## ▶ How to resume

Say this to Claude at the start of the next session:

> Read PROGRESS.md and CLAUDE.md in C:\System Design\Payment System\App,
> then continue from "Next up". Build in short modules, pausing after each
> one so I can review before you continue.

---

**Purpose of this file:** if a session is lost, this is the single place that
says where the build stopped and what happens next. Update *Current state* and
*Next up* at the end of every session.

**Standing conventions**

1. At the end of every phase, update `README.md` with teaching-style
   documentation of what that phase built — architecture, each module, the
   reasoning, verification commands and design talking points.
2. Also extend `DESIGN.md` — decisions, failure-mode rows (marked verified),
   Q&A entries, doc-to-code map.
3. Every phase leaves `dotnet build PayX.slnx` green.
4. Build in short modules, one concept each, pausing after every module.

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

## Current state

**Last updated:** 2026-10-06
**Phase 0 — design. Draft written, awaiting owner review.** Source PDFs read
(the schema/detailed-design figures are blank in the PDF export, so the
storage schema in `DESIGN.md` is inferred from the APIs and text). Stack,
service boundaries and 17 proposed decisions are in `DESIGN.md` §1.
**Phase 1 — Modules 1.1–1.2 done, verified.**
- 1.1 solution skeleton: `dotnet build PayX.slnx` green; `GET :7082/api/ping`
  → 200; unknown route → 404 `application/problem+json`. Committed.
- 1.2 Postgres (`payx-postgres`, host port 5434): six databases, each owned by
  its own login role, `CONNECT` revoked from `PUBLIC`. Verified: each role
  connects to its own database; `payx_payments` → `payx_ledger` and
  `payx_identity` → `payx_payments` are both refused with `permission denied
  … CONNECT privilege`. Awaiting owner review, then commit.

## Phase plan

| Phase | Goal | Doc concept it makes real |
|---|---|---|
| 0 | Design sign-off | requirements, estimates (+ corrections), HLD |
| 1 | Local substrate: solution, ServiceDefaults, compose (Postgres, Redis, Kafka + UI, LocalStack S3/DynamoDB/KMS, Aspire dashboard), Gateway skeleton, health checks | — |
| 2 | Identity: register/authenticate, password hashing, JWT, customer/merchant roles | `registerUser`, `authenticateUser` |
| 3 | PSP Simulator + Vault: test cards, authorize/capture/void/refund/status, failure injection; tokenization with KMS envelope encryption | payment gateway, card network, issuer; card-data encryption |
| 4 | Payment core: payment state machine, `authorizePayment` / `capturePayment` / `checkPaymentStatus`, **idempotency keys** end to end | payment service, idempotency |
| 5 | Risk: rules + Redis velocity counters → score → allow/challenge/block; circuit breaker + amount-threshold fallback | fraud detection, risk check, fallback |
| 6 | Reliability: **outbox → Kafka**, retries w/ backoff+jitter, timeouts, `UNKNOWN` + resolver, poison-message handling | transaction completion, transient failures |
| 7 | Ledger + wallets: double-entry, append-only (DB-enforced), balances available/pending, `getTransactionHistory`, balance API | wallet, ledger, history, balance mgmt |
| 8 | Settlement + Reconciliation: PSP EOD settlement file → S3, recon job, discrepancy report | reconciliation system |
| 9 | Refunds + disputes: refund flow, chargeback webhooks, reversing entries | dispute management |
| 10 | Frontend: checkout (double-click test), customer history, merchant dashboard, ops console (failure-injection toggles, recon results) | merchant's online store, mobile access |
| 11 | Evaluation: fault drills for every failure-mode row, k6 load test vs. the 1,400 TPS estimate, end-to-end traces | NFRs |

## Module breakdown

Each line is one module, one concept. Later phases will be refined further
when we get to them — the list shows the intended granularity, not a contract.

**Phase 1 — Local substrate**
- 1.1 Solution skeleton: `PayX.slnx`, empty `Contracts` + `ServiceDefaults`, one hello-world service. *Concept: project layout & dependency direction.*
- 1.2 Postgres container + one database per service (init script). *Concept: database-per-service.*
- 1.3 Redis + LocalStack (S3, DynamoDB, KMS) + init script. *Concept: local AWS emulation, same SDK as prod.*
- 1.4 Kafka (KRaft) + Kafka UI, create topics by hand, produce/consume from the CLI. *Concept: topics, partitions, keys, offsets — before any code touches Kafka.*
- 1.5 ServiceDefaults: health checks + config wiring. *Concept: readiness vs liveness.*
- 1.6 Gateway with YARP routing to the hello service. *Concept: single entry point.*
- 1.7 `Money` value type + tests. *Concept: why money is integer minor units.*

**Phase 2 — Identity**
- 2.1 Users table + EF Core migration. 2.2 `registerUser` with password hashing. 2.3 `authenticateUser` → JWT. 2.4 Gateway validates JWT; roles (customer/merchant/ops).

**Phase 3 — PSP Simulator + Vault**
- 3.1 PSP sim: `authorize` with test cards (approve / decline / insufficient funds). 3.2 `capture` + `void`, auth holds. 3.3 `status` lookup + idempotency on the PSP side. 3.4 Failure injection (latency, 5xx, timeout-after-success). 3.5 Vault: KMS envelope encryption of one PAN. 3.6 Vault: tokenize/detokenize API, CVV never stored.

**Phase 4 — Payment core**
- 4.1 `payments` table + state-machine enum with legal transitions (pure code + tests). 4.2 `authorizePayment` happy path (calls PSP). 4.3 `capturePayment`. 4.4 `checkPaymentStatus`. 4.5 Idempotency-Key table: insert-or-return-stored-response. 4.6 Concurrent duplicate requests (in-progress lock). 4.7 Propagating idempotency keys to the PSP.

**Phase 5 — Risk**
- 5.1 Rule-based score (amount, card country). 5.2 Velocity counter in Redis (sliding window). 5.3 Score → allow/challenge/block. 5.4 Payment calls Risk; Polly timeout. 5.5 Circuit breaker + amount-threshold fallback.

**Phase 6 — Reliability**
- 6.1 Outbox table, written in the same transaction as the payment update. 6.2 Outbox relay → Kafka. 6.3 Retry with exponential backoff + jitter on PSP calls. 6.4 `UNKNOWN` state on timeout. 6.5 Resolver job. 6.6 Poison messages / dead-letter topic.

**Phase 7 — Ledger + wallets**
- 7.1 Accounts + journal + postings schema; sum-to-zero check. 7.2 Append-only enforcement (trigger + grants). 7.3 Kafka consumer → post entries; commit offset after the DB commit. 7.4 Consumer dedupe (`processed_events`). 7.5 Balances updated in the same transaction (wallet). 7.6 `getTransactionHistory`. 7.7 Balance API (available vs pending).

**Phase 8 — Settlement + Reconciliation**
- 8.1 PSP sim writes the EOD settlement file to S3. 8.2 Recon reads and parses it. 8.3 Matching against the ledger. 8.4 Discrepancy classification + report.

**Phase 9 — Refunds + disputes**
- 9.1 Refund flow + reversing entries. 9.2 PSP webhooks + signature verification + dedupe. 9.3 Chargeback lifecycle.

**Phase 10 — Frontend** (one page per module: checkout, double-click demo, history, merchant dashboard, ops console)

**Phase 11 — Evaluation** (one fault drill per module, then k6 load test, then traces)

## Next up

**Phase 1, Module 1.3 — Redis + LocalStack (S3, DynamoDB, KMS) + init script.**
Design decisions in `DESIGN.md` §1 (esp. 3, 4, 10, 11, 13, 14) still awaiting
owner confirmation; nothing up to 1.7 depends on them.
