using Microsoft.EntityFrameworkCore;
using PayX.PspSimulator.Domain;

namespace PayX.PspSimulator.Repositories;

/// <summary>
/// The provider's own records, in <c>payx_psp</c>. These are the "other side"
/// that reconciliation compares PayX's ledger against in Phase 8.
/// </summary>
public sealed class PspDbContext(DbContextOptions<PspDbContext> options) : DbContext(options)
{
    public DbSet<CardAccount> CardAccounts => Set<CardAccount>();
    public DbSet<Authorization> Authorizations => Set<Authorization>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IdempotencyRecord>(record =>
        {
            record.ToTable("idempotency_keys");
            // The primary key *is* the de-duplication: two requests can't
            // both insert the same key, however close together they arrive.
            record.HasKey(r => r.Key);
            record.Property(r => r.Key).HasMaxLength(255);
            record.Property(r => r.RequestHash).HasMaxLength(64);
            // text, not jsonb: jsonb re-sorts keys and re-spaces the JSON, so a
            // replay would carry the same data in different bytes (seen in
            // testing). A replay must be exactly what was sent the first time.
            record.Property(r => r.ResponseBody).HasColumnType("text");
        });

        modelBuilder.Entity<CardAccount>(account =>
        {
            account.ToTable("card_accounts");
            account.HasKey(a => a.Fingerprint);
            account.Property(a => a.Fingerprint).HasMaxLength(64);
        });

        modelBuilder.Entity<Authorization>(auth =>
        {
            auth.ToTable("authorizations", t =>
            {
                t.HasCheckConstraint("ck_authorizations_amount_positive", "amount_minor > 0");
                // Never charge more than was authorized, even if code forgets to check.
                t.HasCheckConstraint("ck_authorizations_captured_within_authorized",
                    "captured_amount_minor IS NULL OR (captured_amount_minor > 0 AND captured_amount_minor <= amount_minor)");
            });
            // uint + IsRowVersion → Npgsql maps it to the system column xmin:
            // no extra column, and Postgres updates it on every write.
            auth.Property(a => a.Version).IsRowVersion();
            auth.HasKey(a => a.Id);
            auth.Property(a => a.Id).HasMaxLength(40);
            auth.Property(a => a.CardFingerprint).HasMaxLength(64);
            auth.Property(a => a.Last4).HasMaxLength(4);
            auth.Property(a => a.Brand).HasMaxLength(16);
            auth.Property(a => a.MerchantId).HasMaxLength(64);
            auth.Property(a => a.Currency).HasMaxLength(3);
            auth.Property(a => a.Status).HasConversion<string>().HasMaxLength(16);
            auth.Property(a => a.AuthorizationCode).HasMaxLength(6);
            auth.Property(a => a.DeclineCode).HasMaxLength(32);

            auth.HasOne<CardAccount>().WithMany().HasForeignKey(a => a.CardFingerprint);
            // "Sum this card's live holds" runs on every authorization.
            auth.HasIndex(a => new { a.CardFingerprint, a.Status });
        });
    }
}
