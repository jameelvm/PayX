# PayX — system design

> Status: **Phase 0 draft — proposed, pending owner review.** Decisions marked
> *Proposed* are open for change; they become *Accepted* when signed off and
> are then only revisited with a new decision that supersedes them.

## §0 Requirements and estimates (with corrections)

**Functional** (doc ch.1): register/authenticate · pay by card via a PSP ·
transaction history · balance (available + pending) · mobile/responsive access.

**Non-functional**: data integrity & security · availability · reliability
(redundancy, failover, backups) · scalability (peak sales) · performance.

| Quantity | Doc's figure | Check | Note |
|---|---|---|---|
| Transactions/day | 50M | — | given |
| Average TPS | — | 50M / 86,400 ≈ **580** | doc skips this |
| Peak TPS | ~1,400 (5M/hr) | 5M / 3,600 ≈ 1,389 ✓ | |
| Storage/day | 5 GB @ 100 B/txn | ✓ arithmetic | 100 B is unrealistic: a payment row + attempts + ≥2 ledger entries + outbox + idempotency record is ~1–2 KB. Realistic: **50–100 GB/day**, ~20–35 TB/yr before replication |
| Bandwidth | 1.12 / 1.46 Mbps | ✓ | negligible; bandwidth is not this system's constraint |
| Servers | 50M ÷ 64K ≈ 782 | ✗ | Treats *50M transactions/day* as *50M requests/second*. At 1,400 TPS × ~10 internal calls per payment ≈ 14K internal RPS — a **handful** of instances per service; the replica count is driven by availability (≥3 per AZ-spread service), not throughput |

The corrected numbers change the design emphasis: **this is not a throughput
problem, it is a correctness problem.** Postgres on a single primary per
service handles 1,400 TPS comfortably; the hard parts are exactly-once money
movement, auditability and reconciliation. (Interview talking point: spot the
estimate error, then say what it means for the architecture.)

## Architecture overview

Rendered diagrams (Mermaid, viewable on GitHub): [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).
The text sketches below are the quick-reference versions.

### Component diagram

```
                ┌──────────── Next.js: checkout · history · merchant dashboard · ops console
                ▼
            Gateway (YARP, JWT, rate limit, Idempotency-Key required on POST)
      ┌─────────┼───────────────┬──────────────┬───────────────┐
      ▼         ▼               ▼              ▼               ▼
  Identity    Vault ──KMS    Payment ───────► Risk          Ledger API / Recon API / Dispute API
  (PG)       (DynamoDB)      (PG + outbox)   (Redis+Dynamo)
                               │  ▲  │
             authorize/capture │  │  │ outbox relay
             (Idempotency-Key) ▼  │  ▼
                      PSP Simulator   Kafka ── payx.payments.* ──► Ledger (PG, append-only,
                  (PSP+network+issuer)  ▲                           double-entry + wallets)
                     │   │ webhooks ────┘(via Payment)              Dispute, Notifications
                     │   └── EOD settlement file ──► S3 ──► Reconciliation ◄── Ledger export
```

### Payment state machine (owned by PaymentService)

```
 CREATED ─risk─► RISK_APPROVED ─auth─► AUTHORIZED ─capture─► CAPTURED ─settle file─► SETTLED
    │               │                    │    │                  │
    └► RISK_BLOCKED └► CHALLENGED        │    └► VOIDED          └► REFUNDED / DISPUTED
                       (3DS-style)       └► DECLINED
          any PSP call that times out ─► UNKNOWN ─(resolver queries PSP by idempotency key)─► real state
```

`UNKNOWN` is the doc's timeout-ambiguity section turned into a first-class
state: a timeout is *not* a failure, it is "we don't know yet", and a resolver
job asks the PSP (by the same idempotency key) what actually happened.

### Storage schema (inferred — the doc's schema figure is an image that didn't survive PDF export)

