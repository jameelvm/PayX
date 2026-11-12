using PayX.Contracts;
using PayX.Contracts.Events;

namespace PayX.Ledger.Domain;

/// <summary>
/// Which payment events touch the books, and how (7.3). Doc ch.2: the card
/// flow is two-phase, authorize (reserve) then capture (move), and the books
/// follow it. A pure function: same event in, same legs out, which is what
/// lets a replayed event be recognised and skipped (7.4).
///
/// <code>
///                       CustomerHolds  MerchantAuthorized  PspClearing  MerchantPending
/// Authorized  (A)            +A              −A
/// Captured    (A, C≤A)       −A              +A                +C            −C
/// Voided      (A)            −A              +A
/// </code>
///
/// Holds live only in the two memo accounts, so a hold never mixes with
/// money. A capture releases the whole hold and books what was taken: a
/// partial capture of C &lt; A leaves nothing on hold (one capture per
/// authorization, as at the PSP). Real money then sits as "PSP owes us C"
/// against "we owe the merchant C, not yet settled"; settlement (Phase 8)
/// moves it on to MerchantAvailable.
/// </summary>
public static class PostingRules
{
    /// <summary>Statuses that are events in the payment's life but move nothing the ledger holds.</summary>
    public static readonly IReadOnlySet<string> NotBooked = new HashSet<string>(StringComparer.Ordinal)
    {
        "Created", "RiskApproved", "RiskBlocked", "Challenged", "Declined", "Unknown", "Failed",
    };

    public sealed record Booking(EntryKind Kind, Money Amount, IReadOnlyList<Leg> Legs);

    /// <returns>The entry this event books, or null when it books nothing.</returns>
    public static Booking? For(PaymentEvent e)
    {
        var currency = e.Amount.Currency;
        var authorized = e.Amount.AmountMinor;
        AccountKey Holds() => new(AccountType.CustomerHolds, e.CustomerId, currency);
        AccountKey Authorized() => new(AccountType.MerchantAuthorized, e.MerchantId, currency);

        switch (e.Status)
        {
            case "Authorized":
                return new Booking(EntryKind.Authorization, e.Amount, [
                    new Leg(Holds(), authorized),
                    new Leg(Authorized(), -authorized)]);

            case "Captured":
                var captured = e.CapturedAmountMinor!.Value;   // validated by the handler
                return new Booking(EntryKind.Capture, new Money(captured, currency), [
                    new Leg(Holds(), -authorized),
                    new Leg(Authorized(), authorized),
                    new Leg(new AccountKey(AccountType.PspClearing, AccountTypes.System, currency), captured),
                    new Leg(new AccountKey(AccountType.MerchantPending, e.MerchantId, currency), -captured)]);

            case "Voided":
                return new Booking(EntryKind.Void, e.Amount, [
                    new Leg(Holds(), -authorized),
                    new Leg(Authorized(), authorized)]);

            default:
                return null;
        }
    }
}
