using Microsoft.EntityFrameworkCore;
using PayX.Identity.Domain;

namespace PayX.Identity.Repositories;

/// <summary>
/// Identity's view of its own database, <c>payx_identity</c>. No other service
/// has this class, a connection string to that database, or (since Module 1.2)
/// permission to connect to it.
/// </summary>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(user =>
        {
            user.ToTable("users", table =>
            {
                // The database is the last line of defence. Module 2.2 will
                // lower-case input before saving, but if any code path ever
                // forgets, these refuse the row instead of letting "Priya@x.com"
                // and "priya@x.com" become two accounts. Lower-case-only plus a
                // unique index is what makes uniqueness case-insensitive.
                table.HasCheckConstraint("ck_users_username_lowercase", "username = lower(username)");
                table.HasCheckConstraint("ck_users_email_lowercase", "email = lower(email)");
                table.HasCheckConstraint("ck_users_role", "role IN ('Customer', 'Merchant', 'Ops')");
            });

            user.HasKey(u => u.Id);

            user.Property(u => u.Username).HasMaxLength(32);
            user.Property(u => u.Email).HasMaxLength(254);   // the longest valid email address
            user.Property(u => u.PasswordHash).HasMaxLength(256);

            // Stored as text ("Merchant"), not a number: readable in psql, and
            // reordering the enum in code can't silently change anyone's role.
            user.Property(u => u.Role).HasConversion<string>().HasMaxLength(16);

            user.HasIndex(u => u.Username).IsUnique();
            user.HasIndex(u => u.Email).IsUnique();
        });
    }
}