- **payx_identity**: `users(id, username, email, password_hash, role[customer|merchant|ops], created_at)`, `merchants(id, user_id, display_name, settlement_currency)`
- **payx-card-vault** (DynamoDB): `token (PK) → {encrypted_pan, encrypted_dek, kms_key_id, last4, brand, exp_month, exp_year, fingerprint}`; CVV is **never stored** (PCI DSS 3.2)
- **payx_payments**: `payments(id, merchant_id, customer_id, amount_minor, currency, card_token, status, risk_score, psp_reference, authorization_code, version, created_at, updated_at)`, `payment_attempts(id, payment_id, operation, psp_idempotency_key, request, response, outcome, latency_ms)`, `idempotency_keys(key, scope, request_hash, status, response_code, response_body, locked_until, expires_at)`, `outbox(id, aggregate_id, topic, key, payload, created_at, published_at)`
- **payx_ledger**: `accounts(id, owner_id, type[customer_funding|merchant_pending|merchant_available|psp_clearing|fees|chargebacks], currency, balance_minor, version)`, `journal_entries(id, transaction_id, payment_id, kind, created_at)`, `postings(id, entry_id, account_id, amount_minor (signed), currency)` with a check that every entry's postings sum to 0; `processed_events(event_id)` for consumer dedupe
- **payx_recon**: `runs(id, settlement_date, file_key, status, matched, mismatched)`, `discrepancies(id, run_id, payment_id, kind[missing_in_ledger|missing_in_psp|amount_mismatch|status_mismatch], ledger_amount, psp_amount, resolution)`
- **payx_disputes**: `disputes(id, payment_id, psp_dispute_id, reason, amount_minor, status, evidence_due, created_at)`

## §1 Decision register

