using PayX.Contracts;
using PayX.Payment.Domain;
using static PayX.Payment.Domain.PaymentStatus;

namespace PayX.Payment.Tests;

public sealed class PaymentStateMachineTests
{
    // The specification, written out independently of PaymentStateMachine so
    // the test can't agree with the code just by reading the same table.
    private static readonly HashSet<(PaymentStatus, PaymentStatus)> Legal =
    [
        (Created, RiskApproved), (Created, RiskBlocked),
        (RiskApproved, Authorized), (RiskApproved, Declined),
        (Authorized, Captured), (Authorized, Voided),
    ];

    // Every (from, to) pair except "to Created": no method leads back to the
    // start, so there's nothing to call (checked separately below).
    public static TheoryData<PaymentStatus, PaymentStatus> AllPairs()
    {
        var data = new TheoryData<PaymentStatus, PaymentStatus>();
        foreach (var from in Enum.GetValues<PaymentStatus>())
        foreach (var to in Enum.GetValues<PaymentStatus>().Where(s => s != Created))
            data.Add(from, to);
        return data;
    }

    [Theory, MemberData(nameof(AllPairs))]
    public void Only_listed_moves_succeed(PaymentStatus from, PaymentStatus to)
    {
        var payment = PaymentIn(from);

        if (Legal.Contains((from, to)))
        {
            MoveTo(payment, to);
            Assert.Equal(to, payment.Status);
        }
        else
        {
            var ex = Assert.Throws<IllegalTransitionException>(() => MoveTo(payment, to));
            Assert.Equal((from, to), (ex.From, ex.To));
            Assert.Equal(from, payment.Status);   // a refused move changes nothing
        }
    }

    [Fact]
    public void Final_states_are_exactly_the_ones_with_no_way_out() =>
        Assert.Equal([RiskBlocked, Declined, Captured, Voided],
            Enum.GetValues<PaymentStatus>().Where(PaymentStateMachine.IsFinal));

    [Fact]
    public void Nothing_moves_back_to_Created() =>
        Assert.DoesNotContain(Enum.GetValues<PaymentStatus>(), s => PaymentStateMachine.CanMove(s, Created));

    [Fact]
    public void Partial_capture_records_the_captured_amount()
    {
        var payment = PaymentIn(Authorized);

        payment.Capture(new Money(2_000, "USD"));

        Assert.Equal(2_000, payment.CapturedAmountMinor);
    }

    [Theory]
    [InlineData(5_001, "USD")]   // more than authorized
    [InlineData(5_000, "EUR")]   // other currency
    public void Capture_outside_the_authorization_is_refused_and_changes_nothing(long amount, string currency)
    {
        var payment = PaymentIn(Authorized);

        Assert.ThrowsAny<ArgumentException>(() => payment.Capture(new Money(amount, currency)));
        Assert.Equal((Authorized, (long?)null), (payment.Status, payment.CapturedAmountMinor));
    }

    [Fact]
    public void A_payment_must_be_for_a_positive_amount() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Domain.Payment(Guid.NewGuid(), Guid.NewGuid(), new Money(0, "USD"), "tok_x"));

    // Reaches a state the only way production code can: by legal moves.
    private static Domain.Payment PaymentIn(PaymentStatus status)
    {
        var payment = new Domain.Payment(Guid.NewGuid(), Guid.NewGuid(), new Money(5_000, "USD"), "tok_test");
        PaymentStatus[] path = status switch
        {
            Created => [],
            RiskApproved or RiskBlocked => [status],
            Authorized or Declined => [RiskApproved, status],
            Captured or Voided => [RiskApproved, Authorized, status],
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
        foreach (var step in path)
            MoveTo(payment, step);
        return payment;
    }

    private static void MoveTo(Domain.Payment payment, PaymentStatus to)
    {
        switch (to)
        {
            case RiskApproved: payment.ApproveRisk(); break;
            case RiskBlocked: payment.BlockRisk(); break;
            case Authorized: payment.Authorize("auth_test", "A1B2C3"); break;
            case Declined: payment.Decline("card_declined"); break;
            case Captured: payment.Capture(payment.Amount); break;
            case Voided: payment.Void(); break;
            default: throw new ArgumentOutOfRangeException(nameof(to));
        }
    }
}
