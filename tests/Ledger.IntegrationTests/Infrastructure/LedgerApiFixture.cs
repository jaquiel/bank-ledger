using Ledger.Api.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace Ledger.IntegrationTests.Infrastructure;

/// <summary>
/// Sobe a API inteira (pipeline HTTP real) contra um PostgreSQL REAL.
/// Por padrão usa Testcontainers (exige Docker). Para usar um Postgres já existente
/// (ex.: CI sem Docker), defina LEDGER_TEST_CONNECTION_STRING.
/// Não usamos banco em memória: concorrência, locks, constraints e triggers só existem no banco real.
/// </summary>
public sealed class LedgerApiFixture : IAsyncLifetime
{
    public const string SigningKey = "integration-tests-signing-key-0123456789abcdef";

    private PostgreSqlContainer? _container;

    public string ConnectionString { get; private set; } = string.Empty;

    public LedgerApiFactory Factory { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("LEDGER_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(external))
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
        }
        else
        {
            ConnectionString = external;
        }

        Factory = new LedgerApiFactory(ConnectionString);
        _ = Factory.Server; // Força a subida do host (e a aplicação das migrations).
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public LedgerApiClient CreateClient(string clientId = "integration-tests", params string[] scopes) =>
        new(Factory.CreateAuthorizedClient(clientId, scopes));
}

[CollectionDefinition(Name)]
public sealed class LedgerTestGroup : ICollectionFixture<LedgerApiFixture>
{
    public const string Name = "ledger";
}

public sealed class LedgerApiFactory(string connectionString, int rateLimitTokens = 1_000_000, bool applyMigrations = true)
    : WebApplicationFactory<Program>
{
    public HttpClient CreateAuthorizedClient(string clientId, params string[] scopes)
    {
        var client = CreateClient();
        var token = DevTokenIssuer.Issue(
            new LedgerAuthOptions { DevSigningKey = LedgerApiFixture.SigningKey },
            clientId,
            scopes.Length == 0 ? [Scopes.Read, Scopes.Write] : scopes,
            TimeSpan.FromHours(1),
            TimeProvider.System);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Ledger", connectionString);
        builder.UseSetting("Auth:DevSigningKey", LedgerApiFixture.SigningKey);
        builder.UseSetting("Database:ApplyMigrationsOnStartup", applyMigrations ? "true" : "false");
        // Os testes de concorrência enfileiram dezenas de transações na MESMA conta de propósito.
        builder.UseSetting("Database:LockTimeoutMs", "30000");
        builder.UseSetting("Database:StatementTimeoutMs", "30000");
        builder.UseSetting("Outbox:Enabled", "false");
        builder.UseSetting("RateLimiting:TokenLimit", rateLimitTokens.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("RateLimiting:TokensPerPeriod", "1");
        builder.UseSetting("RateLimiting:ReplenishmentPeriodMs", "60000");
    }
}
