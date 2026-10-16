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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CardAccount>(account =>
        {
            account.ToTable("card_accounts");
            account.HasKey(a => a.Fingerprint);
            account.Property(a => a.Fingerprint).HasMaxLength(64);
        });

        modelBuilder.Entity<Authorization>(auth =>
        {
            auth.ToTable("authorizations", t =>
                t.HasCheckConstraint("ck_authorizations_amount_positive", "amount_minor > 0"));
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
