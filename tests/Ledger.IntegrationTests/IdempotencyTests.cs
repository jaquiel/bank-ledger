using System.Net;
using Ledger.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Ledger.IntegrationTests;

[Collection(LedgerTestGroup.Name)]
public sealed class IdempotencyTests(LedgerApiFixture fixture) : IDisposable
{
    private readonly LedgerApiClient _api = fixture.CreateClient();

    [Fact]
    public async Task Retrying_with_the_same_key_replays_the_original_result_without_new_effect()
    {
        var account = await _api.CreateAccountAsync();
        var key = Guid.NewGuid().ToString();

        using var first = await _api.PostMovementAsync(account.Id, "credit", 100m, key);
        using var retry = await _api.PostMovementAsync(account.Id, "credit", 100m, key);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        retry.Headers.GetValues("Idempotent-Replayed").ShouldBe(["true"]);
        (await LedgerApiClient.ReadEntryAsync(retry)).Id.ShouldBe((await LedgerApiClient.ReadEntryAsync(first)).Id);
        (await _api.GetBalanceAsync(account.Id)).Balance.ShouldBe(100m);
    }

    [Fact]
    public async Task Concurrent_requests_with_the_same_key_create_exactly_one_entry()
    {
        var account = await _api.CreateAccountAsync();
        var key = Guid.NewGuid().ToString();

        var responses = await Task.WhenAll(Enumerable.Range(0, 25)
            .Select(_ => _api.PostMovementAsync(account.Id, "credit", 10m, key)));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(24);
        var ids = await Task.WhenAll(responses.Select(LedgerApiClient.ReadEntryAsync));
        ids.Select(e => e.Id).Distinct().Count().ShouldBe(1);
        (await _api.GetBalanceAsync(account.Id)).Balance.ShouldBe(10m);

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task Reusing_a_key_with_a_different_payload_is_rejected()
    {
        var account = await _api.CreateAccountAsync();
        var key = Guid.NewGuid().ToString();
        using var first = await _api.PostMovementAsync(account.Id, "credit", 100m, key);

        using var reused = await _api.PostMovementAsync(account.Id, "credit", 999m, key);

        reused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await LedgerApiClient.ReadErrorCodeAsync(reused)).ShouldBe("idempotency_key_reused");
        (await _api.GetBalanceAsync(account.Id)).Balance.ShouldBe(100m);
    }

    [Fact]
    public async Task Keys_are_scoped_per_client()
    {
        var account = await _api.CreateAccountAsync();
        using var otherClient = fixture.CreateClient("another-client");
        var key = Guid.NewGuid().ToString();

        using var fromA = await _api.PostMovementAsync(account.Id, "credit", 10m, key);
        using var fromB = await otherClient.PostMovementAsync(account.Id, "credit", 10m, key);

        fromA.StatusCode.ShouldBe(HttpStatusCode.Created);
        fromB.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await _api.GetBalanceAsync(account.Id)).Balance.ShouldBe(20m);
    }

    [Fact]
    public async Task A_rejected_operation_does_not_consume_its_key()
    {
        var account = await _api.CreateAccountAsync();
        var key = Guid.NewGuid().ToString();

        using var rejected = await _api.PostMovementAsync(account.Id, "debit", 50m, key);
        await _api.CreditAsync(account.Id, 100m);
        using var retried = await _api.PostMovementAsync(account.Id, "debit", 50m, key);

        rejected.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        retried.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await _api.GetBalanceAsync(account.Id)).Balance.ShouldBe(50m);
    }

    [Fact]
    public async Task Missing_idempotency_key_is_rejected()
    {
        var account = await _api.CreateAccountAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/accounts/{account.Id}/movements")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { type = "credit", amount = 10m }),
        };

        using var response = await _api.Http.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await LedgerApiClient.ReadErrorCodeAsync(response)).ShouldBe("idempotency_key_required");
    }

    public void Dispose() => _api.Dispose();
}
