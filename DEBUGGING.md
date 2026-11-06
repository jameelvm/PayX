# Debugging PayX in Visual Studio

Run any service under the debugger while the rest of the system keeps
running in Docker, without editing any configuration. Same approach as the
sibling SuggestX project, with three differences found while setting it up
here (marked **PayX**).

---

## The idea

Every service has **two homes on the same port**:

| Service | Port | Container (`docker compose`) | Visual Studio profile |
|---|---|---|---|
| Gateway | 7080 | `gateway` | `Gateway (local)` |
| Identity | 7081 | `identity` | `Identity (local)` |
| Payment | 7082 | `payment` | `Payment (local)` |
| Risk | 7083 | `risk` | `Risk (local)` |
| Vault | 7084 | `vault` | `Vault (local)` |
| PSP simulator | 7088 | `psp-simulator` | `PspSimulator (local)` |

Infrastructure is always a container and never moves: Postgres `5434`,
Redis `6381`, LocalStack `4568`, Kafka `9094`.

**Every call from one service to another goes to the target's port on
this machine.** Containers reach it through the alias `payx-host` (Docker
Desktop's `host-gateway`); a service in Visual Studio reaches it as
`localhost`. Whatever is listening on that port answers: the container, or
your debugger after you stopped the container.

```
docker compose stop payment     → port 7082 frees up
F5 "Payment (local)"            → your debugger binds 7082
                                → the Gateway (container) reaches your debugger
                                → your Payment calls Vault / PSP / Identity containers on localhost
docker compose start payment    → back to the container (stop the debugger first)
```

---

## Debug one service

```bash
# 1. Everything up (first time: builds the five images, a few minutes)
docker compose up -d --build

# 2. Stop only the service you want to step through
docker compose stop payment

# 3. Visual Studio: set PayX.Payment as startup project, profile "Payment (local)", F5.
#    The browser opens Scalar (http://localhost:7082/scalar).

# 4. Send requests: PayX.http, Scalar, or curl. Breakpoints hit.

# 5. Done: stop debugging (Shift+F5), then
docker compose start payment
```

Several at once: the solution has a Visual Studio launch profile file,
`PayX.slnLaunch`. Pick **"Payment + Vault"**, **"Payment + Risk"** or **"Payment core (all five)"**
in the start dropdown, after stopping those containers
(`docker compose stop payment vault`, or all five).

### Who answered? `X-PayX-Instance`

Every response says which process handled it, one header per hop:

```
X-PayX-Instance: PayX.Gateway@9e018daad0dc        ← a container (its id)
X-PayX-Instance: PayX.Payment@JAMEELMOIDEEN       ← your PC: the debugger
```

**PayX:** look at this before trusting a breakpoint that didn't hit. It is
the fastest way to see that a request went to the container, not to you.

---

## Sending requests

| Tool | Where | Notes |
|---|---|---|
| **Test console** | `http://localhost:7080/console/` (Development) | A web page with a button for every scenario: create users, enter a card, pay, double-click, reuse a key, status as each user, capture. Logs every request and answer with `X-PayX-Instance` and `Idempotent-Replayed`. Served by the Gateway, so everything goes through it. |
| **`PayX.http`** | repo root; open in Visual Studio | Every scenario from [`docs/PAYMENT-CORE.md`](docs/PAYMENT-CORE.md), in order. Logs in, keeps the tokens and ids from earlier answers. Needs Visual Studio 17.12 or later (request variables). |
| **Scalar** | `http://localhost:7081/scalar` (Identity), `:7082/scalar` (Payment), `:7084/scalar` (Vault), `:7088/scalar` (PSP) | A page per service to send requests from the browser. Paste an access token into **Authentication → Bearer** once. Endpoints that need an `Idempotency-Key` show it as a required header. Development only. |
| curl | anywhere | examples in [`RUNNING.md`](RUNNING.md) |

Scalar talks to a service directly, bypassing the Gateway. `PayX.http` goes
through the Gateway (`:7080`) for Payment, as a real client would, and
directly to Vault and the PSP, which have no Gateway route.

### A temporary public URL (optional)

To call your local PayX from another device (a phone, a colleague),
Visual Studio can create a **dev tunnel**: *View → Other Windows → Dev
Tunnels* (or the dropdown next to the start button → *Dev Tunnels → Create a
Tunnel*), sign in, choose **Temporary** and **Private** access, and start
the Gateway project from Visual Studio. You get an
`https://<random>.devtunnels.ms` URL for port 7080 that lives until Visual
Studio closes. Keep it Private: registration is open, so a public tunnel
lets anyone create users and send payments to your machine.

---

## Breakpoints per scenario

Scenario numbers are the sections of [`docs/PAYMENT-CORE.md`](docs/PAYMENT-CORE.md);
the matching requests are in `PayX.http`. Stop the containers of the
services you want to step through.

| Scenario | Debug | Breakpoints (file → method) | Watch |
|---|---|---|---|
| §2 Tokenize | Vault | `Api/CardsController.cs` → `Tokenize`; `Services/CardVault.cs` → `TokenizeAsync`; `Repositories/CvcStore.cs` → `HoldAsync`; `Services/CardEncryptor.cs` → `EncryptAsync` | the token exists before the ciphertext; `dataKey.Plaintext` zeroed in `finally` |
| §3 Pay, approved | Payment, Vault, PSP | Payment `Api/IdempotentAttribute.cs` → `OnActionExecutionAsync`; `Services/IdempotencyService.cs` → `ClaimAsync`; `Services/PaymentAuthorization.cs` → `AuthorizeAsync`, `ResumeOrCreateAsync`; `Services/VaultClient.cs` → `AuthorizeAsync`. Vault `Services/CardAuthorizer.cs` → `AuthorizeAsync`. PSP `Api/AuthorizationsController.cs` → `Authorize`; `Services/Authorizer.cs` → `AuthorizeAsync` | `payment.Status` after each `SaveChangesAsync`; the `Idempotency-Key` header Vault sends (`{paymentId}:authorize`) |
| §4 Declined | PSP, Payment | PSP `Domain/IssuerBank.cs` → `Decide`; Payment `PaymentAuthorization.AuthorizeAsync` (the `else` branch: `Decline`) | `answer.Status == "declined"`; still a 201 |
| §6 Double-click | Payment | `IdempotencyService.ClaimAsync`: the `catch` for `UniqueViolation` | send the same request twice while paused in `AuthorizeAsync`: the second gets 409 `request_in_progress` |
| §7 Same key, new body | Payment | `IdempotencyService.ClaimAsync` → `existing.RequestHash != requestHash` | 422 |
| §8 Bank down, retry | Payment, Vault | Vault `CardAuthorizer.AuthorizeAsync`: `catch (HttpRequestException)` (CVC **not** forgotten). Payment `IdempotentAttribute`: the `else` → `ReleaseAsync`. On the retry: `ClaimAsync` → `ExecuteUpdateAsync` (takeover, `Resumed`), `ResumeOrCreateAsync` (loads the same payment) | `key.Record.PaymentId` on the retry; risk skipped because the status is already `RiskApproved` |
| §10 Used card token | Vault | `CardAuthorizer.AuthorizeAsync` → `GetCvcAsync` returns null | 409 from Vault → 422 to the client |
| §11 Status | Payment | `Services/PaymentQueries.cs` → `FindVisibleAsync` | the `CustomerId == callerId \|\| MerchantId == callerId` filter |
| §12 Capture | Payment, PSP | Payment `Services/PaymentCapture.cs` → `CaptureAsync`; `Domain/Payment.cs` → `CaptureRefusal`; `Services/PspClient.cs` → `CaptureAsync`. PSP `Services/AuthorizationActions.cs` → `CaptureAsync`; `Domain/Authorization.cs` → `Capture` | refusals happen before the PSP call |
| §13 Capture refused | Payment | `PaymentCapture.CaptureAsync` (merchant filter, `CaptureRefusal`) | the PSP is never called |
| Risk scoring (Phase 5) | Risk | `Api/AssessmentsController.cs` → `Assess`; `Repositories/SlidingWindowCounter.cs` → `RecordAsync`; `Domain/RiskRules.cs` → `Score`; `Domain/RiskPolicy.cs` → `Decide` | `recent` (velocity count), `score.Reasons`, `decision` |
| Payment asks Risk (5.5) | Payment | `Services/PaymentAuthorization.cs` → the `Created` block; `Services/FallbackRiskCheck.cs` → `CheckAsync`; `Services/RiskClient.cs` → `CheckAsync` | `card?.Country` from Vault; `assessment.Decision`; which `catch` fires when Risk fails |
| Risk outage (5.6–5.7) | Payment | `RiskClient.CheckAsync`: the `BrokenCircuitException` and `TimeoutRejectedException` catches; `FallbackRiskCheck` the threshold check | pausing in Risk itself trips Payment's 2 s timeout: debug Risk with `Risk:Timeout` raised in Payment's settings |
| Token checks | Payment or Gateway | ServiceDefaults `JwtAuthenticationExtensions.cs` (configuration); add `options.Events` there to break on `OnAuthenticationFailed` | `User.FindFirst("sub")` in the controllers |

**Pausing too long:** the Gateway gives up on a request after its own
timeout, and a paused Payment holds its idempotency key for 30 s
(`Payment:IdempotencyLock`). A retry during a long pause gets 409
`request_in_progress`; after 30 s, a retry takes the key over. That's the
designed behaviour (Module 4.6), not a bug.

---

## Verified (2026-10-22)

Each state was checked through the Gateway, reading `X-PayX-Instance` to see
which process answered, not just the status code:

| State | Result |
|---|---|
| All five in containers | pay 201 → capture 200 → status Captured ✅ |
| Payment container stopped, Payment run locally | the Gateway container reaches `PayX.Payment@<this PC>`, which calls the Vault and PSP containers; 201 Authorized ✅ |
| Vault container stopped, Vault run locally | the **Payment container** reaches the local Vault through `payx-host:7084`, which calls the PSP container; 201 Authorized ✅ |
| Container running, then F5 | the local service fails: "address already in use" ✅ |
| Debugger running, then `docker compose start` | **PayX:** both run (see below); every request reaches the debugger ✅ |
| Debugger stopped while the container runs | traffic returns to the container by itself ✅ |

### PayX: both can run at once (unlike SuggestX's description)

SuggestX's guide says the second of the two to start always fails to bind.
On this machine that's only true one way round:

- container first, then F5: the debugger fails to bind (Docker holds the
  port);
- **debugger first, then `docker compose start`: both start.** Docker
  Desktop listens on the wildcard address (`::`), the debugger on the
  specific loopback addresses (`127.0.0.1`, `::1`), and Windows allows both.
  The more specific listener wins, so **all traffic goes to the debugger**
  and the container sits idle. Stop the debugger and the container takes
  over.

Harmless today, but two copies of a Kafka consumer (Ledger, from Phase 7)
would both consume. Rule: stop the container before F5, and check
`X-PayX-Instance` when in doubt.

---

## Caveats

**Never kill "whatever is on port 7082".** With containers up, the process
listening there is Docker Desktop's own port forwarder
(`com.docker.backend`, and `wslrelay` for IPv6). Killing it takes down
Docker Desktop and every container (**PayX:** done by accident while
verifying this guide). To stop a stray local service, stop only processes
named `PayX.*` or `dotnet`:

```powershell
Get-NetTCPConnection -LocalPort 7082 -State Listen |
  % { Get-Process -Id $_.OwningProcess } |
  ? { $_.ProcessName -like 'PayX.*' -or $_.ProcessName -eq 'dotnet' } |
  Stop-Process -Force
```

**`dotnet run` starts a child process.** Stopping the `dotnet run` wrapper
doesn't stop the service on Windows; stop the `PayX.*` process (above).
Visual Studio's Stop button handles this itself.

**Local and container share the same data.** Postgres, Redis and LocalStack
are the same containers for both. A change made under the debugger is a
change everywhere.

**Identity's signing key is shared**: the container mounts `App/.keys`, the
same file the debugger uses, so tokens stay valid when you swap Identity.

**LocalStack keeps nothing across restarts.** Restarting it (or Docker
Desktop) creates a new KMS key and an empty card table: card tokens from
before are gone. Tokenize again.

**Smart App Control** (Windows 11) can block freshly built binaries: see
`RUNNING.md`.

**Browsing DynamoDB / S3 / KMS with a GUI (Dynobase etc.).** Such tools
validate a profile with `sts:GetCallerIdentity` first, so LocalStack runs
`sts` too. Profile in `%USERPROFILE%\.aws`: `credentials` →
`[localstack]` with `aws_access_key_id = test`, `aws_secret_access_key = test`;
`config` → `[profile localstack]` with `region = us-east-1` and
`endpoint_url = http://localhost:4568`. Region must be `us-east-1` and the
key `test` (LocalStack's default account, `000000000000`), or the tables
aren't there.

---

## How it works

**Ports.** Each container publishes the port its Visual Studio profile uses
(`7082:8080`, profile `http://localhost:7082`).

**Configuration.** `appsettings.json` holds the addresses a service needs on
the host (`localhost:5434`, `localhost:7084`…). `docker-compose.yml` sets the
same keys as environment variables with container addresses
(`Host=postgres;Port=5432`, `http://payx-host:7084/`). Environment variables
override `appsettings.json`, so one file serves both.

**The host alias.** Every service container has
`extra_hosts: payx-host:host-gateway`, and calls other PayX services
through it, never by container name, so a stopped container's port can be
taken by your debugger. Infrastructure is reached by container name
(`postgres`, `redis`, `localstack`), because it never moves.

**IPv6.** Docker Desktop publishes both an IPv4 and an IPv6 record for the
alias; .NET tries IPv6 first, which has no route to the host. The
containers set `DOTNET_SYSTEM_NET_DISABLEIPV6=1` (found in SuggestX).

**PayX: no shell in the container's entrypoint.** The obvious entrypoint,
`sh -c "exec dotnet $SERVICE_DLL"`, silently drops environment variables
whose names aren't valid shell names. The Gateway's overrides contain the
destination names `payment-1`, `identity-1`, so they never reached the
process: the containerized Gateway kept routing to `localhost` and answered
502. The image now runs the service's native launcher directly
(`/app/service`, a copy of `PayX.<Service>`), so the environment arrives
untouched.
