using Npgsql;

namespace PayX.Ledger.Tests;

// TRUNCATE takes an exclusive lock on each table it names (and, with
// CASCADE, on every table referencing them) before its trigger runs. Beside
// anything booking at the same moment, holding row locks on accounts (7.5)
// or inserting entries, it can deadlock, and Postgres rolls the TRUNCATE
// back as the victim (40P01) before the trigger answers. Two sources of
// that: this assembly's own parallel tests (hence the serial collection),
// and the live Ledger container booking Payment's test events during a
// full-solution test run, which no collection can see (found in 7.6). So
// a deadlock is retried: it truncated nothing (a victim rolls back whole),
// it just didn't reach the trigger yet.
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

        PostgresException? refused = null;
        for (var attempt = 1; attempt <= 5 && refused?.SqlState != PostgresErrorCodes.RestrictViolation; attempt++)
        {
            refused = await Assert.ThrowsAsync<PostgresException>(() =>
                AppendOnlyTests.Execute(LedgerDb.OwnerConnection, sql, 0));
            Assert.True(refused.SqlState is PostgresErrorCodes.RestrictViolation or PostgresErrorCodes.DeadlockDetected,
                $"unexpected {refused.SqlState}: {refused.MessageText}");
        }

        Assert.Equal(PostgresErrorCodes.RestrictViolation, refused!.SqlState);   // the trigger said no
    }
}
