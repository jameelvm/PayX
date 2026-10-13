namespace PayX.Identity.Domain;

/// <summary>
/// Someone who can log in to PayX. Doc ch.2, <c>registerUser(username, email,
/// password)</c>: these are exactly the fields that API needs to store.
/// </summary>
public sealed class User
{
    public Guid Id { get; private set; }

    /// <summary>Stored lower-case; the database refuses anything else.</summary>
    public string Username { get; private set; } = null!;

    /// <summary>Stored lower-case; the database refuses anything else.</summary>
    public string Email { get; private set; } = null!;

    /// <summary>
    /// A slow, salted, one-way hash, never the password itself. Set through
    /// <see cref="SetPasswordHash"/>; the column is NOT NULL, so a user saved
    /// without one is refused by the database.
    /// </summary>
    public string PasswordHash { get; private set; } = null!;

    public UserRole Role { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public User(string username, string email, UserRole role)
    {
        // UUID v7: the first 48 bits are a timestamp, so new ids sort after
        // old ones. Inserts land at the end of the primary-key index instead
        // of at random positions (as with v4), which keeps the index compact
        // as the table grows. Ids still can't be guessed from one another.
        Id = Guid.CreateVersion7();
        Username = username;
        Email = email;
        Role = role;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    // Separate from the constructor because the hasher takes the user as an
    // argument, and because changing a password later is the same operation.
    public void SetPasswordHash(string passwordHash) => PasswordHash = passwordHash;

    // For EF Core when it materialises a row; not for application code.
    private User() { }
}

public enum UserRole
{
    Customer,   // pays
    Merchant,   // gets paid; has a wallet (Phase 7)
    Ops,        // runs reconciliation and disputes (Phases 8–9)
}
