namespace PayX.PspSimulator.Domain;

/// <summary>
/// One settlement run: "these charges are paid out now" (doc ch.2,
/// reconciliation: the PSP's end-of-day settlement file).
///
/// In plain words: during the day, captures only say "charge this card".
/// At the end of the day the PSP takes every capture not yet paid out, puts
/// them in one batch, and sends the merchant one file listing them, with
/// the fee it kept for each. This class is that batch.
///
/// Two steps, in this order:
///   1. Close the batch: in one DB transaction, create this row and stamp
///      every included charge with its id. Each charge lands in exactly
///      one batch, so it is never paid out twice.
///   2. Upload the file to S3, then set <see cref="UploadedAt"/>.
/// S3 is not part of the DB transaction, so step 2 can fail after step 1
/// committed. That is fine: a batch with no UploadedAt is simply uploaded
/// again on the next run, with the same content, to the same S3 key. This
/// is the outbox idea from Module 6.1 again: save the decision first, then
/// do the outside work, and retry it until it is done.
/// </summary>
public sealed class SettlementBatch
{
    public string Id { get; private set; } = null!;              // "stl_…"

    /// <summary>The day this batch belongs to (UTC). Several runs per day are possible locally.</summary>
    public DateOnly BusinessDate { get; private set; }

    /// <summary>Charges captured up to this moment are included.</summary>
    public DateTimeOffset CutoffAt { get; private set; }

    /// <summary>Where the file goes in S3: <c>settlements/2026-11-11/stl_….csv</c>.</summary>
    public string ObjectKey { get; private set; } = null!;

    public int LineCount { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>null until the file is safely in S3.</summary>
    public DateTimeOffset? UploadedAt { get; private set; }

    public SettlementBatch(DateTimeOffset cutoffAt)
    {
        Id = $"stl_{Guid.CreateVersion7():N}";
        CutoffAt = cutoffAt;
        BusinessDate = DateOnly.FromDateTime(cutoffAt.UtcDateTime);
        ObjectKey = $"settlements/{BusinessDate:yyyy-MM-dd}/{Id}.csv";
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public void SetLineCount(int count) => LineCount = count;

    public void MarkUploaded() => UploadedAt = DateTimeOffset.UtcNow;

    private SettlementBatch() { }   // EF Core
}
