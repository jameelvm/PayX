using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.EntityFrameworkCore;
using PayX.PspSimulator.Domain;
using PayX.PspSimulator.Repositories;

namespace PayX.PspSimulator.Services;

/// <summary>
/// One settlement run (8.1; doc ch.2, reconciliation: "the PSP sends a
/// settlement file at the end of the day"). Two steps, see
/// <see cref="SettlementBatch"/>: close a batch in the DB, then upload its
/// file to S3.
/// </summary>
public sealed class Settler(PspDbContext db, IAmazonS3 s3, IConfiguration config, ILogger<Settler> logger)
{
    // Any fixed number works; it only has to differ from other advisory
    // locks in this database. (Payment's outbox uses 62001 in its own DB.)
    private const long SettlementLockId = 81001;

    private string Bucket => config["Settlement:Bucket"] ?? "payx-settlement-files";
    private int FeeBasisPoints => config.GetValue("Settlement:FeeBasisPoints", 290);
    private long FeeFixedMinor => config.GetValue("Settlement:FeeFixedMinor", 30L);

    /// <summary>Close today's batch (if anything is waiting), then upload every file not yet in S3.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        await CloseBatchAsync(DateTimeOffset.UtcNow, ct);
        await UploadPendingAsync(ct);
    }

    /// <summary>
    /// Step 1. Puts every captured, not-yet-settled charge up to
    /// <paramref name="cutoff"/> into a new batch. Returns null when there
    /// was nothing to settle (no empty files).
    /// </summary>
    public async Task<SettlementBatch?> CloseBatchAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // One settlement run at a time, even with two PSP instances running:
        // the second waits here until the first commits. Without it both
        // could create a batch at once; the UPDATE below would still give
        // each charge to only one of them, but the batches would be messy.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({SettlementLockId})", ct);

        var batch = new SettlementBatch(cutoff);
        db.SettlementBatches.Add(batch);
        await db.SaveChangesAsync(ct);

        // One UPDATE stamps every waiting charge with this batch id. It is a
        // single statement, so a charge is either in this batch or not,
        // never half. "settlement_batch_id IS NULL" is what stops a charge
        // being paid out twice: once stamped, no later run picks it up.
        var count = await db.Authorizations
            .Where(a => a.Status == AuthorizationStatus.Captured
                        && a.SettlementBatchId == null
                        && a.CapturedAt <= cutoff)
            .ExecuteUpdateAsync(set => set.SetProperty(a => a.SettlementBatchId, batch.Id), ct);

        if (count == 0)
        {
            await tx.RollbackAsync(ct);   // nothing to pay out: no batch, no file
            db.Entry(batch).State = EntityState.Detached;
            return null;
        }

        batch.SetLineCount(count);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("Settlement batch {BatchId} closed with {Count} charge(s)", batch.Id, count);
        return batch;
    }

    /// <summary>
    /// Step 2. Uploads the file of every batch not yet in S3. Safe to repeat:
    /// the same batch always produces the same file at the same key, so an
    /// upload that already happened just overwrites itself with identical bytes.
    /// </summary>
    public async Task<int> UploadPendingAsync(CancellationToken ct)
    {
        var pending = await db.SettlementBatches
            .Where(b => b.UploadedAt == null)
            .OrderBy(b => b.CreatedAt)
            .ToListAsync(ct);

        foreach (var batch in pending)
        {
            var charges = await db.Authorizations.AsNoTracking()
                .Where(a => a.SettlementBatchId == batch.Id)
                .OrderBy(a => a.CapturedAt).ThenBy(a => a.Id)   // fixed order → identical file every time
                .ToListAsync(ct);

            var body = SettlementFile.Build(batch, charges, FeeBasisPoints, FeeFixedMinor);
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = Bucket,
                Key = batch.ObjectKey,
                ContentBody = body,
                ContentType = "text/csv",
            }, ct);

            // Only now: if we crash before this line, the batch is uploaded again next run.
            batch.MarkUploaded();
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Settlement file uploaded: s3://{Bucket}/{Key} ({Bytes} bytes)",
                Bucket, batch.ObjectKey, Encoding.UTF8.GetByteCount(body));
        }
        return pending.Count;
    }
}
