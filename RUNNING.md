# Running PayX locally

Grows as phases land. Current as of the end of Phase 1.

## Prerequisites

- Docker Desktop
- .NET 10 SDK (`dotnet --version` → 10.x)
- A LocalStack auth token (free account at https://app.localstack.cloud)

## 1. Secrets

Create `.env` next to `docker-compose.yml` (gitignored, never committed):

```
LOCALSTACK_AUTH_TOKEN=<your token>
```

## 2. Platform containers

```bash
docker compose up -d
docker compose ps        # postgres, redis, localstack, kafka healthy; kafka-init "exited (0)"
```

| Container | Host port | What it is |
|---|---|---|
| `payx-postgres` | 5434 | Six databases, one login role each (admin: `payx_admin` / `payx_admin`) |
| `payx-redis` | 6381 | Vault: CVCs until first authorization (`vault:cvc:*`); Risk velocity counters (`risk:velocity:*`, 5.2) |
| `payx-localstack` | 4568 | S3, DynamoDB, KMS |
| `payx-kafka` | 9094 | Kafka broker (containers use `kafka:9092`) |
| `payx-kafka-ui` | 7090 | http://localhost:7090 |

Postgres init scripts run only on an empty volume. After changing one:
`docker compose down -v && docker compose up -d`. LocalStack keeps no state
across restarts (its bootstrap script recreates everything, including a new
KMS key).

## 3. Services

**In containers (default):** `docker compose up -d --build` runs Gateway,
Identity, Payment, Risk, Vault and the PSP simulator on the ports below, next to
the platform containers. To step through one service in Visual Studio,
stop its container and press F5 on the same port: [`DEBUGGING.md`](DEBUGGING.md).
Click through every scenario in the **test console**:
`http://localhost:7080/console/`. Or send requests from `PayX.http`
(Visual Studio) or Scalar at `http://localhost:<port>/scalar`.

**On the host (all of them, without containers):** stop the service
containers first (`docker compose stop gateway identity payment risk vault psp-simulator`).

```bash
dotnet build PayX.slnx

# terminal 1 and 2: two Payment instances
dotnet run --project src/services/PayX.Payment --launch-profile "Payment (local)"
dotnet run --project src/services/PayX.Payment --no-launch-profile --urls http://localhost:7092 --environment Development

# terminal 3: Identity (applies its migrations on start)
dotnet run --project src/services/PayX.Identity --launch-profile "Identity (local)"

# terminal 4: Gateway (fetches Identity's public key, so start Identity first)
dotnet run --project src/services/PayX.Gateway --launch-profile "Gateway (local)"
```

Identity creates its signing key on first login at `.keys/identity-signing-key.pem`
(gitignored). Delete it to rotate; existing tokens then stop verifying.

Check:

```bash
# register, log in, call a protected route
curl -X POST localhost:7080/api/users -H "Content-Type: application/json" \
  -d '{"username":"priya_s","email":"priya@example.com","password":"correct horse battery","role":"Customer"}'

TOKEN=$(curl -s -X POST localhost:7080/api/users/login -H "Content-Type: application/json" \
  -d '{"username":"priya_s","password":"correct horse battery"}' | python -c "import sys,json;print(json.load(sys.stdin)['accessToken'])")

curl localhost:7080/api/payments/ping -H "Authorization: Bearer $TOKEN"   # 200, alternates 7082 / 7092
curl localhost:7080/api/payments/ping                                     # 401
curl localhost:7082/health/ready                                          # postgres:Payments Healthy
```

Ops accounts can't self-register; create one in psql as an admin would
(see PROGRESS.md, Module 2.4).

### PSP simulator and Vault (Phase 3)

```bash
# terminal 5: PSP simulator (applies its migrations on start; Postgres)
dotnet run --project src/services/PayX.PspSimulator --launch-profile "PspSimulator (local)"

# terminal 6: Vault (KMS + DynamoDB in LocalStack, CVCs in Redis: start both first)
dotnet run --project src/services/PayX.Vault --launch-profile "Vault (local)"
```

Neither has a Gateway route: only PayX's own services call them.

```bash
# authorize (every POST needs an Idempotency-Key), then look it up
curl -X POST localhost:7088/api/psp/authorizations -H "Content-Type: application/json" \
  -H "Idempotency-Key: demo-1" \
  -d '{"amount":3000,"currency":"USD","card":{"number":"4242424242424242","expMonth":12,"expYear":2030,"cvc":"123"},"merchantId":"m_1"}'
curl localhost:7088/api/psp/authorizations/<authorizationId>

# make one call misbehave (Development only): latency=ms, error=5xx, timeout-after-success
curl -m 3 -X POST localhost:7088/api/psp/authorizations -H "X-Psp-Fault: timeout-after-success" ...

# tokenize a card; nothing but the token, brand, last4 and expiry ever comes back
curl -X POST localhost:7084/api/vault/cards -H "Content-Type: application/json" \
  -d '{"number":"4242 4242 4242 4242","expMonth":12,"expYear":2030,"cvc":"123"}'
curl localhost:7084/health/ready     # redis + kms:card-vault + dynamodb:card-vault
docker exec payx-redis redis-cli TTL vault:cvc:<token>   # CVC held, encrypted, ~600 s

# authorize on the token (needs the PSP simulator running): Vault adds card + CVC
curl -X POST localhost:7084/api/vault/cards/<token>/authorizations -H "Content-Type: application/json" \
  -H "Idempotency-Key: demo-2" -d '{"amount":5000,"currency":"USD","merchantId":"m_1"}'
# a second authorization on the same token → 409 cvc_unavailable (CVC used once)
```

### Paying end to end (Module 4.2c)

Every scenario (double-click, outages, capture, refusals), with its API
calls and backend logic: [`docs/PAYMENT-CORE.md`](docs/PAYMENT-CORE.md).

All five running (Identity, Gateway, Payment, Vault, PSP simulator).

```bash
# log in as a Customer (see section 3) → $TOKEN; a Merchant's user id is the merchantId
TOK=$(curl -s -X POST localhost:7084/api/vault/cards -H "Content-Type: application/json" \
  -d '{"number":"4242424242424242","expMonth":12,"expYear":2030,"cvc":"123"}' | python -c "import sys,json;print(json.load(sys.stdin)['token'])")
curl -X POST localhost:7080/api/payments -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -H "Idempotency-Key: checkout-1" \
  -d "{\"merchantId\":\"<merchant-user-id>\",\"amount\":{\"amountMinor\":5000,\"currency\":\"USD\"},\"cardToken\":\"$TOK\"}"
# 201 {"status":"Authorized", ...}; a decline is also 201 with status Declined
# the same request again with the same key → the same 201, header Idempotent-Replayed: true

# the merchant captures (log in as that Merchant → $MTOKEN); omit the body for the full amount
curl -X POST localhost:7080/api/payments/<payment-id>/capture -H "Authorization: Bearer $MTOKEN" \
  -H "Idempotency-Key: capture-1" \
  -H "Content-Type: application/json" -d '{"amountMinor":3000}'
# 200 {"status":"Captured","capturedAmountMinor":3000, ...}
```

Test cards (simulator): `4242424242424242` approves; `4000000000000002`
generic decline, `4000000000009995` insufficient funds, `4000000000000069`
expired, `4000000000000127` incorrect CVC.
Each card number has a $5,000 limit that holds and captures use up, so
repeated demos on one card eventually decline: use a fresh Luhn-valid number.

### Risk (Phase 5)

Payment asks Risk (port 7083, internal: no Gateway route) before every
authorization, so Risk must be running for payments to go through
normally. Risk needs Redis.

```bash
# ask Risk directly (Scalar: http://localhost:7083/scalar)
curl -X POST localhost:7083/api/risk/assessments -H "Content-Type: application/json"   -d '{"paymentId":"pay_demo","customerId":"6f1c2b9e-0000-4000-8000-000000000001","amountMinor":600000,"currency":"USD","cardCountry":"GB"}'
# {"decision":"Challenge","score":65,"reasons":[{"code":"amount_high","points":50},{"code":"cross_border","points":15}]}
```

Cards by country (Vault derives it from the BIN, 5.4): `4242424242424242`
US, `4000008260000000` GB (cross-border), `4000009900000004` XR (the
high-risk test country). Random console cards have no known country.

| Pay through the Gateway | Expected |
|---|---|
| $50, US card | 201 `Authorized` |
| $6,000, GB card (fresh customer) | 201 `Challenged` |
| $6,000, XR card | 201 `RiskBlocked` |
| 5+ payments by one customer within 10 minutes | velocity points: Challenge / Block sooner |

**Risk outage drill (5.6–5.7):** `docker pause payx-risk`, then pay $500
several times: 504 `risk_timeout` after 2 s each, then 503
`risk_circuit_open` instantly once the breaker opens; a $50 payment still
goes through (fallback, stored with reason `risk_unavailable_fallback`).
`docker unpause payx-risk`, wait 15 s: back to normal. Policy lives in
configuration: `Risk:Rules` and `Risk:Decision` in Risk's
`appsettings.json` (applied on the next request), `Risk:Timeout`,
`Risk:CircuitBreaker` and `Risk:Fallback` in Payment's.

**LocalStack is not persistent.** Restarting it creates a new KMS key and an
empty `payx-card-vault` table, so every token from before the restart is gone.

### Reliability (Phase 6)

Payment now needs Kafka for its events (the relay retries until Kafka is
there; payments work without it). Full design: [`docs/RELIABILITY.md`](docs/RELIABILITY.md).

```bash
# events waiting / published (outbox)
docker exec payx-postgres psql -U payx_payments -d payx_payments \
  -c "select id, event_type, key, published_at from outbox_messages order by id desc limit 10"
# the events on Kafka: Kafka UI http://localhost:7090 → Topics → payx.payments (key = payment id)
```

| Drill | Do | Expect |
| --- | --- | --- |
| Kafka down | `docker compose stop kafka`, pay and capture, `docker compose start kafka` | 201 / 200; events wait (`published_at` empty), then publish within seconds |
| Bank blip | `docker pause payx-psp-simulator`, pay, `docker unpause` within ~3 s | 201 after a retry (Vault logs `Execution attempt`) |
| Bank silent | `docker pause payx-psp-simulator`, pay, wait > 10 s | 202 `Unknown`; after `docker unpause`, the same key → the bank's answer |
| Nobody comes back | `docker compose stop psp-simulator`, pay (502), `docker compose start psp-simulator`, wait 60–75 s | status `Failed` (or `Authorized` if the PSP did hold it): the resolver |
| Dead letters | publish a malformed message to `payx.payments` from Kafka UI | the Ledger dead-letters it: Kafka UI → `payx.payments.dlq`, headers `dlq-*` |

New statuses: `Unknown` (answered 202 Accepted) and `Failed`.

### Ledger (Phase 7)

The Ledger (7085) books every payment event from Kafka into a double-entry
journal and keeps balances. It needs Postgres and Kafka (`kafka-init`
done), and the database role `payx_ledger_app`: a data volume created
before Phase 7 needs it added once (idempotent):

```bash
docker exec -i payx-postgres psql -U payx_admin -d payx_admin < infra/postgres/upgrade/07-ledger-app-role.sql
docker compose up -d --build ledger gateway
```

Full design: [`docs/LEDGER.md`](docs/LEDGER.md).

```bash
# history and balances, through the Gateway, as the customer ($TOKEN) or the merchant ($MTOKEN)
curl -s localhost:7080/api/ledger/transactions -H "Authorization: Bearer $TOKEN"
curl -s "localhost:7080/api/ledger/transactions?limit=5&before=<nextBefore>" -H "Authorization: Bearer $MTOKEN"
curl -s localhost:7080/api/ledger/balance -H "Authorization: Bearer $MTOKEN"
# after pay $50 + capture $30: merchant {"authorized":0,"pending":3000,"available":0}, customer {"onHold":0}

# the books in psql (read-only role is enough)
docker exec payx-postgres psql -U payx_ledger_app -d payx_ledger \
  -c "select e.id, e.kind, e.payment_id, a.type, p.amount_minor from journal_entries e join postings p on p.entry_id = e.id join accounts a on a.id = p.account_id order by e.id desc, p.id limit 12"
# audits: accounts whose balance differs from their postings (expect none), trial balance (expect 0)
docker exec payx-postgres psql -U payx_ledger_app -d payx_ledger \
  -c "select a.id from accounts a left join postings p on p.account_id = a.id group by a.id, a.balance_minor having a.balance_minor <> coalesce(sum(p.amount_minor), 0)" \
  -c "select currency, sum(balance_minor) from accounts group by currency"
```

| Drill | Do | Expect |
| --- | --- | --- |
| Lag | Kafka UI → Consumers → `payx-ledger` | lag 0 a second after a payment |
| Edit history | as `payx_ledger_app`: `update postings set amount_minor = 1` | `permission denied`; as the owner `payx_ledger`: `the ledger is append-only` |
| Ledger down | `docker compose stop ledger`, pay and capture, `docker compose start ledger` | payments unaffected; the Ledger books the backlog on start |
| Replay everything | stop the Ledger; `kafka-consumer-groups.sh --bootstrap-server kafka:9092 --group payx-ledger --topic payx.payments --reset-offsets --to-earliest --execute` (in `payx-kafka`, `MSYS_NO_PATHCONV=1` from Git Bash); start it | entry count unchanged; logs `redelivery ignored` / `already booked under another event id` |

The reset is refused while the group has members ("group is not
inactive"): stop the Ledger first. Since 7.4 a stopped Ledger leaves the
group at once; before that fix it took ~45 s.

## 4. Tests

```bash
dotnet test PayX.slnx
```

`PayX.Vault.Tests` are integration tests against LocalStack's KMS and
DynamoDB and Redis, so `docker compose up -d localstack redis` first
(Modules 3.6, 4.2a). They fail, rather
than skip, when it is down: a crypto test that silently skips is worse
than one that fails.
`PayX.Payment.Tests` include two integration tests against `payx_payments`,
so Postgres must be up too (Module 4.1).
`PayX.Risk.Tests` include Redis tests (velocity, 5.2): Redis must be up.
`PayX.Payment.Tests` include Kafka tests (relay 6.2, dead-letter consumer 6.6): Kafka must be up (`docker compose up -d kafka kafka-init`).
`PayX.Ledger.Tests` run against `payx_ledger` as both roles (owner for migrations, `payx_ledger_app` for everything else): Postgres up and the 7.2 role created. Stop the `ledger` container before running tests that add a migration: an old Ledger booking against a newer schema can leave rows the new code expects (7.5's balances).

## Windows / Git Bash notes

- Git Bash rewrites absolute container paths (`/opt/kafka/...` becomes
  `C:/Program Files/Git/opt/...`). Prefix `docker exec` commands with
  `MSYS_NO_PATHCONV=1`, or use PowerShell.
- `.sh` files are kept LF by `.gitattributes`; bash inside a container fails
  on CRLF.
- **Smart App Control** (Windows 11) blocks freshly built, unsigned .NET
  binaries: "An Application Control policy has blocked this file" when
  starting a service, or a test project that reports no tests and exits 1.
  It has no allow-list (Defender exclusions, Dev Drive and self-signed
  certificates don't help). The fix used on this machine (2026-10-21):
  Windows Security → App & browser control → Smart App Control → Off.
  Alternative that keeps it on: build and run under WSL2 or in a container.
  Before turning it off, a stop-gap for a blocked `.exe` was running the DLL
  through the signed `dotnet` host:
  `ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:7082 dotnet src/services/PayX.Payment/bin/Debug/net10.0/PayX.Payment.dll`
