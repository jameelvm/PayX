using Microsoft.EntityFrameworkCore;
using PayX.Ledger.Domain;

namespace PayX.Ledger.Repositories;

/// <summary>
/// The books (payx_ledger). Doc ch.2, "Wallet and ledger"; DESIGN decisions 9
/// and 10. Every rule that matters is enforced by the database as well as by
/// the code: a ledger is exactly the place where "the code always does it
/// right" is not good enough.
/// </summary>
public sealed class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<Posting> Postings => Set<Posting>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();

    /// <summary>One booking per payment step (7.4): see the partial unique index below.</summary>
    public const string OneEntryPerStep = "ix_journal_entries_one_per_step";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(account =>
        {
            account.ToTable("accounts", t =>
                t.HasCheckConstraint("ck_accounts_type", $"type IN ({string.Join(", ",
                    Enum.GetNames<AccountType>().Select(s => $"'{s}'"))})"));
            account.HasKey(a => a.Id);
            account.Property(a => a.Id).UseIdentityAlwaysColumn();
            account.Property(a => a.Type).HasConversion<string>().HasMaxLength(24);
            account.Property(a => a.Currency).HasMaxLength(3).IsFixedLength();
            account.Property(a => a.CreatedAt).HasDefaultValueSql("now()");
            // One account per (type, owner, currency): looked up by this, and
            // two consumers creating the same account at once can't make two.
            account.HasIndex(a => new { a.Type, a.OwnerId, a.Currency }).IsUnique();
            // (id, currency) is unique because id is; declared so postings can
            // point at it and the database checks the posting's currency.
            account.HasAlternateKey(a => new { a.Id, a.Currency });
            account.Ignore(a => a.Key);
        });

        modelBuilder.Entity<JournalEntry>(entry =>
        {
            entry.ToTable("journal_entries", t =>
            {
                t.HasCheckConstraint("ck_journal_entries_kind", $"kind IN ({string.Join(", ",
                    Enum.GetNames<EntryKind>().Select(s => $"'{s}'"))})");
                t.HasCheckConstraint("ck_journal_entries_amount_positive", "amount_minor > 0");
            });
            entry.HasKey(e => e.Id);
            entry.Property(e => e.Id).UseIdentityAlwaysColumn();
            entry.Property(e => e.Kind).HasConversion<string>().HasMaxLength(16);
            entry.Property(e => e.PaymentId).HasMaxLength(40);
            entry.ComplexProperty(e => e.Amount, amount =>
            {
                amount.Property(m => m.AmountMinor).HasColumnName("amount_minor");
                amount.Property(m => m.Currency).HasColumnName("currency").HasMaxLength(3).IsFixedLength();
            });
            entry.HasIndex(e => e.PaymentId);
            // Second line of dedupe (7.4), on the business key rather than the
            // event id: a payment is authorized, captured or voided at most
            // once, so even a replay carrying a new event id can't book the
            // same step twice. Partial: refunds (Phase 9) may repeat.
            entry.HasIndex(e => new { e.PaymentId, e.Kind }).IsUnique()
                .HasFilter("kind IN ('Authorization', 'Capture', 'Void')").HasDatabaseName(OneEntryPerStep);

            // Restrict, not EF's default cascade: nothing in a ledger is ever deleted.
            entry.HasMany(e => e.Postings).WithOne().HasForeignKey(p => p.EntryId).OnDelete(DeleteBehavior.Restrict);
            entry.Navigation(e => e.Postings).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<Posting>(posting =>
        {
            posting.ToTable("postings", t => t.HasCheckConstraint("ck_postings_amount_nonzero", "amount_minor <> 0"));
            posting.HasKey(p => p.Id);
            posting.Property(p => p.Id).UseIdentityAlwaysColumn();
            posting.Property(p => p.Currency).HasMaxLength(3).IsFixedLength();
            // The posting's currency must be its account's: a USD amount can
            // never land in a EUR account, whatever the code does.
            posting.HasOne<Account>().WithMany()
                .HasForeignKey(p => new { p.AccountId, p.Currency })
                .HasPrincipalKey(a => new { a.Id, a.Currency })
                .OnDelete(DeleteBehavior.Restrict);
            posting.HasIndex(p => new { p.AccountId, p.Id });   // an account's movements, in order
        });

        modelBuilder.Entity<ProcessedEvent>(processed =>
        {
            processed.ToTable("processed_events");
            // The primary key is the dedupe: a second insert of the same
            // event id fails, whichever transaction gets there second.
            processed.HasKey(p => p.EventId);
            processed.Property(p => p.EventId).ValueGeneratedNever();
            processed.Property(p => p.PaymentId).HasMaxLength(40);
            processed.Property(p => p.ProcessedAt).HasDefaultValueSql("now()");
            processed.HasOne(p => p.Entry).WithMany().HasForeignKey(p => p.EntryId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
