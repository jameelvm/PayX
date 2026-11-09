using PayX.Ledger.Domain;

namespace PayX.Ledger.Tests;

// Which events book what (7.3). Pure: no database.
public sealed class PostingRulesTests
{
    private readonly Events _events = Events.ForNewPayment();

    private long On(PostingRules.Booking booking, AccountType type) =>
        booking.Legs.Where(l => l.Account.Type == type).Sum(l => l.AmountMinor);

    [Fact]
    public void An_authorization_books_a_hold_in_the_memo_accounts_only()
    {
        var booking = PostingRules.For(_events.Event("Authorized", amount: 5_000))!;

        Assert.Equal(EntryKind.Authorization, booking.Kind);
        Assert.Equal(5_000, On(booking, AccountType.CustomerHolds));
        Assert.Equal(-5_000, On(booking, AccountType.MerchantAuthorized));
        Assert.DoesNotContain(booking.Legs, l => l.Account.Type is AccountType.PspClearing or AccountType.MerchantPending);
        JournalEntry.EnsureBalanced(booking.Legs);
    }

    [Fact]
    public void A_partial_capture_releases_the_whole_hold_and_books_what_was_taken()
    {
        var booking = PostingRules.For(_events.Event("Captured", amount: 5_000, captured: 3_000))!;

        Assert.Equal(EntryKind.Capture, booking.Kind);
        Assert.Equal(3_000, booking.Amount.AmountMinor);
        Assert.Equal(-5_000, On(booking, AccountType.CustomerHolds));       // hold released in full
        Assert.Equal(5_000, On(booking, AccountType.MerchantAuthorized));
        Assert.Equal(3_000, On(booking, AccountType.PspClearing));          // the PSP owes us 30.00
        Assert.Equal(-3_000, On(booking, AccountType.MerchantPending));     // we owe the merchant 30.00
        JournalEntry.EnsureBalanced(booking.Legs);
    }

    [Fact]
    public void A_void_releases_the_hold_and_charges_nothing()
    {
        var booking = PostingRules.For(_events.Event("Voided", amount: 5_000))!;

        Assert.Equal(EntryKind.Void, booking.Kind);
        Assert.Equal(-5_000, On(booking, AccountType.CustomerHolds));
        Assert.Equal(5_000, On(booking, AccountType.MerchantAuthorized));
        JournalEntry.EnsureBalanced(booking.Legs);
    }

    [Theory]
    [InlineData("RiskApproved")]
    [InlineData("RiskBlocked")]
    [InlineData("Challenged")]
    [InlineData("Declined")]
    [InlineData("Unknown")]
    [InlineData("Failed")]
    public void Events_that_move_no_money_book_nothing(string status)
    {
        Assert.Null(PostingRules.For(_events.Event(status)));
        Assert.Contains(status, PostingRules.NotBooked);
    }
}
