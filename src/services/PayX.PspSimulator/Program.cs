using PayX.ServiceDefaults;

// The outside world, simulated: one service standing in for the payment
// gateway/PSP (Stripe, Adyen), the card networks and the issuing banks.
// PayX talks to it the way it would talk to a real provider, over HTTP, and
// never reaches into it otherwise. No Gateway route: customers never call a
// PSP directly; only PayX's own services do.
var builder = WebApplication.CreateBuilder(args);

builder.AddPayXDefaults();

var app = builder.Build();

app.MapPayXDefaults();

app.Run();
