using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayX.Identity.Domain;
using PayX.Identity.Repositories;

namespace PayX.Identity.Services;

public enum RegistrationOutcome { Created, UsernameTaken, EmailTaken, RoleNotAllowed }

/// <summary>Doc ch.2, <c>registerUser(username, email, password)</c>.</summary>
public sealed class UserRegistration(IdentityDbContext db, IPasswordHasher<User> hasher)
{
    public async Task<(RegistrationOutcome Outcome, User? User)> RegisterAsync(
        string username, string email, string password, UserRole role, CancellationToken ct)
    {
        // Ops accounts can reconcile and resolve disputes, i.e. move money
        // between ledgers. Nobody gets that by filling in a sign-up form.
        if (role == UserRole.Ops)
            return (RegistrationOutcome.RoleNotAllowed, null);

        // Normalise before anything else, so "Priya@Example.com " and
        // "priya@example.com" are the same account. The database's check
        // constraints (Module 2.1) refuse anything this step misses.
        var user = new User(username.Trim().ToLowerInvariant(), email.Trim().ToLowerInvariant(), role);

        // ASP.NET Core Identity's hasher: PBKDF2-HMAC-SHA512 with a random
        // 128-bit salt and 100,000 iterations, all encoded into one string.
        // Slow on purpose: a leaked table of these costs an attacker ~100,000
        // hash computations per guess, per user. A plain SHA-256 is billions
        // of guesses per second on a GPU, and unsalted it gives identical
        // hashes for identical passwords.
        user.SetPasswordHash(hasher.HashPassword(user, password));

        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
            return (RegistrationOutcome.Created, user);
        }
        // No "does this username exist?" query first: two sign-ups racing for
        // the same name would both see "no" and both insert. The unique index
        // is the only check that can't race, so we insert and let it decide.
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation } unique)
        {
            return unique.ConstraintName == "ix_users_email"
                ? (RegistrationOutcome.EmailTaken, null)
                : (RegistrationOutcome.UsernameTaken, null);
        }
    }
}
