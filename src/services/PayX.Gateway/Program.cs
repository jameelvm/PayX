using PayX.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddPayXDefaults();

// Routes, clusters and health-check policy all live in appsettings.json:
// which URL goes to which service is configuration, not code.
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

app.MapPayXDefaults();
app.MapReverseProxy();

app.Run();
