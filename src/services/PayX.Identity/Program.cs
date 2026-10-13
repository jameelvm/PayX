using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PayX.Identity.Domain;
using PayX.Identity.Repositories;
using PayX.Identity.Services;
using PayX.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddPayXDefaults();
builder.AddPostgresReadiness("Identity");

builder.Services.AddDbContext<IdentityDbContext>(options => options
    .UseNpgsql(builder.Configuration.GetConnectionString("Identity"))
    // users.password_hash, not Users.PasswordHash: Postgres folds unquoted
    // names to lower case, so snake_case is the only style you can type in
    // psql without quoting every identifier.
    .UseSnakeCaseNamingConvention());

builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddScoped<UserRegistration>();

var app = builder.Build();

// Apply pending migrations at startup, connecting as payx_identity, the role
// that owns this database (Module 1.2), so it can create its own tables and
// no admin account is involved. Fine for local development. In production,
// migrations run once as a separate deploy step before the new version
// starts: three instances starting together would otherwise all try to
// migrate at once, and a failed migration would crash-loop every instance.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.MigrateAsync();
}

app.MapPayXDefaults();

app.Run();
