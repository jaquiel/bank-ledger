using Ledger.Domain;

namespace Ledger.Application;

/// <summary>
/// Casos de uso. Valida a entrada e delega às portas; não conhece HTTP nem SQL.
/// As invariantes que dependem de concorrência (saldo, ordem, idempotência) ficam na porta de escrita,
/// que é o único lugar onde podem ser garantidas atomicamente.
/// </summary>
public sealed class LedgerService(ILedgerWriter writer, ILedgerReader reader, TimeProvider timeProvider)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    /// <summary>Tolerância para relógios de clientes levemente adiantados ao consultar "saldo em T".</summary>
    private static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromMinutes(1);

    public async Task<Result<Account>> CreateAccountAsync(decimal? overdraftLimit, CancellationToken cancellationToken)
    {
        var limit = overdraftLimit ?? 0m;
        if (limit < 0 || limit > Money.MaxAmount || decimal.Round(limit, Money.DecimalPlaces) != limit)
        {
            return Result.Failure<Account>(LedgerErrors.Validation(
                "invalid_overdraft_limit", "O limite deve ser zero ou positivo, com no máximo 2 casas decimais."));
        }

        return Result.Success(await writer.CreateAccountAsync(limit, cancellationToken));
    }

    public async Task<Result<MovementOutcome>> RegisterMovementAsync(
        Guid accountId,
        EntryType type,
        decimal amount,
        string? description,
        string? idempotencyKey,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        var key = IdempotencyKey.Create(idempotencyKey);
        if (!key.IsSuccess)
        {
            return Result.Failure<MovementOutcome>(key.Error!);
        }

        var movement = Movement.Create(type, amount, description);
        if (!movement.IsSuccess)
        {
            return Result.Failure<MovementOutcome>(movement.Error!);
        }

        return await writer.RegisterMovementAsync(
            new RegisterMovementCommand(accountId, movement.Value, key.Value, context), cancellationToken);
    }

    public async Task<Result<MovementOutcome>> ReverseEntryAsync(
        Guid accountId,
        Guid entryId,
        string? idempotencyKey,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        var key = IdempotencyKey.Create(idempotencyKey);
        if (!key.IsSuccess)
        {
            return Result.Failure<MovementOutcome>(key.Error!);
        }

        return await writer.ReverseEntryAsync(new ReverseEntryCommand(accountId, entryId, key.Value, context), cancellationToken);
    }

    public async Task<Result<Account>> GetAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await reader.GetAccountAsync(accountId, cancellationToken);
        return account is null
            ? Result.Failure<Account>(LedgerErrors.AccountNotFound(accountId))
            : Result.Success(account);
    }

    /// <summary>Saldo atual (sem <paramref name="at"/>) ou a posição consolidada no instante informado.</summary>
    public async Task<Result<BalanceSnapshot>> GetBalanceAsync(Guid accountId, DateTimeOffset? at, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (at > now + ClockSkewTolerance)
        {
            return Result.Failure<BalanceSnapshot>(LedgerErrors.Validation(
                "invalid_instant", "Não é possível consultar a posição de um instante futuro."));
        }

        var snapshot = await reader.GetBalanceAsync(accountId, at, cancellationToken);
        return snapshot is null
            ? Result.Failure<BalanceSnapshot>(LedgerErrors.AccountNotFound(accountId))
            : Result.Success(snapshot);
    }

    public async Task<Result<LedgerEntry>> GetEntryAsync(Guid accountId, Guid entryId, CancellationToken cancellationToken)
    {
        var entry = await reader.GetEntryAsync(accountId, entryId, cancellationToken);
        return entry is null
            ? Result.Failure<LedgerEntry>(LedgerErrors.EntryNotFound(entryId))
            : Result.Success(entry);
    }

    public async Task<Result<StatementPage>> GetStatementAsync(
        Guid accountId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? cursor,
        int? limit,
        CancellationToken cancellationToken)
    {
        if (from is not null && to is not null && from > to)
        {
            return Result.Failure<StatementPage>(LedgerErrors.Validation("invalid_period", "'from' deve ser anterior a 'to'."));
        }

        var pageSize = limit ?? DefaultPageSize;
        if (pageSize is < 1 or > MaxPageSize)
        {
            return Result.Failure<StatementPage>(LedgerErrors.Validation(
                "invalid_limit", $"'limit' deve estar entre 1 e {MaxPageSize}."));
        }

        long? before = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            var decoded = StatementCursor.Decode(cursor);
            if (!decoded.IsSuccess)
            {
                return Result.Failure<StatementPage>(decoded.Error!);
            }

            before = decoded.Value;
        }

        var page = await reader.GetStatementAsync(new StatementQuery(accountId, from, to, before, pageSize), cancellationToken);
        return page is null
            ? Result.Failure<StatementPage>(LedgerErrors.AccountNotFound(accountId))
            : Result.Success(page);
    }
}
