using Npgsql;
using PayX.Contracts;
using PayX.Ledger.Domain;
using PayX.Ledger.Repositories;
using PayX.Ledger.Services;

namespace PayX.Ledger.Tests;

// Append-only (7.2): history can be added to, never changed. The service's
// role has no privilege to try; the owner, which has, is stopped by the trigger.
public sealed class AppendOnlyTests
{
    [Theory]
    [InlineData("UPDATE postings SET amount_minor = amount_minor * 2 WHERE entry_id = @e")]
    [InlineData("DELETE FROM postings WHERE entry_id = @e")]
    [InlineData("UPDATE journal_entries SET amount_minor = 1 WHERE id = @e")]
    [InlineData("DELETE FROM journal_entries WHERE id = @e")]
    public async Task The_service_role_may_not_change_history(string sql)
    {
        var entryId = await AnEntry();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => Execute(LedgerDb.Connection, sql, entryId));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);   // never even reaches the trigger
    }

    [Theory]
    [InlineData("UPDATE postings SET amount_minor = amount_minor * 2 WHERE entry_id = @e", "postings")]
    [InlineData("DELETE FROM postings WHERE entry_id = @e", "postings")]
    [InlineData("UPDATE journal_entries SET amount_minor = 1 WHERE id = @e", "journal_entries")]
    [InlineData("DELETE FROM journal_entries WHERE id = @e", "journal_entries")]
    public async Task Even_the_owner_is_stopped_by_the_trigger(string sql, string table)
    {
        var entryId = await AnEntry();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => Execute(LedgerDb.OwnerConnection, sql, entryId));

        Assert.Equal(PostgresErrorCodes.RestrictViolation, ex.SqlState);
        Assert.Contains($"append-only", ex.MessageText);
        Assert.Contains(table, ex.MessageText);
    }

    [Theory]
    [InlineData("TRUNCATE postings")]
    [InlineData("TRUNCATE journal_entries CASCADE")]
    [InlineData("TRUNCATE accounts CASCADE")]
    public async Task Nobody_can_truncate_the_books(string sql)
    {
        _ = await AnEntry();   // make sure the tables exist

        var ex = await Assert.ThrowsAsync<PostgresException>(() => Execute(LedgerDb.OwnerConnection, sql, 0));

        Assert.Equal(PostgresErrorCodes.RestrictViolation, ex.SqlState);
    }

    [Fact]
    public async Task The_service_role_can_still_append()
    {
        // Same role as the failures above: reading and adding work.
        var entryId = await AnEntry();
        await using var connection = new NpgsqlConnection(LedgerDb.Connection);
        await connection.OpenAsync();
        await using var count = new NpgsqlCommand("SELECT count(*) FROM postings WHERE entry_id = @e", connection);
        count.Parameters.AddWithValue("e", entryId);
        Assert.Equal(2L, await count.ExecuteScalarAsync());
    }

    private static async Task<long> AnEntry()
    {
        Guid customer = Guid.NewGuid(), merchant = Guid.NewGuid();
        await using var db = LedgerDb.NewContext();
        var entry = await new LedgerWriter(db, new AccountRepository(db), TimeProvider.System).PostAsync(new EntryDraft(
            EntryKind.Authorization, $"pay_{Guid.NewGuid():N}", customer, merchant, new Money(700, "USD"),
            DateTimeOffset.UtcNow, [
                new Leg(new AccountKey(AccountType.CustomerHolds, customer, "USD"), 700),
                new Leg(new AccountKey(AccountType.MerchantAuthorized, merchant, "USD"), -700)], Guid.NewGuid()), default);
        return entry.Entry!.Id;
    }

    private static async Task Execute(string connectionString, string sql, long entryId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("e", entryId);
        await command.ExecuteNonQueryAsync();
    }
}
