using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PayX.PspSimulator.Domain;
using PayX.PspSimulator.Repositories;

namespace PayX.PspSimulator.Services;

/// <summary>
/// The issuer side of an authorization, with memory: work out the card's
/// available credit, decide, and record the result as a hold.
/// </summary>
public sealed class Authorizer(PspDbContext db, IConfiguration config)
{
    private readonly byte[] _fingerprintKey = Encoding.UTF8.GetBytes(config["Psp:FingerprintKey"]!);

    public async Task<Authorization> AuthorizeAsync(string number, string brand, int expMonth, int expYear,
        long amountMinor, string currency, string merchantId, CancellationToken ct)
    {
        var fingerprint = Convert.ToHexString(HMACSHA256.HashData(_fingerprintKey, Encoding.UTF8.GetBytes(number)));

        // Join the caller's transaction if there is one (the idempotency
        // filter, Module 3.4, wraps the whole request in one); otherwise
        // start our own. Either way the lock below lasts until that commit.
        await using var ownTx = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;

        // First time this card is seen: open its account. ON CONFLICT makes
        // two simultaneous first payments safe; one inserts, the other no-ops.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO card_accounts (fingerprint, limit_minor) VALUES ({fingerprint}, {IssuerBank.DefaultLimitMinor}) ON CONFLICT (fingerprint) DO NOTHING", ct);

        // Lock this card's account row until the transaction ends. Without it,
        // two $3,000 authorizations on a $5,000 card arriving together would
        // both read "nothing held yet, $5,000 available", both be approved,
        // and $6,000 would be reserved against $5,000 of credit: the same
        // check-then-act race as two sign-ups for one username (Module 2.2),
        // except that no unique index can catch it. FOR UPDATE makes the
        // second request wait here until the first has committed its hold,
        // then it reads the new total.
        var account = (await db.CardAccounts
            .FromSqlInterpolated($"SELECT * FROM card_accounts WHERE fingerprint = {fingerprint} FOR UPDATE")
            .ToListAsync(ct)).Single();

        // Available = limit minus live holds minus charges. Derived, never
        // stored: a stored "available" column is one more number that can
        // disagree with the holds and charges it is supposed to summarise.
        // A capture swaps a hold for a charge of at most the same amount, so
        // it never reduces available credit further; a void frees it.
        var used = await db.Authorizations
            .Where(a => a.CardFingerprint == fingerprint)
            .SumAsync(a => a.Status == AuthorizationStatus.Authorized ? a.AmountMinor
                         : a.Status == AuthorizationStatus.Captured ? a.CapturedAmountMinor!.Value
                         : 0, ct);
        var available = account.LimitMinor - used;

        var declineCode = IssuerBank.Decide(number, expMonth, expYear, amountMinor, available,
            DateOnly.FromDateTime(DateTime.UtcNow));

        var authorization = new Authorization(fingerprint, number[^4..], brand, merchantId, amountMinor, currency,
            declineCode,
            authorizationCode: declineCode is null ? RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZ23456789", 6) : null);

        db.Authorizations.Add(authorization);
        await db.SaveChangesAsync(ct);
        if (ownTx is not null)
            await ownTx.CommitAsync(ct);   // releases the row lock; the next request for this card proceeds
        return authorization;
    }
}
