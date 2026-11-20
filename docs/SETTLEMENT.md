# Settlement and Reconciliation (Phase 8)

This document grows one section per module. Right now it covers **8.1**.

---

## The big picture in one minute

We follow one example all the way through: **a customer pays a shop $100.**

| Step | What happens | Has money moved? |
|---|---|---|
| 1. Authorize | The bank puts a **hold** of $100 on the customer's card. | No. It is only reserved. |
| 2. Capture | The shop says "charge it now". | No. It is only an instruction. |
| 3. **Settle** (8.1) | At the end of the day the PSP pays the shop. It keeps a fee. | **Yes.** The shop gets $96.80. |
| 4. **Reconcile** (8.2 to 8.4) | We check that our ledger and the PSP's list agree. | No. It is a check. |

Two new words:

- **Settlement** means the money actually moves. The PSP pays the shop and
  sends a list of everything it paid. That list is the **settlement file**.
- **Reconciliation** means comparing two records of the same thing. Our
  ledger says what *we* think happened. The settlement file says what *the
  PSP* thinks happened. If they disagree, someone has made a mistake.

(The **PSP**, payment service provider, is the company that talks to the
card networks and banks for us, like Stripe or Adyen. In PayX the PSP
simulator plays this part.)

---

## Module 8.1: the PSP writes the settlement file

### What it does

Every "day", the PSP simulator:

1. Finds every charge that was **captured** but **not paid out yet**.
2. Puts them into one **batch** (one payout run).
3. Works out its fee for each charge.
4. Writes the list as a CSV file into S3.

"Every day" is **2 minutes** on your machine, so you can watch it happen.
A real PSP does it once a day.

### The fee

The PSP keeps **2.9% + 30¢** of every charge. That is typical card pricing.

For our $100 payment:

```
gross = 10000 cents            what the customer paid
fee   = 290 + 30 = 320 cents   2.9% of 10000 is 290, plus the fixed 30
net   = 10000 - 320 = 9680     what the shop actually receives ($96.80)
```

All amounts are whole cents, never decimals. If a percentage gives half a
cent, it rounds up (14.5 becomes 15).

### The file

It goes to S3 bucket `payx-settlement-files`, at
`settlements/<date>/<batch id>.csv`. One line per charge:

```
batch_id,business_date,psp_reference,merchant_reference,merchant_id,type,gross_minor,fee_minor,net_minor,currency,captured_at
stl_01a1…,2026-11-12,auth_01a1…,pay_01a1…,01a1cdec-…,capture,10000,320,9680,USD,2026-11-12T04:02:42.955Z
```

| Column | Meaning |
|---|---|
| `psp_reference` | The PSP's id for this charge. Our ledger stores it too, so 8.3 can match on it. |
| `merchant_reference` | Our payment id (`pay_…`). |
| `gross / fee / net` | Paid by the customer / kept by the PSP / received by the shop. |

### Two rules that keep it safe

**Rule 1: a charge is never paid out twice.**

When a charge goes into a batch, it is stamped with that batch's id. A
later run only picks charges with **no** stamp. So once a charge has been
paid out, no later run can pay it again.

The stamping is one database statement. A charge is either fully in the
batch or not in it at all, never halfway.

**Rule 2: a file is never lost.**

There are two separate steps:

1. Save the batch in the database.
2. Upload the file to S3.

The program could crash between them. So the batch has an `uploaded_at`
field that is filled in **only after S3 says OK**. If the program crashes
before that, the next run sees "not uploaded yet" and uploads it again.
The same batch always produces exactly the same file, so uploading twice
does no harm.

This is the same idea as the outbox from Phase 6: **save the decision
first, then do the outside work, and keep retrying until it is done.**

### Where the code is

All in `src/services/PayX.PspSimulator/`:

| File | What it does |
|---|---|
| `Domain/SettlementBatch.cs` | One payout run: id, date, S3 key, uploaded or not. |
| `Domain/Authorization.cs` | Each charge now has a `SettlementBatchId` (empty = not paid out yet). |
| `Services/Settler.cs` | Step 1 (close the batch) and step 2 (upload the file). |
| `Services/SettlementFile.cs` | The CSV format and the fee calculation. |
| `Jobs/SettlementJob.cs` | Runs the settler every 2 minutes. |

Settings are in `appsettings.json`, section `Settlement`: how often it runs,
the bucket name and the fee.

### Try it yourself

1. Pay and capture a payment (see `RUNNING.md`, "Paying end to end").
2. Wait up to 2 minutes. The PSP log says:
   `Settlement file uploaded: s3://payx-settlement-files/settlements/…`
3. List and read the files:

```bash
docker exec payx-localstack awslocal s3 ls s3://payx-settlement-files/settlements/ --recursive
docker exec payx-localstack awslocal s3 cp s3://payx-settlement-files/<key> -
```

### Tests

`tests/PayX.PspSimulator.Tests/SettlementTests.cs`, 7 tests:

- the fee maths, including rounding
- the file has a header and the right numbers
- a batch takes captured charges only (a hold is not money yet)
- a charge is never put in two batches
- a charge captured after the cutoff waits for the next batch
- the file really lands in S3, and is uploaded only once

### Questions you might be asked

**Why not upload the file in the same transaction as the database?**
S3 is not part of the database, so one transaction can't cover both. We save
first and upload after, and make the upload safe to repeat.

**What if two copies of the PSP run at the same time?**
A database lock (`pg_advisory_xact_lock`) lets only one settlement run at a
time. Even without it, the "no stamp yet" check means a charge can only go
into one batch.

**Why does a hold not appear in the file?**
A hold only reserves money. Nothing is paid until the shop captures it.
