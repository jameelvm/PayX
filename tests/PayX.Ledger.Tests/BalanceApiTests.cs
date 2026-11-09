using PayX.Ledger.Services;

namespace PayX.Ledger.Tests;

// Balance management (7.7): authorized vs pending vs available, per currency.
public sealed class BalanceApiTests
{
    [Fact]
    public async Task A_merchant_sees_holds_captured_money_and_nothing_available_before_settlement()
    {
        var merchant = Guid.NewGuid();
        var held = new Events(Guid.NewGuid(), merchant, $"pay_{Guid.NewGuid():N}");
        var captured = new Events(Guid.NewGuid(), merchant, $"pay_{Guid.NewGuid():N}");
        await Events.Deliver(held.Event("Authorized", amount: 2_000));
        await Events.Deliver(captured.Event("Authorized", amount: 5_000));
        await Events.Deliver(captured.Event("Captured", amount: 5_000, captured: 3_000));

        await using var db = LedgerDb.NewContext();
        var usd = Assert.Single(await new LedgerQueries(db).MerchantBalancesAsync(merchant, default));

        Assert.Equal(new MerchantBalance("USD", Authorized: 2_000, Pending: 3_000, Available: 0), usd);
    }

    [Fact]
    public async Task Currencies_are_never_added_together()
    {
        var merchant = Guid.NewGuid();
        await Events.Deliver(new Events(Guid.NewGuid(), merchant, $"pay_{Guid.NewGuid():N}").Event("Authorized", amount: 1_000, currency: "USD"));
        await Events.Deliver(new Events(Guid.NewGuid(), merchant, $"pay_{Guid.NewGuid():N}").Event("Authorized", amount: 900, currency: "EUR"));

        await using var db = LedgerDb.NewContext();
        var balances = await new LedgerQueries(db).MerchantBalancesAsync(merchant, default);

        Assert.Equal(["EUR", "USD"], balances.Select(b => b.Currency));
        Assert.Equal([900L, 1_000L], balances.Select(b => b.Authorized));
    }

    [Fact]
    public async Task A_customer_sees_what_is_on_hold_and_it_clears_on_capture_or_void()
    {
        var customer = Guid.NewGuid();
        var captured = new Events(customer, Guid.NewGuid(), $"pay_{Guid.NewGuid():N}");
        var voided = new Events(customer, Guid.NewGuid(), $"pay_{Guid.NewGuid():N}");
        var open = new Events(customer, Guid.NewGuid(), $"pay_{Guid.NewGuid():N}");
        foreach (var p in new[] { captured, voided, open })
            await Events.Deliver(p.Event("Authorized", amount: 1_500));
        await Events.Deliver(captured.Event("Captured", amount: 1_500, captured: 1_500));
        await Events.Deliver(voided.Event("Voided", amount: 1_500));

        await using var db = LedgerDb.NewContext();
        var usd = Assert.Single(await new LedgerQueries(db).CustomerBalancesAsync(customer, default));

        Assert.Equal(1_500, usd.OnHold);   // only the open authorization is still held
    }

    [Fact]
    public async Task Someone_with_no_payments_has_no_balances()
    {
        await using var db = LedgerDb.NewContext();
        Assert.Empty(await new LedgerQueries(db).MerchantBalancesAsync(Guid.NewGuid(), default));
        Assert.Empty(await new LedgerQueries(db).CustomerBalancesAsync(Guid.NewGuid(), default));
    }
}
