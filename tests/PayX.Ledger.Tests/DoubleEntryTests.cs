using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayX.Contracts;
using PayX.Ledger.Domain;
using PayX.Ledger.Repositories;
using PayX.Ledger.Services;

namespace PayX.Ledger.Tests;

// Double entry (7.1): every entry's postings sum to zero per currency,
// enforced by the code and, independently, by the database at commit.
public sealed class DoubleEntryTests
{
    private static Leg Debit(AccountType type, Guid owner, long amount, string currency = "USD") =>
        new(new AccountKey(type, owner, currency), amount);
    private static Leg Credit(AccountType type, Guid owner, long amount, string currency = "USD") =>
        new(new AccountKey(type, owner, currency), -amount);

    [Fact]
    public void A_balanced_entry_is_accepted()
    {
        Guid customer = Guid.NewGuid(), merchant = Guid.NewGuid();
        JournalEntry.EnsureBalanced([
            Debit(AccountType.CustomerHolds, customer, 5_000),
            Credit(AccountType.MerchantAuthorized, merchant, 5_000)]);
    }

    [Theory]
    [InlineData(5_000, 4_999)]   // a cent appears from nowhere
    [InlineData(5_000, 5_001)]   // a cent vanishes
    public void An_unbalanced_entry_is_refused(long debit, long credit)
    {
        var ex = Assert.Throws<UnbalancedEntryException>(() => JournalEntry.EnsureBalanced([
            Debit(AccountType.PspClearing, AccountTypes.System, debit),
            Credit(AccountType.MerchantPending, Guid.NewGuid(), credit)]));
        Assert.Contains("USD", ex.Message);
    }

    [Fact]
    public void One_leg_or_a_zero_leg_is_refused()
    {
        Assert.Throws<UnbalancedEntryException>(() => JournalEntry.EnsureBalanced([
            Debit(AccountType.PspClearing, AccountTypes.System, 100)]));
        Assert.Throws<UnbalancedEntryException>(() => JournalEntry.EnsureBalanced([
            Debit(AccountType.PspClearing, AccountTypes.System, 0),
            Credit(AccountType.MerchantPending, Guid.NewGuid(), 0)]));
    }

    [Fact]
    public void Currencies_never_cancel_each_other()
    {
        // +$10 and −€10 "sum to zero" as numbers, but they are not the same money.
        Assert.Throws<UnbalancedEntryException>(() => JournalEntry.EnsureBalanced([
            Debit(AccountType.PspClearing, AccountTypes.System, 1_000, "USD"),
            Credit(AccountType.MerchantPending, Guid.NewGuid(), 1_000, "EUR")]));
    }

    [Fact]
    public async Task An_entry_is_stored_with_its_postings_and_accounts_are_opened_on_first_use()
    {
        Guid customer = Guid.NewGuid(), merchant = Guid.NewGuid();
        var paymentId = $"pay_{Guid.NewGuid():N}";

        await using (var db = LedgerDb.NewContext())
        {
            await Writer(db).PostAsync(new EntryDraft(EntryKind.Authorization, paymentId, customer, merchant,
                new Money(5_000, "USD"), DateTimeOffset.UtcNow, [
                    Debit(AccountType.CustomerHolds, customer, 5_000),
                    Credit(AccountType.MerchantAuthorized, merchant, 5_000)], Guid.NewGuid()), default);
            // A second entry for the same merchant reuses its account.
            await Writer(db).PostAsync(new EntryDraft(EntryKind.Void, paymentId, customer, merchant,
                new Money(5_000, "USD"), DateTimeOffset.UtcNow, [
                    Credit(AccountType.CustomerHolds, customer, 5_000),
                    Debit(AccountType.MerchantAuthorized, merchant, 5_000)], Guid.NewGuid()), default);
        }

        await using var check = LedgerDb.NewContext();
        var entries = await check.JournalEntries.Include(e => e.Postings)
            .Where(e => e.PaymentId == paymentId).OrderBy(e => e.Id).ToListAsync();
        Assert.Equal([EntryKind.Authorization, EntryKind.Void], entries.Select(e => e.Kind));
        Assert.All(entries, e => Assert.Equal(0, e.Postings.Sum(p => p.AmountMinor)));
        Assert.Equal(1, await check.Accounts.CountAsync(a => a.OwnerId == merchant));   // one account, used twice
    }

