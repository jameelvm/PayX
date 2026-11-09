using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayX.Contracts.Events;
using PayX.Payment.Domain;

namespace PayX.Payment.Repositories;

/// <summary>PayX's own payment records, in <c>payx_payments</c>.</summary>
public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public DbSet<Domain.Payment> Payments => Set<Domain.Payment>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();


    /// <summary>
    /// The transactional outbox (6.1, DESIGN decision 5). Every status change
    /// made on a tracked payment becomes an outbox row *in this same save*:
    /// EF Core wraps one SaveChanges in one database transaction, so the new
    /// status and the event announcing it commit together or not at all.
    ///
    /// The alternatives each lose or invent events on a crash:
    /// publish-then-commit announces a change that may never be saved;
    /// commit-then-publish loses the announcement if the process dies in
    /// between. No code path can forget to write the event, because no code
    /// path writes it: the context does, for every move.
    /// </summary>
    public override async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        var changed = ChangeTracker.Entries<Domain.Payment>()
            .Select(e => e.Entity).Where(p => p.PendingChanges.Count > 0).ToList();

        foreach (var payment in changed)
        foreach (var change in payment.PendingChanges)
        {
            var status = change.Status.ToString();
            var evt = new PaymentEvent(Guid.CreateVersion7(), PaymentEvent.TypeFor(status), payment.Id,
                change.Sequence, status, payment.MerchantId, payment.CustomerId, payment.Amount,
                payment.CapturedAmountMinor, payment.PspAuthorizationId, change.At);
            OutboxMessages.Add(new OutboxMessage(evt.EventId, Topics.Payments, payment.Id, evt.EventType,
                JsonSerializer.Serialize(evt, PaymentEvent.Json), change.At));
        }

        var written = await base.SaveChangesAsync(ct);
        foreach (var payment in changed)
            payment.ClearPendingChanges();   // only once they're safely in the outbox
        return written;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IdempotencyRecord>(record =>
        {
            record.ToTable("idempotency_keys");
            // The primary key is the de-duplication: two requests can't both
            // insert the same (caller, key), however close together they arrive.
            record.HasKey(r => new { r.Scope, r.Key });
            record.Property(r => r.Scope).HasMaxLength(64);
            record.Property(r => r.Key).HasMaxLength(255);
            record.Property(r => r.RequestHash).HasMaxLength(64);
            // text, not jsonb: a replay must be byte-for-byte the first answer (3.4).
            record.Property(r => r.ResponseBody).HasColumnType("text");
            record.Property(r => r.ResponseLocation).HasMaxLength(200);
            record.Property(r => r.PaymentId).HasMaxLength(40);
        });

        modelBuilder.Entity<Domain.Payment>(payment =>
        {
            payment.ToTable("payments", t =>
            {
                // The database refuses what the code should never write, in
                // case some code path (or a hand-written UPDATE) forgets. The
                // status list is generated from the enum, so adding a state is
                // a migration that widens this constraint.
                t.HasCheckConstraint("ck_payments_status", $"status IN ({string.Join(", ",
                    Enum.GetNames<PaymentStatus>().Select(s => $"'{s}'"))})");
                t.HasCheckConstraint("ck_payments_amount_positive", "amount_minor > 0");
                t.HasCheckConstraint("ck_payments_captured_within_amount",
                    "captured_amount_minor IS NULL OR (captured_amount_minor > 0 AND captured_amount_minor <= amount_minor)");
                // A payment the PSP authorized must say which authorization:
                // without it, capture, void and reconciliation have nothing to quote.
                t.HasCheckConstraint("ck_payments_psp_reference",
                    "status NOT IN ('Authorized', 'Captured', 'Voided') OR psp_authorization_id IS NOT NULL");
                t.HasCheckConstraint("ck_payments_captured_amount",
                    "(status = 'Captured') = (captured_amount_minor IS NOT NULL)");
                t.HasCheckConstraint("ck_payments_risk_score", "risk_score IS NULL OR risk_score BETWEEN 0 AND 100");
            });

            payment.HasKey(p => p.Id);
            payment.Property(p => p.Id).HasMaxLength(40);
            payment.Property(p => p.Version).IsRowVersion();   // → xmin, no extra column

            // Money is one value in code, two columns in the table.
            payment.ComplexProperty(p => p.Amount, amount =>
            {
                amount.Property(m => m.AmountMinor).HasColumnName("amount_minor");
                amount.Property(m => m.Currency).HasColumnName("currency").HasMaxLength(3);
            });

            payment.Property(p => p.CardToken).HasMaxLength(64);
            payment.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
            payment.Property(p => p.PspAuthorizationId).HasMaxLength(40);
            payment.Property(p => p.AuthorizationCode).HasMaxLength(6);
            payment.Property(p => p.DeclineCode).HasMaxLength(32);
            payment.Property(p => p.RiskReasons).HasMaxLength(200);
            payment.Ignore(p => p.PendingChanges);
        });

        modelBuilder.Entity<OutboxMessage>(outbox =>
        {
            outbox.ToTable("outbox_messages");
            outbox.HasKey(o => o.Id);
            outbox.Property(o => o.Id).UseIdentityAlwaysColumn();
            outbox.HasIndex(o => o.EventId).IsUnique();
            outbox.Property(o => o.Topic).HasMaxLength(100);
            outbox.Property(o => o.Key).HasMaxLength(40);
            outbox.Property(o => o.EventType).HasMaxLength(50);
            outbox.Property(o => o.Payload).HasColumnType("jsonb");
            // What the relay asks for, over and over: "unpublished, oldest
            // first". A partial index holds only those rows, so it stays tiny
            // however many published rows pile up behind it.
            outbox.HasIndex(o => o.Id).HasFilter("published_at IS NULL").HasDatabaseName("ix_outbox_unpublished");
        });
    }
}
