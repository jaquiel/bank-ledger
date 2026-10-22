namespace Ledger.Domain;

public enum AccountStatus
{
    Active,
    Blocked,
}

/// <summary>Estado corrente de uma conta. O saldo é uma materialização do ledger, mantida na mesma transação.</summary>
public sealed record Account(
    Guid Id,
    string Currency,
    decimal Balance,
    decimal OverdraftLimit,
    AccountStatus Status,
    long Version,
    DateTimeOffset CreatedAt);

/// <summary>
/// Lançamento imutável do livro-razão. <see cref="Sequence"/> é a ordem total por conta (fonte de verdade da ordenação);
/// <see cref="BalanceAfter"/> permite responder "saldo em T" com uma única leitura indexada.
/// </summary>
public sealed record LedgerEntry(
    Guid Id,
    Guid AccountId,
    long Sequence,
    EntryType Type,
    decimal Amount,
    decimal BalanceAfter,
    string? Description,
    Guid? ReversalOf,
    DateTimeOffset OccurredAt);
