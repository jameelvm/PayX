using Microsoft.EntityFrameworkCore;
using PayX.Payment.Domain;

namespace PayX.Payment.Repositories;

/// <summary>PayX's own payment records, in <c>payx_payments</c>.</summary>
public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public DbSet<Domain.Payment> Payments => Set<Domain.Payment>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

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
        });
    }
}
