namespace PayX.Ledger.Domain;

/// <summary>
/// "This event is already in the books" (7.4): the consumer's inbox. Kafka
/// delivers at least once (6.2 relay, 7.3 commit-after-booking), so the
/// same event can arrive twice; its id is written in the same transaction as
/// its entry, and its primary key lets only one of two deliveries commit.
/// Points at the entry it produced: an auditor can go from any booking back
/// to the exact event that caused it.
/// </summary>
public sealed class ProcessedEvent
{
    public Guid EventId { get; private set; }
    public string PaymentId { get; private set; } = null!;
    public long EntryId { get; private set; }
    public DateTimeOffset ProcessedAt { get; private set; }

    private ProcessedEvent() { }   // EF Core

    public ProcessedEvent(Guid eventId, string paymentId, JournalEntry entry, DateTimeOffset processedAt)
    {
        EventId = eventId;
        PaymentId = paymentId;
        Entry = entry;
        ProcessedAt = processedAt;
    }

    internal JournalEntry? Entry { get; private set; }   // so EF fills EntryId on insert
}
