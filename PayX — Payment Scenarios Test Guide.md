# PayX — Payment Scenarios Test Guide

Oct 31, 2026 · @Jameel

This guide covers the nine payment scenarios you can click through in the test console, plus two that only tests reach so far. Each one gives the console steps, the API calls, the expected answer and what the code does, seen from the customer's side. Capture and status lookups are left out on purpose.

## Before you start

1. Start everything: `docker compose up -d --build` from `App/` (the first run builds five images).
2. Open the console at **http://localhost:7080/console/**. It is served by the Gateway, so every click goes through the Gateway like a real client.
3. Click **1 · Users → Create users and log in** once. It registers Priya (customer), Shoe Shop and Other Shop (merchants) and keeps their tokens.
4. To step through code, stop that service's container (`docker compose stop payment`), then press F5 on its "(local)" profile in Visual Studio. The `X-PayX-Instance` chip in the log names your PC when the debugger answered, and a container id when Docker did.
5. Restore `PayX.slnLaunch` first (`git checkout PayX.slnLaunch`) if you use the multi-project launch. A Visual Studio save cut the "all five" profile down to Payment alone.

**The one rule that shapes the order:** Vault keeps a card's CVC only until its first authorization finishes, as PCI DSS requires. Every new payment therefore needs a fresh **Tokenize**. With "Fresh approving card" selected, the console picks a new random card number after each Tokenize.

**Pausing in the debugger:** a request holds its idempotency key for 30 s. A retry during a longer pause gets 409; after 30 s a retry takes the key over. That is designed behaviour, not a bug.

## How a payment flows

A payment is two calls from the browser: **Tokenize** hands the card to Vault, then **Pay** asks Payment to authorize an amount on that token. Payment never sees the card number or CVC.

**The call chain for Pay:** Console → Gateway `:7080` (checks the JWT) → Payment `:7082` → Vault `:7084` (adds the card number and CVC) → PSP simulator `:7088` (plays the card network and the issuing bank).

&#91;embedded content: Pay call chain · 5 hops, 3 data stores\]

Each hop forwards an idempotency key; only Vault ever holds the card number and CVC.

**The payment's states.** Payment saves the row after every step, so one row always says how far the payment got:

| State | Meaning | Final? |
| --- | --- | --- |
| `Created` | Saved before talking to anyone | no |
| `RiskApproved` | Risk said yes; the bank hasn't answered yet | no |
| `RiskBlocked` | Risk said no; the bank is never called | yes |
| `Authorized` | The bank reserved the money (a hold) | yes, until capture |
| `Declined` | The bank said no | yes |

**The idempotency key chain.** Each hop forwards a key, so no retry can charge twice:

