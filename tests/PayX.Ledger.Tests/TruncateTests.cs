using Npgsql;

namespace PayX.Ledger.Tests;

// Runs on its own, after every other test class (DisableParallelization).
// TRUNCATE takes an exclusive lock on each table it names (and, with
// CASCADE, on every table referencing them). Run beside tests that hold
// FOR UPDATE row locks on accounts (7.5), it deadlocked, and Postgres
// rolled the TRUNCATE back as the victim (40P01) before its trigger could
// answer. Found when 7.5 added the locks.
[CollectionDefinition(nameof(SerialLedgerTests), DisableParallelization = true)]
public sealed class SerialLedgerTests;

[Collection(nameof(SerialLedgerTests))]
public sealed class TruncateTests
{
    [Theory]
    [InlineData("TRUNCATE postings")]
    [InlineData("TRUNCATE journal_entries CASCADE")]
    [InlineData("TRUNCATE accounts CASCADE")]
    public async Task Nobody_can_truncate_the_books(string sql)
    {
        _ = await AppendOnlyTests.AnEntry();   // make sure the tables exist

        var ex = await Assert.ThrowsAsync<PostgresException>(() => AppendOnlyTests.Execute(LedgerDb.OwnerConnection, sql, 0));

        Assert.Equal(PostgresErrorCodes.RestrictViolation, ex.SqlState);
    }
}
