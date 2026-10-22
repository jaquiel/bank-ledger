using Ledger.Domain;

namespace Ledger.Infrastructure.Persistence;

// Linhas de leitura mapeadas pelo Dapper. Ficam separadas do domínio de propósito: o formato do
// banco (snake_case, timestamptz como DateTime UTC, enums como texto) não vaza para dentro do domínio.

public sealed class AccountRow
{
    public const string Columns =
        "id AS Id, currency AS Currency, balance AS Balance, overdraft_limit AS OverdraftLimit, " +
        "status AS Status, version AS Version, created_at AS CreatedAt";

    public Guid Id { get; init; }

    public string Currency { get; init; } = Money.Currency;

    public decimal Balance { get; init; }

    public decimal OverdraftLimit { get; init; }

    public string Status { get; init; } = "active";

    public long Version { get; init; }

    public DateTime CreatedAt { get; init; }

    public Account ToDomain() => new(
        Id,
        Currency.Trim(),
        Balance,
        OverdraftLimit,
        Status == "active" ? AccountStatus.Active : AccountStatus.Blocked,
        Version,
        DbTime.ToOffset(CreatedAt));
}

public sealed class EntryRow
{
    public const string Columns =
        "id AS Id, account_id AS AccountId, sequence AS Sequence, type AS Type, amount AS Amount, " +
        "balance_after AS BalanceAfter, description AS Description, reversal_of AS ReversalOf, occurred_at AS OccurredAt";

    public Guid Id { get; init; }

    public Guid AccountId { get; init; }

    public long Sequence { get; init; }

    public string Type { get; init; } = string.Empty;

    public decimal Amount { get; init; }

    public decimal BalanceAfter { get; init; }

    public string? Description { get; init; }

    public Guid? ReversalOf { get; init; }

    public DateTime OccurredAt { get; init; }

    public LedgerEntry ToDomain() => new(
        Id,
        AccountId,
        Sequence,
        EntryTypeExtensions.FromCode(Type),
        Amount,
        BalanceAfter,
        Description,
        ReversalOf,
        DbTime.ToOffset(OccurredAt));
}

public sealed class AppliedBalanceRow
{
    public decimal Balance { get; init; }

    public long Version { get; init; }

    public DateTime OccurredAt { get; init; }
}

public sealed class IdempotencyRow
{
    public string RequestHash { get; init; } = string.Empty;

    public Guid? EntryId { get; init; }
}

public sealed class BalanceRow
{
    public string Currency { get; init; } = Money.Currency;

    public decimal Balance { get; init; }

    public long LastSequence { get; init; }

    public DateTime AsOf { get; init; }
}

internal static class DbTime
{
    /// <summary>timestamptz chega do Npgsql como DateTime UTC.</summary>
    public static DateTimeOffset ToOffset(DateTime utc) =>
        new(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeSpan.Zero);
}
