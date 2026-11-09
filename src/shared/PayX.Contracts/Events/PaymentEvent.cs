using System.Text.Json;
using System.Text.Json.Serialization;

namespace PayX.Contracts.Events;

/// <summary>Kafka topic names. One topic per aggregate (DESIGN decision 4).</summary>
public static class Topics
{
    /// <summary>Every payment event; message key = payment id, so one payment's events stay in order.</summary>
    public const string Payments = "payx.payments";

    /// <summary>Payment events a consumer could not process (6.6).</summary>
    public const string PaymentsDeadLetter = "payx.payments.dlq";
}

/// <summary>
/// "Payment X is now in status Y": published on <see cref="Topics.Payments"/>
/// for every status change (doc ch.2, "Transaction completion"). The event
/// type travels inside the message (<c>payment.authorized</c>,
/// <c>payment.captured</c>…), never as the topic name.
/// </summary>
/// <param name="EventId">Unique per event. Consumers dedupe on it: the relay
/// publishes at least once, so the same event can arrive twice (7.4).</param>
/// <param name="Sequence">Position in the payment's own history (1, 2, 3…),
/// so a consumer can tell an old event from a newer one.</param>
public sealed record PaymentEvent(
    Guid EventId,
    string EventType,
    string PaymentId,
    int Sequence,
    string Status,
    Guid MerchantId,
    Guid CustomerId,
    Money Amount,
    long? CapturedAmountMinor,
    string? PspAuthorizationId,
    DateTimeOffset OccurredAt)
{
    /// <summary>"Authorized" → "payment.authorized".</summary>
    public static string TypeFor(string status) => $"payment.{status.ToLowerInvariant()}";

    /// <summary>
    /// The one way this event is written and read (7.3): camelCase, enums as
    /// text. Shared here so producer (Payment's outbox) and consumers
    /// (Ledger) can't drift apart; each used to need its own copy, and a
    /// consumer that forgot camelCase would read every field as null.
    /// Read-only, so no caller can change it for everyone else.
    /// </summary>
    public static readonly JsonSerializerOptions Json = CreateJson();

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
