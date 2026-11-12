using PayX.Contracts;

namespace PayX.Ledger.Domain;

/// <summary>Why money moved: one entry kind per payment step that touches the books.</summary>
public enum EntryKind
{
    Authorization,   // a hold is placed (memo accounts only)
    Capture,         // the hold is released and the captured amount becomes real money
    Void,            // the hold is released, nothing charged
}

/// <summary>One leg of an entry, before its account has been looked up.</summary>
public readonly record struct Leg(AccountKey Account, long AmountMinor);

/// <summary>
/// One business event in the books: "payment X was captured". Doc ch.2,
/// "immutable entry … auditable trail" (DESIGN decision 9).
///
/// <para><b>Double entry.</b> An entry is two or more postings whose signed
/// amounts sum to zero: every unit of money that arrives somewhere left
/// somewhere else. Money can't appear or vanish, and if a bug ever tries,
/// the sum is not zero and the entry is refused, here and again by the
/// database (<c>ck_entry_balanced</c> trigger), so a hand-written INSERT
/// can't sneak an unbalanced one in either.</para>
///
/// <para>The headline amount (<see cref="Amount"/>) is what the entry is
/// about, for history screens. The postings are the truth.</para>
/// </summary>
public sealed class JournalEntry
{
    public long Id { get; private set; }
    public EntryKind Kind { get; private set; }
    public string PaymentId { get; private set; } = null!;
    public Guid CustomerId { get; private set; }
    public Guid MerchantId { get; private set; }
    public Money Amount { get; private set; } = null!;
    /// <summary>When it happened at the source (the payment event's time).</summary>
    public DateTimeOffset OccurredAt { get; private set; }
    /// <summary>When the ledger booked it.</summary>
    public DateTimeOffset RecordedAt { get; private set; }

    private readonly List<Posting> _postings = [];
    public IReadOnlyList<Posting> Postings => _postings;

    private JournalEntry() { }   // EF Core

    /// <param name="accounts">The accounts the legs name, already looked up or created.</param>
    public JournalEntry(EntryKind kind, string paymentId, Guid customerId, Guid merchantId, Money amount,
        DateTimeOffset occurredAt, DateTimeOffset recordedAt, IReadOnlyList<Leg> legs,
        IReadOnlyDictionary<AccountKey, Account> accounts)
    {
        EnsureBalanced(legs);

        Kind = kind;
        PaymentId = paymentId;
        CustomerId = customerId;
        MerchantId = merchantId;
        Amount = amount;
        OccurredAt = occurredAt;
        RecordedAt = recordedAt;
        foreach (var leg in legs)
            _postings.Add(new Posting(accounts[leg.Account], leg.AmountMinor));
    }

    /// <summary>
    /// The double-entry rule: at least two legs, none zero, and per currency
    /// the debits (+) and credits (−) cancel out exactly. Per currency
    /// because $10 and €10 don't cancel; a currency exchange is two balanced
    /// halves through an FX account, never one entry that "sums" across.
    /// </summary>
    public static void EnsureBalanced(IReadOnlyList<Leg> legs)
    {
        if (legs.Count < 2)
            throw new UnbalancedEntryException("An entry needs at least two postings: money moves from somewhere to somewhere");
        if (legs.Any(l => l.AmountMinor == 0))
            throw new UnbalancedEntryException("A posting of zero moves nothing");

        foreach (var currency in legs.GroupBy(l => l.Account.Currency))
        {
            var sum = currency.Aggregate(0L, (total, leg) => checked(total + leg.AmountMinor));
            if (sum != 0)
                throw new UnbalancedEntryException($"Postings in {currency.Key} sum to {sum}, not 0");
        }
    }
}

/// <summary>
/// One line of an entry: this account, this signed amount (+ debit, − credit).
/// The currency is the account's, repeated here so the database can check
/// the two agree (composite foreign key) and sum per currency without a join.
/// </summary>
public sealed class Posting
{
    public long Id { get; private set; }
    public long EntryId { get; private set; }
    public long AccountId { get; private set; }
    public long AmountMinor { get; private set; }
    public string Currency { get; private set; } = null!;

    private Posting() { }   // EF Core

    internal Posting(Account account, long amountMinor)
    {
        AccountId = account.Id;
        Currency = account.Currency;
        AmountMinor = amountMinor;
    }
}

public sealed class UnbalancedEntryException(string message) : InvalidOperationException(message);