    [Fact]
    public async Task The_database_refuses_an_unbalanced_entry_even_when_the_code_is_bypassed()
    {
        var (entryId, account1, account2) = await EntryAndTwoAccounts();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertPostings(entryId,
            (account1, 5_000), (account2, -4_999)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal("ck_entry_balanced", ex.ConstraintName);
    }

    [Fact]
    public async Task The_database_refuses_an_entry_with_no_postings()
    {
        await using var connection = new NpgsqlConnection(LedgerDb.Connection);
        await connection.OpenAsync();
        await using var insert = new NpgsqlCommand("""
            INSERT INTO journal_entries (kind, payment_id, customer_id, merchant_id, amount_minor, currency, occurred_at, recorded_at)
            VALUES ('Capture', 'pay_lonely', gen_random_uuid(), gen_random_uuid(), 100, 'USD', now(), now())
            """, connection);

        // Autocommit: the deferred check runs at the end of this statement's transaction.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal("ck_entry_balanced", ex.ConstraintName);
    }

    [Fact]
    public async Task The_database_refuses_a_posting_in_another_currency_than_its_account()
    {
        var (entryId, usdAccount, _) = await EntryAndTwoAccounts();

        await using var connection = new NpgsqlConnection(LedgerDb.Connection);
        await connection.OpenAsync();
        await using var insert = new NpgsqlCommand(
            "INSERT INTO postings (entry_id, account_id, amount_minor, currency) VALUES (@e, @a, 100, 'EUR')", connection);
        insert.Parameters.AddWithValue("e", entryId);
        insert.Parameters.AddWithValue("a", usdAccount);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);   // (account, EUR) doesn't exist
    }

    private static LedgerWriter Writer(LedgerDbContext db) => new(db, new AccountRepository(db), TimeProvider.System);

    // A real, balanced entry (so its id exists) plus two USD accounts to aim raw postings at.
    private static async Task<(long EntryId, long Account1, long Account2)> EntryAndTwoAccounts()
    {
        Guid customer = Guid.NewGuid(), merchant = Guid.NewGuid();
        await using var db = LedgerDb.NewContext();
        var entry = await Writer(db).PostAsync(new EntryDraft(EntryKind.Authorization, $"pay_{Guid.NewGuid():N}",
            customer, merchant, new Money(100, "USD"), DateTimeOffset.UtcNow, [
                Debit(AccountType.CustomerHolds, customer, 100),
                Credit(AccountType.MerchantAuthorized, merchant, 100)], Guid.NewGuid()), default);
        var booked = entry.Entry!;
        return (booked.Id, booked.Postings[0].AccountId, booked.Postings[1].AccountId);
    }

    // Raw SQL in one transaction: what a buggy code path or a hand-typed fix would do.
    private static async Task InsertPostings(long entryId, params (long Account, long Amount)[] postings)
    {
        await using var connection = new NpgsqlConnection(LedgerDb.Connection);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        foreach (var (account, amount) in postings)
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO postings (entry_id, account_id, amount_minor, currency) VALUES (@e, @a, @m, 'USD')", connection, tx);
            insert.Parameters.AddWithValue("e", entryId);
            insert.Parameters.AddWithValue("a", account);
            insert.Parameters.AddWithValue("m", amount);
            await insert.ExecuteNonQueryAsync();   // each row is accepted for now...
        }
        await tx.CommitAsync();                     // ...the entry as a whole is checked here
    }
}
