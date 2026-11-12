using Amazon.Runtime;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PayX.PspSimulator.Domain;
using PayX.PspSimulator.Repositories;
using PayX.PspSimulator.Services;

namespace PayX.PspSimulator.Tests;

// Module 8.1: the PSP's end-of-day settlement file.
// Uses the real payx_psp database and LocalStack's S3
// (docker compose up -d postgres localstack). Each test uses its own
// merchant id, so it only looks at its own charges.
public sealed class SettlementTests
{
    private const string Connection = "Host=localhost;Port=5434;Database=payx_psp;Username=payx_psp;Password=payx_psp_dev";
    private const string Bucket = "payx-settlement-files";

    private static readonly Lazy<bool> Migrated = new(() =>
    {
        using var db = NewContext();
        db.Database.Migrate();
        return true;
    });

    private static PspDbContext NewContext() =>
        new(new DbContextOptionsBuilder<PspDbContext>().UseNpgsql(Connection).UseSnakeCaseNamingConvention().Options);

    private static PspDbContext Db()
    {
        _ = Migrated.Value;
        return NewContext();
    }

    private static readonly IAmazonS3 S3 = new AmazonS3Client(new BasicAWSCredentials("test", "test"),
        new AmazonS3Config { ServiceURL = "http://localhost:4568", ForcePathStyle = true });

    private static Settler NewSettler(PspDbContext db) =>
        new(db, S3, new ConfigurationBuilder().Build(), NullLogger<Settler>.Instance);

    /// <summary>A card + one authorization for this merchant; captured for <paramref name="capture"/> cents if given.</summary>
    private static async Task<Authorization> NewAuthorizationAsync(PspDbContext db, string merchant, long amount, long? capture)
    {
        var card = new CardAccount(Guid.NewGuid().ToString("N"), 1_000_000);
        var auth = new Authorization(card.Fingerprint, "4242", "Visa", merchant, amount, "USD",
            declineCode: null, authorizationCode: "ABC123", reference: Guid.NewGuid().ToString("N"));
        if (capture is { } c)
            Assert.Null(auth.Capture(c));
        db.CardAccounts.Add(card);
        db.Authorizations.Add(auth);
        await db.SaveChangesAsync();
        return auth;
    }

    // ---- Fee and file format (no database) -------------------------------

    [Theory]
    [InlineData(10_000, 320)]   // $100.00 → 2.9% = 290 + 30 = $3.20
    [InlineData(500, 45)]       // 2.9% of 500 = 14.5 → rounds half up to 15, + 30
    [InlineData(1_069, 61)]     // 31.001 → 31, + 30
    public void Fee_is_percentage_plus_fixed_rounded_half_up(long gross, long expectedFee) =>
        Assert.Equal(expectedFee, SettlementFile.Fee(gross, basisPoints: 290, fixedMinor: 30));

    [Fact]
    public void File_has_a_header_and_one_line_per_charge_with_net_equal_gross_minus_fee()
    {
        var auth = new Authorization("fp", "4242", "Visa", "m_1", 10_000, "USD", null, "ABC123", reference: "pay_1");
        auth.Capture(10_000);
        var batch = new SettlementBatch(DateTimeOffset.UtcNow);

        var lines = SettlementFile.Build(batch, [auth], 290, 30).TrimEnd('\n').Split('\n');

        Assert.Equal(SettlementFile.Header, lines[0]);
        var cells = lines[1].Split(',');
        Assert.Equal([batch.Id, auth.Id, "pay_1", "m_1", "capture", "10000", "320", "9680", "USD"],
            [cells[0], cells[2], cells[3], cells[4], cells[5], cells[6], cells[7], cells[8], cells[9]]);
    }

    // ---- Step 1: closing a batch -----------------------------------------

    [Fact]
    public async Task Batch_takes_captured_charges_only_and_never_takes_one_twice()
    {
        var merchant = $"m_{Guid.NewGuid():N}";
        await using var db = Db();
        var captured = await NewAuthorizationAsync(db, merchant, 5_000, capture: 4_000);
        var heldOnly = await NewAuthorizationAsync(db, merchant, 7_000, capture: null);   // authorized, never captured

        var first = await NewSettler(db).CloseBatchAsync(DateTimeOffset.UtcNow, default);
        var second = await NewSettler(db).CloseBatchAsync(DateTimeOffset.UtcNow, default);

        await using var check = Db();
        var mine = await check.Authorizations.Where(a => a.MerchantId == merchant).ToDictionaryAsync(a => a.Id);
        Assert.NotNull(first);
        Assert.Equal(first.Id, mine[captured.Id].SettlementBatchId);   // paid out in the first batch
        Assert.Null(mine[heldOnly.Id].SettlementBatchId);              // a hold is not money yet
        // The second run found this charge already stamped: no second payout.
        Assert.True(second is null || second.Id != mine[captured.Id].SettlementBatchId);
    }

    [Fact]
    public async Task Charges_captured_after_the_cutoff_wait_for_the_next_batch()
    {
        var merchant = $"m_{Guid.NewGuid():N}";
        await using var db = Db();
        var cutoff = DateTimeOffset.UtcNow;
        await Task.Delay(20);
        var late = await NewAuthorizationAsync(db, merchant, 1_000, capture: 1_000);

        await NewSettler(db).CloseBatchAsync(cutoff, default);

        await using var check = Db();
        Assert.Null((await check.Authorizations.SingleAsync(a => a.Id == late.Id)).SettlementBatchId);
    }

    // ---- Step 2: the file in S3 ------------------------------------------

    [Fact]
    public async Task Upload_puts_the_file_in_S3_and_marks_the_batch_uploaded_once()
    {
        var merchant = $"m_{Guid.NewGuid():N}";
        await using var db = Db();
        var charge = await NewAuthorizationAsync(db, merchant, 10_000, capture: 10_000);
        var settler = NewSettler(db);
        var batch = await settler.CloseBatchAsync(DateTimeOffset.UtcNow, default);
        Assert.NotNull(batch);

        Assert.True(await settler.UploadPendingAsync(default) >= 1);
        Assert.Equal(0, await settler.UploadPendingAsync(default));     // nothing left to upload

        using var file = await S3.GetObjectAsync(Bucket, batch.ObjectKey);
        var body = await new StreamReader(file.ResponseStream).ReadToEndAsync();
        Assert.Contains($",{charge.Id},{charge.Reference},{merchant},capture,10000,320,9680,USD,", body);

        await using var check = Db();
        Assert.NotNull((await check.SettlementBatches.SingleAsync(b => b.Id == batch.Id)).UploadedAt);
    }
}
