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
| `payx-redis` | 6381 | Risk velocity counters (later) |
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

# terminal 3: Gateway
dotnet run --project src/services/PayX.Gateway --launch-profile "Gateway (local)"
```

Check:

```bash
curl localhost:7080/api/payments/ping     # alternates "instance":"7082" / "7092"
curl localhost:7082/health/ready          # postgres:Payments Healthy
```

## 4. Tests

```bash
dotnet test PayX.slnx
```

## Windows / Git Bash notes

- Git Bash rewrites absolute container paths (`/opt/kafka/...` becomes
  `C:/Program Files/Git/opt/...`). Prefix `docker exec` commands with
  `MSYS_NO_PATHCONV=1`, or use PowerShell.
- `.sh` files are kept LF by `.gitattributes`; bash inside a container fails
  on CRLF.
