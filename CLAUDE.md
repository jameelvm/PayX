# PayX

A working card-payment system built to internalise the system design in
`../*.pdf` ("Introduction to the Payment System" and "Design of a Payment
System", chapters 1–2). The goal is **understanding through implementation**:
every component in the design doc has a real, runnable counterpart here.

## Read this first

- **`PROGRESS.md`** — live build state. What is done, what is next, where the
  last session stopped. **Update it at the end of every working session.**
- **`README.md`** — end-to-end teaching documentation. **Update it at the end of
  every phase**, explaining what was built and why, with verification steps and
  design talking points. It is revision material, not a change log.
- **`DESIGN.md`** — the system design summary: decision register, failure-mode
  table, Q&A bank and a doc-to-code coverage map. **Extend it at the end of
  every phase.** README explains *how* it was built; DESIGN explains *why it is
  shaped this way*.
- `RUNNING.md` and `DEBUGGING.md` arrive with Phase 1, same role as in the
  sibling projects.

## Owner context

The author is a C#/.NET developer with AWS, PostgreSQL and DynamoDB experience,
and has already built two companion projects with this exact pattern —
`JameX` (YouTube, `../../Youtube/App`) and `SuggestX` (typeahead,
`../../TypeheadSuggestion/App`). Prefer idiomatic .NET and real AWS service
APIs over bespoke abstractions — the code should double as an answer to "how
would you actually build this on AWS?".

## What this system actually is

The functional surface is small — register, pay by card, see history, see a
balance — but the design doc's real content is **correctness under partial
failure**: a payment crosses five or six parties (customer, merchant, our
services, PSP, card network, issuer) and any hop can time out *after* doing its
work. The interesting questions are all of the form "the network dropped the
response — was the customer charged?". The whole system is shaped by three
rules:

1. **Never move money twice** — idempotency keys on every money-moving call,
   end to end, including the outbound call to the PSP.
2. **Never lose a payment event** — state change and the event announcing it
   are committed atomically (transactional outbox), and consumers only ack
   after the ledger write commits (doc: "a message is only marked as consumed
   after the transaction is … recorded in the wallet and ledger").
3. **Never trust a single record** — the ledger is append-only double-entry,
   and an independent reconciliation job checks it against the PSP's
   settlement file every day.

The doc's two chapters build up to it:

1. **Requirements** — card payments via a PSP; FRs: registration/auth, payment
   processing, transaction history, balance management, mobile access. NFRs:
   integrity & security, availability, reliability, scalability, performance.
   Estimates: 50M txn/day, ~1,400 TPS peak, ~5GB/day. (The server estimate in
   the doc divides 50M by 64K RPS treating *daily transactions* as *requests
   per second* — see `DESIGN.md` §0 for the corrected numbers.)
2. **Design** — payment service → fraud detection + risk check → payment
   gateway/PSP → card network → issuer; wallet + immutable ledger;
   reconciliation against the PSP's EOD settlement file; dispute management;
   Kafka for transaction completion; retry/backoff, timeouts, idempotency and
   fallbacks for transient failures. Card flow is two-phase:
   **authorize** (reserve funds) then **capture/settle** (move them).

## Architecture

**Service-oriented**, each service owning its data exclusively. Payment is an
**orchestrated saga**: PaymentService owns the state machine and drives each
step; downstream effects (ledger, wallet, notifications) are driven by events
off Kafka.

| Service | Owns exclusively | Talks to |
|---|---|---|
| Gateway | — (YARP, JWT validation, rate limiting) | all services |
| Identity | Postgres `payx_identity` (users, merchants, credentials) | — |
| Vault | DynamoDB `payx-card-vault` (KMS-envelope-encrypted PANs → tokens) | KMS |
| Payment | Postgres `payx_payments` (payments, attempts, idempotency keys, outbox) | Risk, PSP (HTTP); Kafka (publish) |
| Risk | Redis `risk:*` velocity counters, DynamoDB `payx-risk-decisions` | — |
| Ledger | Postgres `payx_ledger` (append-only journal + account balances / wallets) | Kafka (consume) |
| Reconciliation | Postgres `payx_recon` (runs, discrepancies) | S3 settlement files (read), Ledger API |
| Dispute | Postgres `payx_disputes` | Ledger, PSP webhooks |
| **PSP Simulator** | its own in-memory/Postgres state, S3 `payx-settlement-files` | — (stands in for the outside world) |

The PSP simulator is not "our" system — it plays Stripe/Adyen + card network +
issuer bank, with deterministic test cards and **failure injection** (latency,
5xx, timeout-after-success, duplicate webhook). Every reliability lesson in the
doc is demonstrated against it.

## Stack (proposed — see `DESIGN.md` §1 for status of each decision)

| Layer | Choice | Stands in for (per the doc) |
|---|---|---|
| Services | .NET 10, ASP.NET Core MVC controllers + `BackgroundService` workers | — |
| Gateway | YARP + ASP.NET rate limiter | load balancer / web servers |
| Relational | PostgreSQL 17, one database per owning service | "associated database" |
| Event backbone | **Apache Kafka** (KRaft, single broker locally), `Confluent.Kafka` client | Kafka (unchanged — MSK in prod) |
| Event publishing | Transactional outbox in Postgres + relay worker | — (the doc's "no lost events" made concrete) |
| Resilience | `Microsoft.Extensions.Http.Resilience` (Polly v8): retry + jitter, timeout, circuit breaker, fallback | retry / timeout / fallback |
| Card data | Vault service, **AWS KMS** envelope encryption, DynamoDB | "where are card details encrypted?" |
| Risk state | Redis (velocity counters, sliding windows) | risk check system |
| Settlement files | S3 | PSP EOD settlement file |
| Observability | **OpenTelemetry** → standalone .NET Aspire dashboard | — |
| Load testing | **k6** | validates the 1,400 TPS estimate |
| Frontend | Next.js App Router — checkout, customer history, merchant dashboard, ops console | merchant's online store |
| Local runtime | Docker Compose + LocalStack (S3, DynamoDB, KMS, Secrets Manager) | — |

## Layout (target)

```
App/
├── CLAUDE.md  PROGRESS.md  README.md  DESIGN.md
├── docker-compose.yml
├── .env                    # LOCALSTACK_AUTH_TOKEN — gitignored
├── infra/
│   ├── docker/             # Service.Dockerfile (parameterised)
│   ├── localstack/init/    # buckets, tables, KMS key
│   └── postgres/           # one database per service
├── src/
│   ├── shared/
│   │   ├── PayX.Contracts/        # DTOs + Kafka event schemas; no infra deps
│   │   └── PayX.ServiceDefaults/  # AWS clients, Kafka, outbox, Redis, OTel, health
│   └── services/
│       ├── PayX.Gateway/
│       ├── PayX.Identity/
│       ├── PayX.Vault/
│       ├── PayX.Payment/
│       ├── PayX.Risk/
│       ├── PayX.Ledger/
│       ├── PayX.Reconciliation/
│       ├── PayX.Dispute/
│       └── PayX.PspSimulator/
├── tests/load/             # k6 scripts
└── web/                    # Next.js frontend
```

## Conventions

- **One service owns a store.** No service reads another's database.
- **Money is `long` minor units + ISO-4217 currency**, never `decimal` floats
  on the wire and never `double` anywhere. A `Money` value type lives in
  `PayX.Contracts`.
- **Every money-moving endpoint requires an `Idempotency-Key` header.**
  Every outbound money-moving call forwards a derived key to the next hop.
- **State changes + their events commit in one DB transaction** (outbox).
  Nothing calls `producer.Produce` directly from request-handling code.
- **The ledger is append-only.** Corrections are reversing entries, never
  `UPDATE`/`DELETE` — enforced in the database, not just by convention.
- **Raw PANs never leave Vault.** Every other service sees a token + last4 +
  brand. Logs are scrubbed.
- **Layered inside each service**: `Api/` → `Services/` → `Repositories/` →
  `Domain/`; workers get `Jobs/`. Controllers, not minimal APIs.
- AWS resources are named `payx-*`; Kafka topics `payx.<domain>.<event>`.
- Anything that demonstrates a design-doc concept carries a comment naming the
  chapter/section it comes from.
- Batch cadences (settlement, reconciliation) are configurable and set short
  for local demoing — always documented as such.

## Ports

Shifted to coexist with JameX (8080-8090, 3100, 5432, 6379, 4566) and SuggestX
(9080-9084, 3010, 6380, 4567, 2181):

web `3020`, gateway `7080`, identity `7081`, payment `7082`, risk `7083`,
vault `7084`, ledger `7085`, reconciliation `7086`, dispute `7087`,
psp-simulator `7088`, Postgres `5434`, Redis `6381`, LocalStack `4568`,
Kafka `9094` (host listener), Kafka UI `7090`, Aspire dashboard `18888`.
