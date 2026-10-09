using PayX.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddPayXDefaults();
// Payment is ready only if it can reach its own database, payx_payments.
builder.AddPostgresReadiness("Payments");

var app = builder.Build();

app.MapPayXDefaults();

app.Run();
