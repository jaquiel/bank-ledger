using System.Net;
using Ledger.Domain;
using Ledger.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Ledger.IntegrationTests;

[Collection(LedgerTestGroup.Name)]
public sealed class ReversalTests(LedgerApiFixture fixture) : IDisposable
{
    private readonly LedgerApiClient _api = fixture.CreateClient();

    [Fact]
    public async Task Reversal_is_a_compensating_entry_and_the_original_stays_untouched()
    {
        var account = await _api.CreateAccountAsync();
        var credit = await _api.CreditAsync(account.Id, 70m);

        using var response = await _api.ReverseAsync(account.Id, credit.Id);
        var reversal = await LedgerApiClient.ReadEntryAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        reversal.Type.ShouldBe(EntryType.Debit);
        reversal.Amount.ShouldBe(70m);
        reversal.ReversalOf.ShouldBe(credit.Id);
        reversal.BalanceAfter.ShouldBe(0m);

        var entries = await _api.GetAllEntriesAsync(account.Id);
        entries.Count.ShouldBe(2);
        entries.Single(e => e.Id == credit.Id).ShouldBe(credit);
    }

    [Fact]
    public async Task An_entry_can_only_be_reversed_once()
    {
        var account = await _api.CreateAccountAsync();
        var credit = await _api.CreditAsync(account.Id, 10m);

        using var first = await _api.ReverseAsync(account.Id, credit.Id);
        using var second = await _api.ReverseAsync(account.Id, credit.Id);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await LedgerApiClient.ReadErrorCodeAsync(second)).ShouldBe("entry_already_reversed");
    }

    [Fact]
    public async Task Concurrent_reversals_of_the_same_entry_apply_exactly_once()
    {
        var account = await _api.CreateAccountAsync();
        await _api.CreditAsync(account.Id, 100m);
        var debit = await _api.DebitAsync(account.Id, 40m);

        var responses = await Task.WhenAll(Enumerable.Range(0, 15).Select(_ => _api.ReverseAsync(account.Id, debit.Id)));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(14);
        (await _api.GetBalanceAsync(account.Id)).Balance.ShouldBe(100m);

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task A_reversal_cannot_be_reversed()
    {
        var account = await _api.CreateAccountAsync();
        var credit = await _api.CreditAsync(account.Id, 10m);
        using var reversalResponse = await _api.ReverseAsync(account.Id, credit.Id);
        var reversal = await LedgerApiClient.ReadEntryAsync(reversalResponse);

        using var response = await _api.ReverseAsync(account.Id, reversal.Id);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await LedgerApiClient.ReadErrorCodeAsync(response)).ShouldBe("cannot_reverse_reversal");
    }

    [Fact]
    public async Task Reversing_a_credit_already_spent_respects_the_balance_rule()
    {
        var account = await _api.CreateAccountAsync();
        var credit = await _api.CreditAsync(account.Id, 50m);
        await _api.DebitAsync(account.Id, 50m);

        using var response = await _api.ReverseAsync(account.Id, credit.Id);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await LedgerApiClient.ReadErrorCodeAsync(response)).ShouldBe("insufficient_funds");
    }

    [Fact]
    public async Task Reversing_an_entry_of_another_account_returns_404()
    {
        var accountA = await _api.CreateAccountAsync();
        var accountB = await _api.CreateAccountAsync();
        var credit = await _api.CreditAsync(accountA.Id, 10m);

        using var response = await _api.ReverseAsync(accountB.Id, credit.Id);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    public void Dispose() => _api.Dispose();
}
