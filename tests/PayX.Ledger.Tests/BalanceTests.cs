using Microsoft.EntityFrameworkCore;
using PayX.Ledger.Domain;
using PayX.Ledger.Services;

namespace PayX.Ledger.Tests;

// Balances (7.5): updated in the postings' transaction, under a row lock, so
// they always equal the sum of the postings, however many consumers book at once.
public sealed class BalanceTests
{
    [Fact]
    public async Task Balances_follow_a_payment_through_hold_and_capture()
    {
        var events = Events.ForNewPayment();

        await Events.Deliver(events.Event("Authorized", amount: 5_000));
        Assert.Equal(5_000, await Balance(AccountType.CustomerHolds, events.Customer));       // on hold
        Assert.Equal(5_000, await Balance(AccountType.MerchantAuthorized, events.Merchant));

        await Events.Deliver(events.Event("Captured", amount: 5_000, captured: 3_000));
        Assert.Equal(0, await Balance(AccountType.CustomerHolds, events.Customer));           // hold released
        Assert.Equal(0, await Balance(AccountType.MerchantAuthorized, events.Merchant));
        Assert.Equal(3_000, await Balance(AccountType.MerchantPending, events.Merchant));     // owed, not yet settled
    }

    [Fact]
    public async Task A_redelivery_does_not_move_a_balance_twice()
    {
        var events = Events.ForNewPayment();
        var authorized = events.Event("Authorized", amount: 2_500);

        await Events.Deliver(authorized);
        await Events.Deliver(authorized);   // rolled back whole: entry, postings and balance (7.4)

        Assert.Equal(2_500, await Balance(AccountType.CustomerHolds, events.Customer));
    }

    [Fact]
    public async Task Concurrent_bookings_on_one_account_lose_no_update()
    {
        // 25 payments to one merchant, captured by parallel consumers at once.
        // Every capture adds to the same MerchantPending row (and the shared
        // PspClearing row): without the row lock, balance += amount races
        // and some captures vanish from the balance while their postings stay.
        Guid merchant = Guid.NewGuid();
        var payments = Enumerable.Range(0, 25)
            .Select(_ => new Events(Guid.NewGuid(), merchant, $"pay_{Guid.NewGuid():N}")).ToList();
        foreach (var p in payments)
            await Events.Deliver(p.Event("Authorized", amount: 1_000));

        await Task.WhenAll(payments.Select(p =>
            Task.Run(() => Events.Deliver(p.Event("Captured", amount: 1_000, captured: 1_000)))));

        Assert.Equal(25_000, await Balance(AccountType.MerchantPending, merchant));
        Assert.Equal(0, await Balance(AccountType.MerchantAuthorized, merchant));
        await using var db = LedgerDb.NewContext();
        Assert.Empty(await new BalanceAudit(db).FindMismatchesAsync(default));
    }

    [Fact]
    public async Task Every_balance_equals_its_postings_and_the_books_sum_to_zero()
    {
        await Events.Deliver(Events.ForNewPayment().Event("Authorized"));   // at least something to check

        await using var db = LedgerDb.NewContext();
        var audit = new BalanceAudit(db);
        Assert.Empty(await audit.FindMismatchesAsync(default));
        Assert.All(await audit.TrialBalanceAsync(default), c => Assert.Equal(0, c.Total));
    }

    [Fact]
    public async Task The_service_role_can_update_a_balance_but_nothing_else_about_an_account()
    {
        await Events.Deliver(Events.ForNewPayment().Event("Authorized"));
        await using var connection = new Npgsql.NpgsqlConnection(LedgerDb.Connection);
        await connection.OpenAsync();
        await using var retype = new Npgsql.NpgsqlCommand(
            "UPDATE accounts SET owner_id = gen_random_uuid() WHERE id = (SELECT max(id) FROM accounts)", connection);

        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => retype.ExecuteNonQueryAsync());
        Assert.Equal(Npgsql.PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);   // column grant: balance_minor only
    }

    private static async Task<long> Balance(AccountType type, Guid owner)
    {
        await using var db = LedgerDb.NewContext();
        var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.Type == type && a.OwnerId == owner && a.Currency == "USD");
        return account.DisplayBalanceMinor;
    }
}
