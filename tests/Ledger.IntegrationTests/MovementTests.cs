using System.Net;
using Ledger.Domain;
using Ledger.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Ledger.IntegrationTests;

[Collection(LedgerTestGroup.Name)]
public sealed class MovementTests(LedgerApiFixture fixture) : IDisposable
{
    private readonly LedgerApiClient _api = fixture.CreateClient();

    [Fact]
    public async Task Credits_and_debits_update_balance_and_form_a_gapless_sequence()
    {
        var account = await _api.CreateAccountAsync();

        var credit = await _api.CreditAsync(account.Id, 100.50m);
        var debit = await _api.DebitAsync(account.Id, 40.25m);

        credit.Sequence.ShouldBe(1);
        credit.BalanceAfter.ShouldBe(100.50m);
        debit.Sequence.ShouldBe(2);
        debit.Type.ShouldBe(EntryType.Debit);
        debit.BalanceAfter.ShouldBe(60.25m);
        (await _api.GetBalanceAsync(account.Id)).Balance.ShouldBe(60.25m);
    }

    [Fact]
    public async Task Debit_beyond_balance_is_rejected_and_changes_nothing()
    {
        var account = await _api.CreateAccountAsync();
        await _api.CreditAsync(account.Id, 50m);

        using var response = await _api.PostMovementAsync(account.Id, "debit", 50.01m);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await LedgerApiClient.ReadErrorCodeAsync(response)).ShouldBe("insufficient_funds");
        (await _api.GetBalanceAsync(account.Id)).Balance.ShouldBe(50m);
        (await _api.GetAllEntriesAsync(account.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Overdraft_limit_allows_negative_balance_up_to_the_limit_only()
    {
        var account = await _api.CreateAccountAsync(overdraftLimit: 100m);

        var debit = await _api.DebitAsync(account.Id, 100m);
        using var beyond = await _api.PostMovementAsync(account.Id, "debit", 0.01m);

        debit.BalanceAfter.ShouldBe(-100m);
        beyond.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    [InlineData(10.001)]
    public async Task Invalid_amount_is_rejected_with_400(decimal amount)
    {
        var account = await _api.CreateAccountAsync();

        using var response = await _api.PostMovementAsync(account.Id, "credit", amount);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await LedgerApiClient.ReadErrorCodeAsync(response)).ShouldBe("invalid_amount");
    }

    [Fact]
    public async Task Unknown_entry_type_is_rejected_with_400()
    {
        var account = await _api.CreateAccountAsync();

        using var response = await _api.PostMovementAsync(account.Id, "transfer", 10m);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Movement_on_unknown_account_returns_404()
    {
        using var response = await _api.PostMovementAsync(Guid.NewGuid(), "credit", 10m);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await LedgerApiClient.ReadErrorCodeAsync(response)).ShouldBe("account_not_found");
    }

    [Fact]
    public async Task Created_entry_can_be_read_back_from_its_location()
    {
        var account = await _api.CreateAccountAsync();
        using var created = await _api.PostMovementAsync(account.Id, "credit", 12.34m, description: "Pix recebido");

        using var fetched = await _api.Http.GetAsync(created.Headers.Location, TestContext.Current.CancellationToken);
        var entry = await LedgerApiClient.ReadEntryAsync(fetched);

        fetched.StatusCode.ShouldBe(HttpStatusCode.OK);
        entry.Amount.ShouldBe(12.34m);
        entry.Description.ShouldBe("Pix recebido");
    }

    public void Dispose() => _api.Dispose();
}
