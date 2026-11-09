using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
using PayX.Contracts;
using PayX.Contracts.Events;
using PayX.Ledger.Jobs;
using PayX.Ledger.Repositories;
using PayX.Ledger.Services;

namespace PayX.Ledger.Tests;

// Builds payment events exactly as Payment's outbox writes them (same record,
// same serializer options), wrapped as Kafka would deliver them.
internal sealed class Events(Guid customer, Guid merchant, string paymentId)
{
    private int _sequence;

    public static Events ForNewPayment() => new(Guid.NewGuid(), Guid.NewGuid(), $"pay_{Guid.NewGuid():N}");

    public Guid Customer => customer;
    public Guid Merchant => merchant;
    public string PaymentId => paymentId;

    public PaymentEvent Event(string status, long amount = 5_000, long? captured = null, string currency = "USD") =>
        new(Guid.CreateVersion7(), PaymentEvent.TypeFor(status), paymentId, ++_sequence, status, merchant, customer,
            new Money(amount, currency), captured, status is "Authorized" or "Captured" or "Voided" ? "auth_1" : null,
            DateTimeOffset.UtcNow);

    public static ConsumeResult<string, string> Message(PaymentEvent e) => Message(e.PaymentId, JsonSerializer.Serialize(e, PaymentEvent.Json));

    public static ConsumeResult<string, string> Message(string key, string json) => new()
    {
        Topic = Topics.Payments,
        Message = new Message<string, string> { Key = key, Value = json, Headers = [new Header("event-type", Encoding.UTF8.GetBytes("test"))] },
    };

    public static PaymentEventHandler Handler(LedgerDbContext db) =>
        new(db, new LedgerWriter(db, new AccountRepository(db), TimeProvider.System), NullLogger<PaymentEventHandler>.Instance);

    /// <summary>Hands the event to a fresh handler, as the consumer loop does (one scope per message).</summary>
    public static async Task Deliver(PaymentEvent e)
    {
        await using var db = LedgerDb.NewContext();
        await Handler(db).HandleAsync(Message(e), default);
    }
}