| # | Decision | Status | Why | Alternative rejected |
|---|---|---|---|---|
| 1 | Service-oriented, one store per service | Proposed | Same discipline as JameX/SuggestX; makes the saga boundaries real | Modular monolith (would hide every distributed-failure lesson) |
| 2 | Postgres for payments, ledger, identity, recon, disputes | Proposed | Money needs ACID, constraints, serializable transactions; ~1,400 TPS is easy for PG | DynamoDB for payments — possible (conditional writes), but double-entry invariants and history queries are far more natural in SQL |
| 3 | **Orchestrated saga** in PaymentService (explicit state machine), events for side effects | Proposed | Payment flow is linear with a clear owner; orchestration keeps the state in one inspectable row | Pure choreography (state smeared across services); Temporal/Step Functions — see §4, possible later phase |
| 4 | **Kafka** (KRaft) for the event backbone, partition key = `payment_id` | Proposed | Doc names Kafka; per-payment ordering; replayable log (rebuild wallets, re-run recon); new tech vs JameX's SNS/SQS | SNS→SQS FIFO (works, but no replay; already learned in JameX) |
| 5 | **Transactional outbox** + relay | Proposed | Closes the dual-write gap between "row updated" and "event published" | Publish-then-commit / commit-then-publish (each loses or invents events on crash); Debezium CDC (heavier — mention as prod option) |
| 6 | **Idempotency keys** persisted in PaymentService's PG, Stripe-style (request hash, in-progress lock, stored response) | Proposed | Doc's answer to double-charge; storing the *response* means a retry returns the identical result | Redis-only keys (lost on eviction → double charge); dedupe by amount+card+time (false positives) |
| 7 | Idempotency propagated to the PSP (`{payment_id}:{operation}:{attempt-safe-key}`) | Proposed | Our retry after a PSP timeout must not create a second authorization | Relying on our own dedupe only — doesn't help when the PSP did the work |
| 8 | `UNKNOWN` state + resolver job for timeouts | Proposed | The doc's three timeout cases are indistinguishable to the caller; only the PSP knows | Treat timeout as failure (double charge on retry, or lost sale) |
| 9 | Ledger is **double-entry, append-only**, enforced by DB trigger + no UPDATE/DELETE grant | Proposed | Doc: "immutable entry … auditable trail"; double-entry makes money-from-nowhere detectable (sum ≠ 0) | Single-entry transaction log (can't detect imbalance) |
| 10 | **Wallet balances live inside Ledger**, updated in the same DB transaction as the postings | Proposed — **deviation from doc** | Doc updates wallet *then* appends ledger as two writes — a crash between them corrupts balances. One transaction makes balance a provably consistent projection | Separate Wallet service (dual-write); balances computed by SUM on every read (slow at scale; fine as the recon cross-check) |
| 11 | Fraud detection + risk check as **one Risk service** with two stages (rules/velocity → score → allow/challenge/block) | Proposed | Doc describes two tightly-coupled components with no separate data; splitting adds a hop with no ownership boundary | Two services (revisit if an ML model stage is added) |
| 12 | Risk fallback: on Risk timeout/error, approve if amount < configurable threshold, else fail closed | Proposed | Doc's fallback example verbatim; makes the risk/UX trade-off a config value | Always fail closed (lost sales during a Risk outage) / always fail open (fraud window) |
| 13 | **Vault + KMS envelope encryption**; PAN → token at the edge | Proposed — **addition** | Answers the doc's "where are card details encrypted?"; shrinks PCI scope to one service | Payment service storing encrypted PAN (whole service enters PCI scope) |
| 14 | **PSP Simulator** with test cards and failure injection | Proposed | Can't demo timeout-after-success or duplicate webhooks against a real sandbox on demand | Stripe test mode (real, but can't inject the failures the doc is about — could be an adapter later) |
| 15 | Resilience via Polly v8 (`Microsoft.Extensions.Http.Resilience`) — retry w/ exponential backoff + jitter (only on idempotent/keyed calls), per-attempt + total timeouts, circuit breaker | Proposed | Doc's retry/timeout/fallback; idiomatic .NET | Hand-rolled retry loops |
| 16 | OpenTelemetry tracing across HTTP **and** Kafka hops → Aspire dashboard | Proposed — **addition** | A payment touches 6+ services; one trace per payment is how you debug a saga | Log correlation IDs only |
| 17 | Money as `long` minor units + currency | Proposed | No float rounding; currency explicit everywhere | `decimal` amounts (fine in C#, ambiguous across JSON/JS) |

## §2 Failure-mode table (seeded — verified per phase)

| Failure | Effect without mitigation | Mitigation | Phase verified |
|---|---|---|---|
| Customer double-clicks Pay | Two charges | Client-generated Idempotency-Key + PG unique key + stored response | — |
| PSP times out after authorizing | Retry → second auth, or sale marked failed while customer charged | Same PSP idempotency key on retry; `UNKNOWN` + resolver | — |
| PaymentService crashes after DB commit, before publishing event | Ledger never credits merchant | Outbox: event is in the same commit; relay publishes on restart | — |
| Ledger consumer crashes mid-processing | Event lost or applied twice | Commit offset only after PG commit; `processed_events` dedupe | — |
| Kafka unavailable | Payments can't announce completion | Outbox buffers in PG; payment API still works; relay drains on recovery | — |
| Risk service down | All payments blocked | Circuit breaker + amount-threshold fallback (dec. 12) | — |
| PSP sends duplicate / out-of-order webhooks | Double capture/refund | Webhook dedupe by PSP event id; state machine rejects illegal transitions | — |
| Settlement file disagrees with ledger | Silent money loss | Reconciliation flags discrepancy for investigation | — |
| Authorized but never captured (auth expiry) | Merchant loses sale | Capture deadline job; void/alert | — |

## §3 Q&A bank (seed — from the doc's own prompts)

- *Auth succeeded, settlement failed — when and what then?* Auth expired before
  capture, card cancelled, issuer reversal, amount mismatch. Handle via capture
  deadlines, reconciliation, merchant notification, and keeping the payment in
  a non-terminal state until settlement confirms.
- *How does decoupling fraud checks from the gateway help?* Independent scaling
  and deploy cadence, our own risk policy instead of the PSP's, ability to fail
  over PSPs without losing risk history, and a fallback path when risk is down.
- *Fraud detection already exists — what does a risk check add?* Fraud detection
  flags patterns; risk check turns signals into a **decision** (allow /
  challenge / block) with business context (amount, merchant category, customer
  tenure).
- *Where are card details encrypted?* In transit with TLS from the browser;
  ideally tokenized before reaching the merchant (hosted fields); at rest only
  inside the vault under KMS envelope encryption. CVV is never stored.
- *Reconciliation mismatch — then what?* Classify (missing on either side,
  amount, status), auto-resolve known timing differences (T+1 settlement),
  escalate the rest; correct via reversing ledger entries, never edits.
- *Buy button hit many times?* Idempotency key generated once per checkout
  session, disabled button client-side, server dedupe as the real guarantee.

## §4 Open questions / deferred

- Temporal or AWS Step Functions as the saga engine — a possible late phase to
  contrast with the hand-rolled orchestrator.
- Multi-currency and FX — out of scope initially (single currency per merchant).
- 3-D Secure challenge flow — modelled as a `CHALLENGED` state; full flow deferred.
- Multi-PSP routing / failover — mention only, unless time allows.
- Payouts from merchant available balance to bank — deferred.

## §5 Doc-to-code map

Filled in as phases land.
