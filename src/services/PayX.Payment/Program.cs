using PayX.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddPayXDefaults();

var app = builder.Build();

app.MapPayXDefaults();

app.Run();
