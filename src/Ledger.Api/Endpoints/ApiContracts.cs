using Ledger.Application;
using Ledger.Domain;

namespace Ledger.Api.Endpoints;

public sealed record CreateAccountRequest(decimal? OverdraftLimit);

public sealed record RegisterMovementRequest(EntryType? Type, decimal? Amount, string? Description);

public sealed record DevTokenRequest(string? ClientId, IReadOnlyList<string>? Scopes);

public sealed record DevTokenResponse(string AccessToken, string TokenType, int ExpiresIn);

public sealed record AccountResponse(
    Guid Id,
    string Currency,
    decimal Balance,
    decimal OverdraftLimit,
    AccountStatus Status,
    DateTimeOffset CreatedAt)
{
    public static AccountResponse From(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return new(account.Id, account.Currency, account.Balance, account.OverdraftLimit, account.Status, account.CreatedAt);
    }
}

public sealed record EntryResponse(
    Guid Id,
    Guid AccountId,
    long Sequence,
    EntryType Type,
    decimal Amount,
    decimal BalanceAfter,
    string? Description,
    Guid? ReversalOf,
    DateTimeOffset OccurredAt)
{
    public static EntryResponse From(LedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new(entry.Id, entry.AccountId, entry.Sequence, entry.Type, entry.Amount, entry.BalanceAfter,
            entry.Description, entry.ReversalOf, entry.OccurredAt);
    }
}

/// <summary>Posição consolidada. <c>LastSequence</c> identifica o último lançamento considerado no saldo.</summary>
public sealed record BalanceResponse(Guid AccountId, string Currency, decimal Balance, DateTimeOffset AsOf, long LastSequence)
{
    public static BalanceResponse From(BalanceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(snapshot.AccountId, snapshot.Currency, snapshot.Balance, snapshot.AsOf, snapshot.LastSequence);
    }
}

public sealed record StatementResponse(IReadOnlyList<EntryResponse> Items, string? NextCursor);
