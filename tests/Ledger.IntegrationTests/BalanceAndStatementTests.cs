using System.Net;
using Ledger.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Ledger.IntegrationTests;

[Collection(LedgerTestGroup.Name)]
public sealed class BalanceAndStatementTests(LedgerApiFixture fixture) : IDisposable
{
    private readonly LedgerApiClient _api = fixture.CreateClient();

    [Fact]
    public async Task Balance_at_an_instant_reflects_only_entries_up_to_that_instant()
    {
        var account = await _api.CreateAccountAsync();
        var first = await _api.CreditAsync(account.Id, 100m);
        var second = await _api.CreditAsync(account.Id, 50m);
        var third = await _api.DebitAsync(account.Id, 30m);

        (await _api.GetBalanceAsync(account.Id, first.OccurredAt.AddTicks(-10))).Balance.ShouldBe(0m);
        (await _api.GetBalanceAsync(account.Id, first.OccurredAt)).Balance.ShouldBe(100m);
        (await _api.GetBalanceAsync(account.Id, second.OccurredAt)).Balance.ShouldBe(150m);
        (await _api.GetBalanceAsync(account.Id, Midpoint(second.OccurredAt, third.OccurredAt))).Balance.ShouldBe(150m);
        (await _api.GetBalanceAsync(account.Id, third.OccurredAt)).Balance.ShouldBe(120m);

        var current = await _api.GetBalanceAsync(account.Id);
        current.Balance.ShouldBe(120m);
        current.LastSequence.ShouldBe(3);
    }

    [Fact]
    public async Task Balance_at_an_instant_is_stable_after_later_movements()
    {
        var account = await _api.CreateAccountAsync();
        var entry = await _api.CreditAsync(account.Id, 80m);
        var before = await _api.GetBalanceAsync(account.Id, entry.OccurredAt);

        await _api.DebitAsync(account.Id, 80m);

        (await _api.GetBalanceAsync(account.Id, entry.OccurredAt)).ShouldBe(before);
    }

    [Fact]
    public async Task Instants_returned_by_the_api_can_be_sent_back_unescaped_in_the_query_string()
    {
        var account = await _api.CreateAccountAsync();
        using var created = await _api.PostMovementAsync(account.Id, "credit", 42m);
        using var json = System.Text.Json.JsonDocument.Parse(await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var occurredAt = json.RootElement.GetProperty("occurredAt").GetString()!;

        using var response = await _api.Http.GetAsync($"/v1/accounts/{account.Id}/balance?at={occurredAt}", TestContext.Current.CancellationToken);

        occurredAt.ShouldEndWith("Z");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Balance_in_the_future_is_rejected()
    {
        var account = await _api.CreateAccountAsync();

        using var response = await _api.GetBalanceResponseAsync(account.Id, DateTimeOffset.UtcNow.AddDays(1));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Balance_of_unknown_account_returns_404()
    {
        using var response = await _api.GetBalanceResponseAsync(Guid.NewGuid());

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Statement_is_paginated_by_cursor_newest_first_without_gaps_or_duplicates()
    {
        var account = await _api.CreateAccountAsync();
        for (var i = 1; i <= 25; i++)
        {
            await _api.CreditAsync(account.Id, i);
        }

        var page1 = await _api.GetStatementAsync(account.Id, "?limit=10");
        var page2 = await _api.GetStatementAsync(account.Id, $"?limit=10&cursor={page1.NextCursor}");
        var page3 = await _api.GetStatementAsync(account.Id, $"?limit=10&cursor={page2.NextCursor}");

        page1.Items.Select(e => e.Sequence).ShouldBe(Enumerable.Range(16, 10).Reverse().Select(i => (long)i));
        page2.Items.Select(e => e.Sequence).ShouldBe(Enumerable.Range(6, 10).Reverse().Select(i => (long)i));
        page3.Items.Select(e => e.Sequence).ShouldBe(Enumerable.Range(1, 5).Reverse().Select(i => (long)i));
        page3.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task Statement_filters_by_period()
    {
        var account = await _api.CreateAccountAsync();
        await _api.CreditAsync(account.Id, 1m);
        var middle = await _api.CreditAsync(account.Id, 2m);
        await _api.CreditAsync(account.Id, 3m);
        var instant = Uri.EscapeDataString(middle.OccurredAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

        var page = await _api.GetStatementAsync(account.Id, $"?from={instant}&to={instant}");

        page.Items.ShouldHaveSingleItem().Id.ShouldBe(middle.Id);
    }

    [Theory]
    [InlineData("?cursor=invalido!")]
    [InlineData("?limit=0")]
    [InlineData("?limit=1000")]
    public async Task Statement_rejects_invalid_parameters(string query)
    {
        var account = await _api.CreateAccountAsync();

        using var response = await _api.Http.GetAsync($"/v1/accounts/{account.Id}/movements{query}", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static DateTimeOffset Midpoint(DateTimeOffset a, DateTimeOffset b) => a + ((b - a) / 2);

    public void Dispose() => _api.Dispose();
}
