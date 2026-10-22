using System.Net;
using System.Net.Http.Headers;
using Ledger.Api.Auth;
using Ledger.IntegrationTests.Infrastructure;
using Npgsql;
using Shouldly;
using Xunit;

namespace Ledger.IntegrationTests;

[Collection(LedgerTestGroup.Name)]
public sealed class SecurityTests(LedgerApiFixture fixture) : IDisposable
{
    private readonly LedgerApiClient _api = fixture.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Requests_without_token_are_rejected()
    {
        using var anonymous = fixture.Factory.CreateClient();

        using var response = await anonymous.GetAsync($"/v1/accounts/{Guid.NewGuid()}/balance", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Token_signed_with_another_key_is_rejected()
    {
        using var client = fixture.Factory.CreateClient();
        var forged = DevTokenIssuer.Issue(
            new LedgerAuthOptions { DevSigningKey = "attacker-controlled-key-0123456789abcdef" },
            "attacker",
            [Scopes.Read, Scopes.Write],
            TimeSpan.FromHours(1),
            TimeProvider.System);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", forged);

        using var response = await client.PostAsync("/v1/accounts", null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Read_only_client_cannot_write()
    {
        var account = await _api.CreateAccountAsync();
        using var reader = fixture.CreateClient("read-only", Scopes.Read);

        using var write = await reader.PostMovementAsync(account.Id, "credit", 10m);
        using var read = await reader.GetBalanceResponseAsync(account.Id);

        write.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Write_only_client_cannot_read()
    {
        var account = await _api.CreateAccountAsync();
        using var writer = fixture.CreateClient("write-only", Scopes.Write);

        using var read = await writer.GetBalanceResponseAsync(account.Id);

        read.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Health_endpoints_are_public_and_report_ready()
    {
        using var anonymous = fixture.Factory.CreateClient();

        using var live = await anonymous.GetAsync("/health/live", Ct);
        using var ready = await anonymous.GetAsync("/health/ready", Ct);

        live.StatusCode.ShouldBe(HttpStatusCode.OK);
        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Client_exceeding_its_rate_limit_receives_429_with_retry_after()
    {
        await using var limited = new LedgerApiFactory(fixture.ConnectionString, rateLimitTokens: 3);
        using var client = new LedgerApiClient(limited.CreateAuthorizedClient("noisy-client"));
        var accountId = Guid.NewGuid();

        var responses = new List<HttpResponseMessage>();
        for (var i = 0; i < 5; i++)
        {
            responses.Add(await client.GetBalanceResponseAsync(accountId));
        }

        responses.Take(3).ShouldAllBe(r => r.StatusCode == HttpStatusCode.NotFound);
        responses.Skip(3).ShouldAllBe(r => r.StatusCode == HttpStatusCode.TooManyRequests);
        responses[^1].Headers.RetryAfter.ShouldNotBeNull();

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    public void Dispose() => _api.Dispose();
}

/// <summary>Banco inacessível: a API falha rápido, de forma honesta e acionável.</summary>
public sealed class DatabaseOutageTests
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=ledger;Username=x;Password=x;Timeout=2";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Writes_fail_fast_with_503_and_retry_after_when_the_database_is_down()
    {
        await using var factory = new LedgerApiFactory(UnreachableDatabase, applyMigrations: false);
        using var client = new LedgerApiClient(factory.CreateAuthorizedClient("outage-client"));

        using var response = await client.PostMovementAsync(Guid.NewGuid(), "credit", 10m);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter.ShouldNotBeNull();
        (await LedgerApiClient.ReadErrorCodeAsync(response)).ShouldBe("database_unavailable");
    }

    [Fact]
    public async Task Readiness_reports_unhealthy_while_liveness_stays_up()
    {
        await using var factory = new LedgerApiFactory(UnreachableDatabase, applyMigrations: false);
        using var client = factory.CreateClient();

        using var live = await client.GetAsync("/health/live", Ct);
        using var ready = await client.GetAsync("/health/ready", Ct);

        live.StatusCode.ShouldBe(HttpStatusCode.OK);
        ready.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }
}

/// <summary>Invariantes garantidas pelo BANCO, independentemente do código da aplicação.</summary>
[Collection(LedgerTestGroup.Name)]
public sealed class DatabaseInvariantTests(LedgerApiFixture fixture) : IDisposable
{
    private readonly LedgerApiClient _api = fixture.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("UPDATE ledger_entries SET amount = amount + 1 WHERE id = @id")]
    [InlineData("DELETE FROM ledger_entries WHERE id = @id")]
    public async Task Ledger_entries_cannot_be_updated_or_deleted(string sql)
    {
        var account = await _api.CreateAccountAsync();
        var entry = await _api.CreditAsync(account.Id, 10m);

        var exception = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(sql, entry.Id));

        exception.SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
        (await _api.GetAllEntriesAsync(account.Id)).ShouldHaveSingleItem().ShouldBe(entry);
    }

    [Fact]
    public async Task Balance_below_overdraft_limit_is_rejected_even_bypassing_the_api()
    {
        var account = await _api.CreateAccountAsync();

        var exception = await Should.ThrowAsync<PostgresException>(
            () => ExecuteAsync("UPDATE accounts SET balance = -0.01 WHERE id = @id", account.Id));

        exception.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    private async Task ExecuteAsync(string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql.Replace("@id", "$1", StringComparison.Ordinal), connection);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        await command.ExecuteNonQueryAsync(Ct);
    }

    public void Dispose() => _api.Dispose();
}