| Hop | Key | Who makes it |
| --- | --- | --- |
| Browser → Payment | `checkout-1a2b3c4d` (the console's `Idempotency-Key` box) | the client, one per checkout |
| Payment → Vault → PSP | `{paymentId}:authorize`, e.g. `pay_01J…:authorize` | Payment, fixed per payment |

Payment remembers the client's key in its `idempotency_keys` table, Vault keeps the PSP's answer per key in Redis for 24 h, and the PSP has its own key table.

## Scenario 1: Tokenize the card (needed before every payment)

Priya types her card at checkout and gets back a token. From then on only the token travels.

**In the console:** panel **2 · Card** → keep "Fresh approving card" → **Tokenize**.

**API:** `POST /api/vault/cards` → Gateway → Vault. No login and no idempotency key: storing a card moves no money.

```json
{ "number": "4242424242424242", "expMonth": 12, "expYear": 2030, "cvc": "123" }
```

**Expected:** `201 Created` with `{ "token": "tok_…", "brand": "Visa", "last4": "4242" }`. The console shows the token under the card fields.

**What the code does, from Priya's side:**

1. `CardsController.Tokenize` receives the card; the number gets a Luhn check (CardNumber, in Domain/VaultedCard.cs) and its brand is worked out.
2. `CardVault.TokenizeAsync` creates the token **first**, because the encryption is bound to it.
3. `CvcStore.HoldAsync` keeps the CVC encrypted in Redis (`vault:cvc:{token}`) for up to 10 minutes. It exists only for her first authorization.
4. `CardEncryptor.EncryptAsync` asks KMS for a fresh data key, encrypts the number with it, and stores only the encrypted number and the wrapped key in DynamoDB `payx-card-vault`. The plaintext key is wiped in a `finally`.

**Why it matters:** Payment, the Gateway and the logs never hold a card number, which keeps them out of PCI DSS scope.

**Breakpoints (Vault):** `Api/CardsController.cs` → `Tokenize`; `Services/CardVault.cs` → `TokenizeAsync`; `Services/CardEncryptor.cs` → `EncryptAsync`.

## Scenario 2: Pay, and the bank approves

Priya clicks Pay for $50.00 at Shoe Shop; the bank reserves the money and she sees "Payment authorized".

**In the console:** after Scenario 1, panel **3 · Pay** → amount `5000`, currency `USD` → **Pay**.

**API:** `POST /api/payments` with `Authorization: Bearer <Priya's token>` and `Idempotency-Key: checkout-…`.

```json
{ "merchantId": "<Shoe Shop's id>", "amount": { "amountMinor": 5000, "currency": "USD" }, "cardToken": "tok_…" }
```

Behind it, Payment calls Vault `POST /api/vault/cards/{token}/authorizations` (key `pay_…:authorize`), and Vault calls the PSP `POST /api/psp/authorizations` with the card number and CVC filled in.

**Expected:** `201 Created`, `"status": "Authorized"`, a 6-character `authorizationCode`, and a `Location: /api/payments/pay_…` header.

**What the code does, from Priya's side:**

1. **The Gateway** checks her JWT and forwards the request. Inside Payment, `[Authorize(Roles = "Customer")]` makes sure only a customer can pay. Her id comes from the token's `sub`, never from the body.
2. **`IdempotencyFilter`** (`Api/IdempotentAttribute.cs`) hashes method + path + body and calls `IdempotencyService.ClaimAsync`. The key row is inserted and committed **before** any work, so any duplicate from now on sees it.
3. **`PaymentsController.Authorize`** turns the amount into a `Money` value (a bad currency becomes a 400 here) and calls `PaymentAuthorization.AuthorizeAsync`.
4. **`ResumeOrCreateAsync`** saves the payment as `Created` and attaches its id to the key, in one transaction. If the process died after this, the row proves a payment was attempted.
5. **Risk**: `AllowAllRiskCheck` always says Allow for now (real scoring arrives in Phase 5). The payment becomes `RiskApproved` and is saved.
6. **`VaultClient.AuthorizeAsync`** asks Vault to authorize by token with the key `{paymentId}:authorize`.
7. **Vault's `CardAuthorizer`** loads the card, reads the CVC, decrypts the number, and calls the PSP with the same key. On a definitive answer it stores that answer in Redis for 24 h, **then** deletes the CVC.
8. **The PSP's `Authorizer`** locks the card's account row (`FOR UPDATE`), works out available credit ($5,000 limit minus holds and charges), asks `IssuerBank.Decide`, and records a hold.
9. Back in Payment, the status becomes `Authorized` with the code and is saved. The filter stores the 201 answer under Priya's key.

**What Priya sees:** "Payment authorized". No money has moved yet: the bank holds $50 until Shoe Shop captures it.

**Breakpoints:** Payment `IdempotentAttribute.OnActionExecutionAsync`, `IdempotencyService.ClaimAsync`, `PaymentAuthorization.AuthorizeAsync`, `VaultClient.AuthorizeAsync`; Vault `CardAuthorizer.AuthorizeAsync`; PSP `Authorizer.AuthorizeAsync`. Watch `payment.Status` after each `SaveChangesAsync`.

## Scenario 3: The bank declines

The bank says no; Priya gets a clear "card declined" with the reason, and the answer is still a 201, not an error.

**In the console:** for each test card below: choose it in **Test card** → **Tokenize** → **New key** → **Pay**.

| Card | Expected `declineCode` |
| --- | --- |
| 4000 0000 0000 0002 | `card_declined` (the bank won't say why) |
| 4000 0000 0000 9995 | `insufficient_funds` |
| 4000 0000 0000 0069 | `expired_card` |
| 4000 0000 0000 0127 | `incorrect_cvc` |

**API:** the same `POST /api/payments` as Scenario 2.

**Expected:** `201 Created`, `"status": "Declined"`, the `declineCode` above, `authorizationCode: null`.

**What the code does, from Priya's side:**

1. Everything up to the PSP is identical to Scenario 2.
2. `IssuerBank.Decide` finds the number in its test-card list and returns the scripted reason. For any other card it really checks the expiry date and the available credit, so a card can also run out after about $5,000 of holds.
3. The PSP answers **200 with `status: declined`** and records no hold. Vault stores that answer and deletes the CVC, because a decline is definitive too.
4. `PaymentAuthorization` takes the `else` branch: `payment.Decline(code)`, saved. The filter stores the 201 under Priya's key.

**Why 201 and not an error:** the request was valid and fully processed; the bank's answer was "no". An HTTP error would invite a retry, and a declined card should never be retried as is.

**Breakpoints:** PSP `Domain/IssuerBank.cs` → `Decide`; Payment `PaymentAuthorization.AuthorizeAsync`, the `else` branch.

## Scenario 4: Pay again with the same key (replay)

Priya's app retries after a lost response; she gets the same payment back and the bank is not asked again.

**In the console:** right after a finished payment (Scenarios 2 or 3), leave the **Idempotency-Key** box as it is → **Pay**.

**API:** the identical `POST /api/payments`, same key, same body.

**Expected:** the stored answer: same status code, **same payment id**, and an `Idempotent-Replayed` chip in the log. Response time drops to a few milliseconds.

**What the code does, from Priya's side:**

1. `ClaimAsync` tries to insert the key row and hits the primary key (a `UniqueViolation`).
2. It loads the existing row: same request hash, and `IsCompleted` is true.
3. The filter returns the stored status, body and `Location`, and adds `Idempotent-Replayed: true`. The controller never runs, so no new payment, no Vault call, no PSP call.

**Why it matters:** this is rule 1, "never move money twice". A phone that lost signal can safely press Pay again.

**Breakpoint:** `IdempotencyService.ClaimAsync`, the line `if (existing.IsCompleted) return … Replay`.

## Scenario 5: Priya double-clicks Pay

Two identical requests arrive at the same moment; exactly one runs, the other is told to wait, and Priya is charged once.

**In the console:** **Tokenize** a fresh card → **Double-click (new key, 2 at once)**. The button makes a new key itself and fires both requests together.

**API:** two `POST /api/payments` in parallel, same key, same body.

**Expected:** one `201 Authorized` and one `409 request_in_progress` with a `Retry-After: 1` header. Either can arrive first in the log.

**What the code does, from Priya's side:**

1. Both requests try to insert the same key row. Postgres lets exactly one insert win; the loser gets a `UniqueViolation`.
2. The loser loads the row: same hash, not completed, and its lock is still live (30 s). The `ExecuteUpdateAsync` takeover matches nothing, so the answer is `InProgress` → 409.
3. The winner runs Scenario 2 to the end and stores its answer. Clicking Pay again now (Scenario 4) replays the 201.

**Why insert first:** a "does the key exist?" check followed by an insert would let both requests see "no" and both run. The unique key makes the database the referee.

**If the race doesn't show** (both finish too fast to overlap, so you see 201 plus a replayed 201): stop the Payment container, run it in Visual Studio, put a breakpoint in `PaymentAuthorization.AuthorizeAsync`, and click Double-click. While the first request is paused, the second gets 409.

**Breakpoint:** `IdempotencyService.ClaimAsync`, the `catch` for `UniqueViolation`.

## Scenario 6: The same key for a different amount

A buggy client reuses a key for a different request; Payment refuses rather than guess which one Priya meant.

**In the console:** after any finished payment, leave the key → **Same key, +$10**. It sends the amount plus 1000 minor units.

**API:** `POST /api/payments`, same key, body with `amountMinor` 6000 instead of 5000.

**Expected:** `422`, `title: idempotency_key_reused_with_different_request`. Nothing new is created.

**What the code does, from Priya's side:**

1. The filter hashes method + path + the request body. A different amount gives a different hash.
2. `ClaimAsync` hits the existing key, compares hashes, and returns `Mismatch` before anything else is checked.

**Why refuse:** replaying the $50 answer would tell the client "$60 done" when it wasn't; running the $60 would break the promise that one key means one payment. The fix is the client's: one new key per checkout.

**Breakpoint:** `ClaimAsync`, the line `existing.RequestHash != requestHash`.

## Scenario 7: Pay without an Idempotency-Key

A money-moving call with no key is refused up front, because without one a retry could charge twice.

**In the console:** with a token and users in place → **Pay without key**.

**API:** `POST /api/payments` with no `Idempotency-Key` header.

**Expected:** `400`, `title: idempotency_key_required`. No key row, no payment.

**What the code does, from Priya's side:** the first lines of `IdempotencyFilter.OnActionExecutionAsync` read the header; empty or longer than 255 characters ends the request there. The controller never runs.

**Related 400:** pick currency **XYZ** and Pay (fresh token, new key) to see the other validation path: `Money` rejects an unknown ISO-4217 code and the controller returns a validation problem on `amount.currency`.

**Breakpoint:** `IdempotentAttribute.cs` → `OnActionExecutionAsync`, the `key.Length is 0 or > 255` check.

## Scenario 8: A new payment with a used card token

Priya tries a second purchase with the token from her first one; she is asked to re-enter her card, because the CVC is gone.

**In the console:** after an approved payment, click **New key** (keep the same token) → **Pay**.

**API:** `POST /api/payments` with a new key and the old `cardToken`.

**Expected:** `422`, `title: cvc_unavailable`, `detail: payment pay_… is RiskApproved`.

**What the code does, from Priya's side:**

1. The new key is claimed and a **new** payment is created and passes risk (`RiskApproved`).
2. Vault's `CardAuthorizer` finds no stored answer for this new key, then asks Redis for the CVC: `GetCvcAsync` returns null, because the first authorization deleted it.
3. Vault answers `409 cvc_unavailable`. Payment turns any non-5xx failure into a `422` with the same code and stores it under the key.
4. The new payment stays at `RiskApproved`. The PSP is never called, so nothing is held on her card.

**Why:** PCI DSS forbids keeping a CVC after authorization. Saved-card payments without a CVC are not supported yet.

**Breakpoint:** Vault `CardAuthorizer.AuthorizeAsync`, the `GetCvcAsync` line.

## Scenario 9: The bank is unreachable, then Priya retries

The PSP is down when Priya pays; she gets a retryable error, retries with the same key once it is back, and ends up with **one** authorized payment, the same one.

**In the console (panel 6):**

1. **Tokenize** a fresh card → **New key**.
2. In a terminal: `docker compose stop psp-simulator`.
3. **Pay** → expect `502`, `title: psp_unreachable`, `detail: payment pay_… is RiskApproved`. Note the payment id.
4. `docker compose start psp-simulator`, wait about 10 s.
5. **Pay** again **without** touching the key → expect `201 Authorized` with the **same payment id** as step 3.

**API:** the same `POST /api/payments` twice, same key, same body.

**What the code does, from Priya's side:**

1. **First try:** the payment is created and passes risk. Vault's call to the PSP throws `HttpRequestException` → `502 psp_unreachable`. Vault does **not** delete the CVC, because the bank never answered.
2. Payment returns the 502. The filter sees a 5xx, so it **does not store** it: `ReleaseAsync` frees the key. A 5xx is never an outcome.
3. **Retry:** `ClaimAsync` finds the released key and takes it over with one `ExecuteUpdateAsync` → `Resumed`.
4. `ResumeOrCreateAsync` sees the key already points to a payment and loads it instead of creating a second one. Its status is `RiskApproved`, so risk is skipped.
5. Vault is called with the same `pay_…:authorize` key, the CVC is still there, and the PSP authorizes. Payment saves `Authorized` and the 201 is stored.

**Why it matters:** the payment's state is its recovery point. A retry continues where the last attempt stopped, with the same downstream key, so the bank can never place two holds.

**Breakpoints:** Vault `CardAuthorizer`, the `catch (HttpRequestException)`; Payment `IdempotentAttribute`, the `else` → `ReleaseAsync`; on the retry, `ClaimAsync` → `ExecuteUpdateAsync` and `ResumeOrCreateAsync`. Watch `key.Record.PaymentId`.

## Not clickable yet

Two scenarios exist in the code but only unit tests reach them today.

**Risk blocks the payment.** When `IRiskCheck` answers `Block`, the payment becomes `RiskBlocked`, is saved, and the PSP is never called: no hold, nothing at the bank. Priya gets `201` with `"status": "RiskBlocked"`. The running service uses `AllowAllRiskCheck`, which always allows, so the console can't trigger it. Phase 5 replaces it with the real Risk service.

**The process dies, or the answer is lost, after the bank said yes.** Payment crashes after Vault returned "authorized" but before saving `Authorized`. On a retry with the same key:

1. Before 30 s, the retry gets `409 request_in_progress`; after 30 s the lock has lapsed and the retry takes the key over.
2. The payment is still `RiskApproved`, so Vault is called again with the same `pay_…:authorize` key.
3. Vault finds its stored answer for that key in Redis and replays it, without calling the PSP. The CVC is already gone and isn't needed.
4. Payment saves `Authorized`: one hold, one call to the bank.

To try it by hand: run Payment in Visual Studio, break on the `payment.Authorize(…)` line in `PaymentAuthorization.AuthorizeAsync`, click **Pay**, stop the debugger there, restart it, wait 30 s, and click **Pay** again with the same key.

## Verification checklist

Run top to bottom; the order matters because each payment uses up its card's CVC. Tick each row once it matches.

| # | Console steps | Expected |
| --- | --- | --- |
| 0 | Create users and log in | 3 × 201 register, 3 × 200 login |
| 1 | Tokenize | 201, `tok_…` |
| 2 | Pay | 201 `Authorized` |
| 4 | Pay (same key) | 201, same id, `Idempotent-Replayed` |
| 6 | Same key, +$10 | 422 `idempotency_key_reused_with_different_request` |
| 8 | New key → Pay (same token) | 422 `cvc_unavailable` |
| 7 | Pay without key | 400 `idempotency_key_required` |
| 5 | Tokenize → Double-click | one 201 + one 409 `request_in_progress` |
| 3 | Per decline card: Tokenize → New key → Pay | 201 `Declined` + its code (4 cards) |
| 7b | Tokenize → New key → currency XYZ → Pay | 400 on `amount.currency` |
| 9 | Tokenize → New key → stop PSP → Pay | 502 `psp_unreachable` |
| 9b | Start PSP, wait 10 s → Pay (same key) | 201 `Authorized`, same id as row 9 |

- [ ] All rows match
- [ ] Every breakpoint listed above was hit at least once under the debugger
