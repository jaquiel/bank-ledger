using System.Net;
using Ledger.Api.Endpoints;
using Ledger.Domain;
using Ledger.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Ledger.IntegrationTests;

/// <summary>
/// O teste mais importante do projeto: sob concorrência real na MESMA conta, o saldo nunca fica
/// inconsistente, nunca ultrapassa o limite e a sequência de lançamentos não tem lacunas nem duplicatas.
/// </summary>
[Collection(LedgerTestGroup.Name)]
public sealed class ConcurrencyTests(LedgerApiFixture fixture) : IDisposable
{
    private readonly LedgerApiClient _api = fixture.CreateClient();

    [Fact]
    public async Task Parallel_debits_on_the_same_account_never_overdraw_it()
    {
        const int attempts = 100;
        const decimal initialBalance = 1_000m;
        const decimal debitAmount = 15m;
        var account = await _api.CreateAccountAsync();
        await _api.CreditAsync(account.Id, initialBalance);

        var responses = await Task.WhenAll(Enumerable.Range(0, attempts)
            .Select(_ => _api.PostMovementAsync(account.Id, "debit", debitAmount)));

        var expectedSuccesses = (int)(initialBalance / debitAmount); // 66
        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(expectedSuccesses);
        responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity).ShouldBe(attempts - expectedSuccesses);
        (await _api.GetBalanceAsync(account.Id)).Balance.ShouldBe(initialBalance - (expectedSuccesses * debitAmount));
        AssertConsistentChain(await _api.GetAllEntriesAsync(account.Id));
        DisposeAll(responses);
    }

    [Fact]
    public async Task Parallel_mixed_credits_and_debits_produce_a_consistent_ledger()
    {
        var account = await _api.CreateAccountAsync();
        await _api.CreditAsync(account.Id, 500m);

        var responses = await Task.WhenAll(Enumerable.Range(0, 80)
            .Select(i => _api.PostMovementAsync(account.Id, i % 2 == 0 ? "credit" : "debit", 10m + (i % 7))));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.UnprocessableEntity);
        var entries = await _api.GetAllEntriesAsync(account.Id);
        AssertConsistentChain(entries);

        var finalBalance = entries.OrderBy(e => e.Sequence).Last().BalanceAfter;
        (await _api.GetBalanceAsync(account.Id)).Balance.ShouldBe(finalBalance);
        finalBalance.ShouldBe(entries.Sum(e => e.Type == EntryType.Credit ? e.Amount : -e.Amount));
        DisposeAll(responses);
    }

    [Fact]
    public async Task Concurrent_operations_on_different_accounts_do_not_interfere()
    {
        var accounts = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => _api.CreateAccountAsync()));

        var responses = await Task.WhenAll(accounts.SelectMany(a => Enumerable.Range(0, 10)
            .Select(_ => _api.PostMovementAsync(a.Id, "credit", 1m))));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Created);
        foreach (var account in accounts)
        {
            (await _api.GetBalanceAsync(account.Id)).Balance.ShouldBe(10m);
            AssertConsistentChain(await _api.GetAllEntriesAsync(account.Id));
        }

        DisposeAll(responses);
    }

    /// <summary>Cada lançamento parte exatamente do saldo deixado pelo anterior: sem lacunas, sem perda de atualização.</summary>
    private static void AssertConsistentChain(IReadOnlyList<EntryResponse> entries)
    {
        var ordered = entries.OrderBy(e => e.Sequence).ToList();
        ordered.Select(e => e.Sequence).ShouldBe(Enumerable.Range(1, ordered.Count).Select(i => (long)i));

        var running = 0m;
        foreach (var entry in ordered)
        {
            running += entry.Type == EntryType.Credit ? entry.Amount : -entry.Amount;
            entry.BalanceAfter.ShouldBe(running, $"lançamento {entry.Sequence}");
            entry.BalanceAfter.ShouldBeGreaterThanOrEqualTo(0m);
        }

        ordered.Zip(ordered.Skip(1)).ShouldAllBe(pair => pair.First.OccurredAt <= pair.Second.OccurredAt);
    }

    private static void DisposeAll(IEnumerable<HttpResponseMessage> responses)
    {
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    public void Dispose() => _api.Dispose();
}
