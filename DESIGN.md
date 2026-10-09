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
                      PSP Simulator   Kafka ─── payx.payments ──► Ledger (PG, append-only,
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

### Production topology on AWS (no API Gateway)

Everything above runs locally in Docker Compose. This is the same
architecture deployed to AWS. The entry point is an **ALB in front of our
own YARP Gateway**; Amazon API Gateway is not needed (decision 19 says why
it was not chosen).

```
                                   Internet
                                      │
                           Route 53  (pay.example.com)
                                      │
                           AWS WAF    (bots, injection, rate rules)
                                      │
┌──────────────────────────── VPC · 3 Availability Zones ────────────────────────────┐
│                                     │                                               │
│  PUBLIC SUBNETS                     ▼                                               │
│     ┌─────────────── ALB · HTTPS (ACM certificate) ───────────────┐   NAT Gateway ──┼──► real PSP
│     │   health check: /health/ready on every Gateway task         │        ▲        │    (Stripe / Adyen)
│     └──────────────────────────────┬──────────────────────────────┘        │        │
│                                    │                                       │        │
│  PRIVATE SUBNETS · app             ▼                                       │        │
│               ECS Fargate · Gateway (YARP) × 2–3 tasks                     │        │
│                                    │                                       │        │
│                     ECS Service Connect (Cloud Map discovery)              │        │
│        ┌───────────┬───────────────┼───────────────┬───────────────┐       │        │
│        ▼           ▼               ▼               ▼               ▼       │        │
│    Identity     Payment ──────► Risk            Vault ─────────────────────┘        │
│                    │  └──────────────────────────► (card ops)                       │
│                    │ outbox relay                                                   │
│                    ▼                                                                │
│  PRIVATE SUBNETS · data                                                             │
│     Amazon MSK (Kafka, 3 brokers) ──► Ledger      Dispute ──► Ledger                │
│     RDS PostgreSQL / Aurora, Multi-AZ (one database + role per service)             │
│     ElastiCache Redis (Risk velocity counters)                                      │
│                                                                                     │
│  VPC endpoints (traffic to AWS APIs never leaves AWS):                              │
│     DynamoDB (Vault, Risk) · S3 (settlement files → Reconciliation)                 │
│     KMS (Vault) · Secrets Manager (all services)                                    │
└─────────────────────────────────────────────────────────────────────────────────────┘
```

Two layers of load balancing (see Module 1.6):

| Layer | Balances | Local | AWS |
|---|---|---|---|
| Edge | client → Gateway instances | none (one Gateway) | **ALB**, across availability zones |
| Service | Gateway → service instances | YARP clusters, hard-coded ports | YARP + **ECS Service Connect** (discovered addresses) |

Service-to-service calls (Payment → Risk, Payment → Vault) never go back
through the Gateway: north–south traffic enters via ALB → Gateway, east–west
traffic uses Service Connect directly.

**Local → AWS, piece by piece**

| Local (Docker Compose) | AWS | Code change? |
|---|---|---|
| — (one Gateway, no edge) | ALB + ACM certificate + WAF | None |
| Gateway (YARP) on `localhost:7080` | ECS Fargate service, 2–3 tasks | Destinations from service discovery instead of hard-coded ports |
| Hard-coded `7082` / `7092` | ECS Service Connect / Cloud Map, e.g. `http://payment:8080` | Config only |
| Each service | ECS Fargate service each, with auto scaling | None |
| Postgres container, 6 databases | RDS PostgreSQL / Aurora, Multi-AZ (one cluster with 6 databases, or one instance per service) | Connection strings only |
| Per-service roles + dev passwords | Same roles; passwords in Secrets Manager (or IAM database authentication) | Read the secret at startup |
| Redis container | ElastiCache for Redis (Multi-AZ, encryption in transit) | Connection string |
| LocalStack DynamoDB, S3, KMS | Real DynamoDB, S3, KMS | **None**: same AWS SDK, drop the endpoint override |
| Kafka container | Amazon MSK (3 brokers over 3 AZs, or MSK Serverless) | Bootstrap servers + IAM auth (MSK IAM signer, a few lines of client config) |
| PSP simulator | ECS service in staging; the real PSP via NAT Gateway in production | Swap the PSP adapter behind its interface |
| `/health/ready`, `/health/live` | ALB target group / YARP use ready; ECS container health check uses live | None |
| Console logs | CloudWatch Logs; traces via ADOT (OpenTelemetry) → X-Ray / CloudWatch | Config |

**Security layers**

| Concern | AWS mechanism |
|---|---|
| Only the ALB can reach the Gateway | Security groups: ALB SG → Gateway SG on 8080 only |
| A service can't reach another's database | Security groups (Payment SG → its RDS only) **plus** the per-service database roles from Module 1.2 |
| Nothing internal is on the internet | Services and stores in private subnets; only the ALB is public |
| AWS API calls stay inside AWS | VPC endpoints for DynamoDB, S3, KMS, Secrets Manager |
| Card data encrypted | KMS-encrypted Vault, TLS everywhere, encryption at rest on RDS / MSK / ElastiCache |
| Least privilege for AWS access | One IAM task role per service, e.g. only Vault's role may `kms:Decrypt` with the vault key |
| Audit | CloudTrail (who used KMS, when), alongside the ledger |

**Build and deploy:** AWS CDK in C# (or Terraform) for infrastructure, images
in ECR, GitHub Actions for CI/CD with rolling ECS deploys gated on readiness.
A small always-on setup is roughly $400–600/month (MSK, RDS Multi-AZ and the
NAT Gateway dominate); for learning, deploy, run the drills and tear it down.

## §1 Decision register

| # | Decision | Status | Why | Alternative rejected |
|---|---|---|---|---|
| 1 | Service-oriented, one store per service | Proposed | Same discipline as JameX/SuggestX; makes the saga boundaries real | Modular monolith (would hide every distributed-failure lesson) |
| 2 | Postgres for payments, ledger, identity, recon, disputes | Proposed | Money needs ACID, constraints, serializable transactions; ~1,400 TPS is easy for PG | DynamoDB for payments — possible (conditional writes), but double-entry invariants and history queries are far more natural in SQL |
| 3 | **Orchestrated saga** in PaymentService (explicit state machine), events for side effects | Proposed | Payment flow is linear with a clear owner; orchestration keeps the state in one inspectable row | Pure choreography (state smeared across services); Temporal/Step Functions — see §4, possible later phase |
| 4 | **Kafka** (KRaft) for the event backbone: **one topic per aggregate** (`payx.payments`, 6 partitions), message key = `payment_id`, event type inside the message | Proposed (topic shape verified in Module 1.4) | Doc names Kafka. Kafka orders only *within a partition*: same key → same partition → a payment's events stay in order. One topic per event type (`…authorized`, `…captured`) would be separate logs and the Ledger could read a capture before its authorization. Replayable log (rebuild wallets, re-run recon); new tech vs JameX's SNS/SQS | SNS→SQS FIFO (works, but no replay; already learned in JameX); topic per event type (loses per-payment ordering) |
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
| 17 | Money as a `sealed record class`: `long` minor units + ISO-4217 currency, capped at ±(2^53 − 1) | Accepted (Module 1.7) | No float rounding; currency explicit everywhere; per-currency decimals (JPY 0, KWD 3). A class, not a struct, so `default(Money)` and deserializers can't create one with a null currency. The cap keeps every amount exact in a JavaScript `Number` (JSON is parsed as doubles) | `decimal` (no currency, parsed as a double by JS); `record struct` (the `default` / hidden-constructor hole, hit in 1.7); amounts as JSON strings (avoids the cap, but every client must parse them) |
| 18 | Gateway **fails open** when every instance of a service is unhealthy (YARP `HealthyOrPanic`) | Accepted (verified in Module 1.6) | Matches AWS ALB, the production target. If every instance looks unhealthy, a broken health check is a likelier cause than every instance failing at once; failing closed would turn that into a full outage. Instances whose database really is down still fail fast with their own 503 before any money moves | Fail closed (`HealthyAndUnknown`): Gateway returns 503 itself. Cleaner when the outage is real, but a bad probe path or a probe timeout set too tight takes the whole service offline |
| 19 | Self-hosted **YARP Gateway behind an ALB**, not Amazon API Gateway | Accepted | Learning value (routing, balancing, health and fail-open are visible and testable, as decision 18 showed); identical behaviour locally and on AWS; custom C# edge logic (e.g. require `Idempotency-Key` on POST, scrub card data from logs); at the doc's ~1.5B requests/month, per-request pricing (~$1/M HTTP API, ~$3.50/M REST) costs far more than an ALB plus a few containers | Amazon API Gateway (HTTP API + VPC Link + Cloud Map): fully managed, built-in JWT authorizers, throttling, usage plans and API keys, WAF. The better choice for low or spiky volume, serverless/Lambda backends, or a public merchant API with per-client keys |

**Decision 17 notes: known limits of `Money`, deliberately left for later**

- **Allocation remainder is a business rule.** `Allocate` gives leftover
  minor units to the first parts. Real options (platform keeps it, largest
  share gets it, rotate) must be an explicit decision when multi-seller
  payouts land in Phase 7. `Allocate` also only splits *equally*; orders are
  split *by ratio* (seller prices minus fees), so a ratio-based `Allocate`
  with the same never-lose-a-cent technique is needed then.
- **Currency table is a stub.** Six hard-coded currencies. Production loads
  the full, maintained ISO-4217 list (currencies are added, retired, and
  occasionally change their number of decimals).
- **No FX conversion.** `Money` only refuses to *mix* currencies.
  Conversion needs an exchange rate with a source and timestamp, a written
  rounding policy, the rate recorded in the ledger for audit, and usually a
  spread posted as its own entry. Out of scope (§4); it would be its own
  `ExchangeRate` type and module.

### Decision 4 in detail: why Kafka, not SNS + SQS

**SNS + SQS could run PayX.** SNS FIFO fanning out to SQS FIFO queues gives
per-payment ordering via `MessageGroupId`. Kafka was chosen because of one
structural difference: **SQS is a queue, Kafka is a log.**

```
SQS (queue):  message → consumer → delete           gone once processed
Kafka (log):  message → appended at offset N        kept for the retention period (7 days here);
              each consumer group keeps its own bookmark (offset) into the same log
```

**What Kafka does better for a payment system**

| Need | Kafka | SNS + SQS |
|---|---|---|
| Replay history | Reset a group's offset and re-read the retained events: rebuild the Ledger after a bug, re-run reconciliation | Not possible: messages are deleted once processed. Needs a separate archive (e.g. SNS → Firehose → S3) |
| Per-payment ordering with parallelism | Key = `payment_id` → same partition → in order; 6 partitions processed in parallel | SQS FIFO + `MessageGroupId` works too, with tighter throughput limits |
| New consumers later | A new consumer group can start from the earliest retained event (`auto.offset.reset=earliest`) and process the history, without moving any other group's offsets | A new subscription is equally easy (no publisher change either, when publishing via SNS), but it **only sees events from subscription time on**: past events would need a hand-written backfill |
| Fan-out | Any number of consumer groups read the same log | One SQS queue per subscriber |
| Audit trail | The log is itself an ordered record of what happened | Nothing remains after processing |

**What SNS + SQS does better (the trade-off we accept)**

| SQS advantage | What it costs PayX on Kafka |
|---|---|
| Per-message ack: a failed message reappears after its visibility timeout while the rest keep flowing | Kafka commits an offset per partition, so **one bad message blocks its whole partition** until skipped. Handled by a dead-letter topic (Module 6.6) |
| Built-in dead-letter queue (`maxReceiveCount`) | Built by hand (6.6) |
| Fully serverless, nothing to size | Brokers, partitions and replication to plan (MSK Serverless reduces this) |
| Pay per message, near zero when idle | MSK has a baseline cost even when idle |
| FIFO dedup window (5 minutes) | Consumers dedupe themselves (`processed_events`, 7.4) |

**Verdict:** for a ledger-backed money system, being able to replay and audit
is worth the loss of per-message convenience. For a fire-and-forget side
effect ("email the receipt"), SQS would be the better tool.

**What PayX uses Kafka for.** All of it runs *after* the customer has their
response. Kafka is never on the synchronous payment path.

| # | Function | Producer → consumer | Module |
|---|---|---|---|
| 1 | Ledger posting: authorize/capture/refund/void → double-entry postings | Payment (outbox relay) → Ledger | 6.2, 7.3 |
| 2 | Guaranteed completion: offset committed only after the ledger's DB commit (doc ch.2, "Transaction completion") | Ledger consumer | 7.3 |
| 3 | Merchant balances (pending/available) updated in the same transaction as the postings | Ledger | 7.5 |
| 4 | Refund and dispute reversals | Dispute → Ledger | 9.1 |
| 5 | Poison messages moved to `payx.payments.dlq` so the partition keeps moving | Ledger → DLQ topic | 6.6 |
| 6 | Replay drill: reset offsets, rebuild Ledger state | Ops | 11 |
| 7 | Future consumers (notifications, analytics) with no change to Payment | — | optional |

Not on Kafka: Payment → Risk and Payment → Vault → PSP (synchronous HTTP, the
customer is waiting), PSP webhooks (HTTP), the settlement file (S3).

**AWS options for this role**

| AWS service | What it is | Fit for PayX |
|---|---|---|
| **Amazon MSK** (provisioned, or Express brokers) | Managed Apache Kafka: AWS runs the brokers, you choose size and count | **The production target.** The same Kafka protocol as the local container, so the code doesn't change, only the bootstrap servers and IAM auth (SASL with the AWS MSK IAM signer) |
| **Amazon MSK Serverless** | Kafka with no broker sizing; capacity scales automatically | Good fit at ~1,400 TPS; trades some configuration control and per-cluster limits for no capacity planning |
| **Amazon Kinesis Data Streams** | AWS's own log service. Shards ≈ partitions, partition key ≈ message key, retention 24 h by default and extendable up to 365 days, consumers checkpoint via KCL (in DynamoDB) | The closest AWS-native equivalent: same log model, per-key ordering, replay. Different API (AWS SDK, not the Kafka protocol) and per-shard throughput limits. A valid alternative if you want to stay fully AWS-native |
| **Amazon EventBridge** | Event bus with rule-based routing; has *archive and replay* | Strong for routing events between many services or SaaS integrations; no partition ordering guarantees, so not for ledger posting |
| **SNS + SQS (FIFO)** | Pub/sub fan-out to queues | Covered above: workable, but no replay |

**Why Kafka in Docker locally, not LocalStack:** the container runs real
Apache Kafka, which is exactly what MSK runs, so local behaviour matches
production. (LocalStack emulates Kinesis on its free tier, so Kinesis is
also a one-module experiment if we ever want to compare the two.)

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

- *Liveness vs readiness: what is each for?* (Module 1.5) Liveness = "is the
  process stuck?" and the orchestrator **restarts** it on failure, so it runs
  no dependency checks. Readiness = "can it take work now?" and the load
  balancer **stops routing** to it on failure, without restarting it. Never
  put a database in liveness (a blip restarts every instance at once: a
  restart storm). Readiness checks only what the instance can't work without
  (its own store), never other services (one failure would cascade
  "not ready" up the whole call chain; use timeouts/breakers/fallbacks).
- *Database down: does the load balancer still send traffic?* If only some
  instances can't reach it, no: readiness removes them. If the database
  itself is down, every instance fails together and there is nowhere better
  to route: AWS ALB **fails open** (sends to all targets anyway), Kubernetes
  has no endpoints (immediate 503). Either way the protection is the service
  failing fast with a 503 before any money moves, plus idempotent client
  retries. Health checks are a routing optimisation, not a correctness
  guarantee, and detection lag (interval × threshold) means some requests
  always hit a broken instance first.

## §4 Open questions / deferred

- Temporal or AWS Step Functions as the saga engine — a possible late phase to
  contrast with the hand-rolled orchestrator.
- Multi-currency and FX — out of scope initially (single currency per merchant).
- 3-D Secure challenge flow — modelled as a `CHALLENGED` state; full flow deferred.
- Multi-PSP routing / failover — mention only, unless time allows.
- Payouts from merchant available balance to bank — deferred.

## §5 Doc-to-code map

Filled in as phases land.
