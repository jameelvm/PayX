using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using PayX.Contracts;
using PayX.Contracts.Events;
using PayX.Payment.Domain;
using PayX.Payment.Repositories;

namespace PayX.Payment.Tests;

// The transactional outbox (6.1) against the real payx_payments database:
// every status change becomes an event row in the same save, and a save that
// fails writes neither.
public sealed class OutboxTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public OutboxTests()
    {
        using var db = NewContext();
        db.Database.Migrate();
    }

    [Fact]
    public async Task Every_status_change_is_written_as_an_event_in_order()
    {
        var payment = new Domain.Payment(Guid.NewGuid(), Guid.NewGuid(), new Money(5_000, "USD"), "tok_test");
        await using (var db = NewContext())
        {
            db.Payments.Add(payment);
            await db.SaveChangesAsync();                 // created: no status change yet, no event
            payment.ApproveRisk();
            await db.SaveChangesAsync();
            payment.Authorize("auth_1", "A1B2C3");
            await db.SaveChangesAsync();
            payment.Capture(new Money(3_000, "USD"));
            await db.SaveChangesAsync();
        }

        var rows = await Outbox(payment.Id);

        Assert.Equal(["payment.riskapproved", "payment.authorized", "payment.captured"], rows.Select(r => r.EventType));
        Assert.All(rows, r => Assert.Equal((Topics.Payments, payment.Id, (DateTimeOffset?)null), (r.Topic, r.Key, r.PublishedAt)));

        var events = rows.Select(r => JsonSerializer.Deserialize<PaymentEvent>(r.Payload, Json)!).ToList();
        Assert.Equal([1, 2, 3], events.Select(e => e.Sequence));
        Assert.Equal(rows.Select(r => r.EventId), events.Select(e => e.EventId));
        var captured = events[2];
        Assert.Equal(("Captured", 3_000L, "auth_1", 5_000L, "USD"),
            (captured.Status, captured.CapturedAmountMinor, captured.PspAuthorizationId, captured.Amount.AmountMinor, captured.Amount.Currency));
    }

    [Fact]
    public async Task A_save_that_fails_writes_neither_the_change_nor_its_event()
    {
        var payment = new Domain.Payment(Guid.NewGuid(), Guid.NewGuid(), new Money(5_000, "USD"), "tok_test");
        await using (var db = NewContext())
        {
            db.Payments.Add(payment);
            await db.SaveChangesAsync();

            // A move plus a value the database refuses (risk_score must be 0-100):
            // the whole save is one transaction, so the move is refused too.
            payment.RecordRiskAssessment(150, ["broken"]);
            payment.ApproveRisk();
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        await using var check = NewContext();
        Assert.Equal(PaymentStatus.Created, (await check.Payments.SingleAsync(p => p.Id == payment.Id)).Status);
        Assert.Empty(await Outbox(payment.Id));
    }

    [Fact]
    public async Task Events_are_written_once_even_when_the_payment_is_saved_again()
    {
        var payment = new Domain.Payment(Guid.NewGuid(), Guid.NewGuid(), new Money(5_000, "USD"), "tok_test");
        await using (var db = NewContext())
        {
            db.Payments.Add(payment);
            payment.ApproveRisk();
            await db.SaveChangesAsync();
            await db.SaveChangesAsync();                 // nothing new: no second event
        }

        Assert.Single(await Outbox(payment.Id));
    }

    private static async Task<List<OutboxMessage>> Outbox(string paymentId)
    {
        await using var db = NewContext();
        return await db.OutboxMessages.Where(o => o.Key == paymentId).OrderBy(o => o.Id).ToListAsync();
    }

    private static PaymentsDbContext NewContext() => new(new DbContextOptionsBuilder<PaymentsDbContext>()
        .UseNpgsql("Host=localhost;Port=5434;Database=payx_payments;Username=payx_payments;Password=payx_payments_dev")
        .UseSnakeCaseNamingConvention()
        .Options);
}
