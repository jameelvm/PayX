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
| `payx-redis` | 6381 | Vault: CVCs until first authorization (`vault:cvc:*`); Risk velocity counters (Phase 5) |
| `payx-localstack` | 4568 | S3, DynamoDB, KMS |
| `payx-kafka` | 9094 | Kafka broker (containers use `kafka:9092`) |
| `payx-kafka-ui` | 7090 | http://localhost:7090 |

Postgres init scripts run only on an empty volume. After changing one:
`docker compose down -v && docker compose up -d`. LocalStack keeps no state
across restarts (its bootstrap script recreates everything, including a new
KMS key).

## 3. Services (run on the host for now)

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
```

Test cards (simulator): `4242424242424242` approves; `4000000000000002`
generic decline, `4000000000009995` insufficient funds, `4000000000000069`
expired, `4000000000000127` incorrect CVC.
Each card number has a $5,000 limit that holds and captures use up, so
repeated demos on one card eventually decline: use a fresh Luhn-valid number.

**LocalStack is not persistent.** Restarting it creates a new KMS key and an
empty `payx-card-vault` table, so every token from before the restart is gone.

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

## Windows / Git Bash notes

- Git Bash rewrites absolute container paths (`/opt/kafka/...` becomes
  `C:/Program Files/Git/opt/...`). Prefix `docker exec` commands with
  `MSYS_NO_PATHCONV=1`, or use PowerShell.
- `.sh` files are kept LF by `.gitattributes`; bash inside a container fails
  on CRLF.
