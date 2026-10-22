using Ledger.Api;
using Ledger.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddLedgerInfrastructure(builder.Configuration)
    .AddLedgerApi(builder.Configuration);

var app = builder.Build();
app.UseLedgerApi();
await app.RunAsync();
