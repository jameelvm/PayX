using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PayX.Identity.Domain;
using PayX.Identity.Repositories;

namespace PayX.Identity.Services;

/// <summary>Doc ch.2, <c>authenticateUser(username, password)</c>.</summary>
public sealed class UserAuthentication(IdentityDbContext db, IPasswordHasher<User> hasher, TokenIssuer tokens)
{
    // A real hash of a password nobody knows, computed once. Verified against
    // when the username doesn't exist; see below.
    private static readonly string DummyHash =
        new PasswordHasher<User>().HashPassword(null!, Guid.NewGuid().ToString());

    /// <returns>A token, or null for "invalid username or password".</returns>
    public async Task<IssuedToken?> AuthenticateAsync(string username, string password, CancellationToken ct)
    {
        var normalized = username.Trim().ToLowerInvariant();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Username == normalized, ct);

        if (user is null)
        {
            // Spend the same ~100,000 iterations as a real check before
            // answering. Otherwise "no such user" returns in 1 ms and "wrong
            // password" in ~50 ms, and anyone with a stopwatch can find out
            // which usernames exist: the same leak an honest error message
            // would cause, through timing instead.
            hasher.VerifyHashedPassword(null!, DummyHash, password);
            return null;
        }

        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
            return null;

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            // The stored hash used weaker settings (fewer iterations, older
            // algorithm) than today's. Only now, with the plain password in
            // hand after a successful login, can it be upgraded, so hashes get
            // stronger over time without forcing anyone to reset a password.
            user.SetPasswordHash(hasher.HashPassword(user, password));
            await db.SaveChangesAsync(ct);
        }

        return tokens.Issue(user);
    }
}
