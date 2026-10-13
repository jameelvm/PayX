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
    /// A one-way hash, never the password itself. The hashing algorithm
    /// arrives in Module 2.2; this module only reserves the column.
    /// </summary>
    public string PasswordHash { get; private set; } = null!;

    public UserRole Role { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public User(string username, string email, string passwordHash, UserRole role)
    {
        // UUID v7: the first 48 bits are a timestamp, so new ids sort after
        // old ones. Inserts land at the end of the primary-key index instead
        // of at random positions (as with v4), which keeps the index compact
        // as the table grows. Ids still can't be guessed from one another.
        Id = Guid.CreateVersion7();
        Username = username;
        Email = email;
        PasswordHash = passwordHash;
        Role = role;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    // For EF Core when it materialises a row; not for application code.
    private User() { }
}

public enum UserRole
{
    Customer,   // pays
    Merchant,   // gets paid; has a wallet (Phase 7)
    Ops,        // runs reconciliation and disputes (Phases 8–9)
}
