namespace PayX.Payment.Repositories;

/// <summary>
/// An event waiting to be published (doc ch.2 "Transaction completion";
/// DESIGN decision 5, the transactional outbox). Written in the same database
/// transaction as the state change it announces, so the two exist together
/// or not at all. The relay (6.2) publishes it to Kafka and stamps
/// <see cref="PublishedAt"/>.
/// </summary>
public sealed class OutboxMessage
{
    /// <summary>Increasing: the relay publishes in this order, which keeps one payment's events in order.</summary>
    public long Id { get; private set; }
    public Guid EventId { get; private set; }
    public string Topic { get; private set; } = null!;
    /// <summary>The Kafka message key: the payment id, so all its events share a partition.</summary>
    public string Key { get; private set; } = null!;
    public string EventType { get; private set; } = null!;
    public string Payload { get; private set; } = null!;   // the event as JSON
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }

    public OutboxMessage(Guid eventId, string topic, string key, string eventType, string payload, DateTimeOffset createdAt)
    {
        EventId = eventId;
        Topic = topic;
        Key = key;
        EventType = eventType;
        Payload = payload;
        CreatedAt = createdAt;
    }

    private OutboxMessage() { }   // EF Core

    public void MarkPublished(DateTimeOffset at) => PublishedAt = at;
}
