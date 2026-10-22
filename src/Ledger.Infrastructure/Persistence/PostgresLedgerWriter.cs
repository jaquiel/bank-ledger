using System.Data;
using System.Text.Json;
using Dapper;
using Ledger.Application;
using Ledger.Domain;
using Npgsql;

namespace Ledger.Infrastructure.Persistence;

/// <summary>
/// Caminho crítico de escrita. Cada operação é UMA transação Read Committed que:
/// <list type="number">
///   <item>reserva a Idempotency-Key (índice único serializa requisições duplicadas);</item>
///   <item>aplica o delta com um UPDATE condicional atômico (lock de linha só na conta afetada);</item>
///   <item>grava o lançamento imutável com sequence = nova versão da conta e balance_after;</item>
///   <item>vincula a chave ao lançamento e grava o evento no outbox.</item>
/// </list>
/// Não há ler-modificar-escrever: a regra de saldo é avaliada pelo próprio banco sob o lock da linha,
/// então não existe janela de corrida entre "ler saldo" e "gravar saldo".
/// </summary>
public sealed class PostgresLedgerWriter(NpgsqlDataSource dataSource, TransientRetry retry) : ILedgerWriter
{
    private const string EntryRecordedEventType = "ledger.entry_recorded.v1";

    private delegate Task<Result<LedgerEntry>> TransactionalOperation(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken);

    public async Task<Account> CreateAccountAsync(decimal overdraftLimit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleAsync<AccountRow>(new CommandDefinition(
            $"INSERT INTO accounts (id, overdraft_limit) VALUES (@Id, @OverdraftLimit) RETURNING {AccountRow.Columns}",
            new { Id = Guid.CreateVersion7(), OverdraftLimit = overdraftLimit },
            cancellationToken: cancellationToken));
        return row.ToDomain();
    }

    public Task<Result<MovementOutcome>> RegisterMovementAsync(RegisterMovementCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var requestHash = RequestFingerprint.ForMovement(command.AccountId, command.Movement);
        var append = new AppendRequest(
            command.AccountId,
            command.Movement.Type,
            command.Movement.Amount.Amount,
            command.Movement.Description,
            ReversalOf: null,
            command.Context);

        return retry.ExecuteAsync(
            ct => ExecuteIdempotentAsync(command.Context, command.IdempotencyKey, requestHash, (c, t, token) => AppendAsync(c, t, append, token), ct),
            cancellationToken);
    }

    public Task<Result<MovementOutcome>> ReverseEntryAsync(ReverseEntryCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var requestHash = RequestFingerprint.ForReversal(command.AccountId, command.EntryId);

        return retry.ExecuteAsync(
            ct => ExecuteIdempotentAsync(command.Context, command.IdempotencyKey, requestHash, (c, t, token) => ReverseAsync(c, t, command, token), ct),
            cancellationToken);
    }

    private async Task<Result<MovementOutcome>> ExecuteIdempotentAsync(
        OperationContext context,
        IdempotencyKey key,
        string requestHash,
        TransactionalOperation operation,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // 1. Reserva a chave. Se outra transação concorrente já inseriu a mesma chave, este INSERT ESPERA
        //    ela terminar: se ela commitou, não inserimos nada e caímos no replay; se abortou, a chave é nossa.
        var claimed = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO idempotency_keys (client_id, key, request_hash)
            VALUES (@ClientId, @Key, @RequestHash)
            ON CONFLICT (client_id, key) DO NOTHING
            """,
            new { context.ClientId, Key = key.Value, RequestHash = requestHash },
            transaction,
            cancellationToken: cancellationToken)) == 1;

        if (!claimed)
        {
            var replay = await ReplayAsync(connection, transaction, context.ClientId, key, requestHash, cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
            return replay;
        }

        // 2-4. Executa a operação de negócio na mesma transação.
        var result = await operation(connection, transaction, cancellationToken);
        if (!result.IsSuccess)
        {
            // Rejeições de negócio não são persistidas: nada foi movimentado e a chave fica livre.
            await transaction.RollbackAsync(cancellationToken);
            return Result.Failure<MovementOutcome>(result.Error!);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE idempotency_keys SET entry_id = @EntryId WHERE client_id = @ClientId AND key = @Key",
            new { EntryId = result.Value.Id, context.ClientId, Key = key.Value },
            transaction,
            cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
        return Result.Success(new MovementOutcome(result.Value, Replayed: false));
    }

    private static async Task<Result<MovementOutcome>> ReplayAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string clientId,
        IdempotencyKey key,
        string requestHash,
        CancellationToken cancellationToken)
    {
        var existing = await connection.QuerySingleOrDefaultAsync<IdempotencyRow>(new CommandDefinition(
            "SELECT request_hash AS RequestHash, entry_id AS EntryId FROM idempotency_keys WHERE client_id = @ClientId AND key = @Key",
            new { ClientId = clientId, Key = key.Value },
            transaction,
            cancellationToken: cancellationToken));

        if (existing?.EntryId is null)
        {
            return Result.Failure<MovementOutcome>(LedgerErrors.IdempotencyKeyInProgress());
        }

        if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return Result.Failure<MovementOutcome>(LedgerErrors.IdempotencyKeyReused());
        }

        var entry = await connection.QuerySingleAsync<EntryRow>(new CommandDefinition(
            $"SELECT {EntryRow.Columns} FROM ledger_entries WHERE id = @Id",
            new { Id = existing.EntryId.Value },
            transaction,
            cancellationToken: cancellationToken));

        return Result.Success(new MovementOutcome(entry.ToDomain(), Replayed: true));
    }

    private static async Task<Result<LedgerEntry>> ReverseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ReverseEntryCommand command,
        CancellationToken cancellationToken)
    {
        var original = await connection.QuerySingleOrDefaultAsync<EntryRow>(new CommandDefinition(
            $"SELECT {EntryRow.Columns} FROM ledger_entries WHERE id = @EntryId AND account_id = @AccountId",
            new { command.EntryId, command.AccountId },
            transaction,
            cancellationToken: cancellationToken));

        if (original is null)
        {
            return Result.Failure<LedgerEntry>(LedgerErrors.EntryNotFound(command.EntryId));
        }

        if (original.ReversalOf is not null)
        {
            return Result.Failure<LedgerEntry>(LedgerErrors.CannotReverseReversal(command.EntryId));
        }

        var alreadyReversed = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM ledger_entries WHERE reversal_of = @EntryId)",
            new { command.EntryId },
            transaction,
            cancellationToken: cancellationToken));

        if (alreadyReversed)
        {
            return Result.Failure<LedgerEntry>(LedgerErrors.EntryAlreadyReversed(command.EntryId));
        }

        var reversal = new AppendRequest(
            command.AccountId,
            EntryTypeExtensions.FromCode(original.Type).Opposite(),
            original.Amount,
            $"Estorno do lançamento {original.Id}",
            original.Id,
            command.Context);

        try
        {
            return await AppendAsync(connection, transaction, reversal, cancellationToken);
        }
        catch (PostgresException ex) when (ex is { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "uq_ledger_entries_reversal_of" })
        {
            // Dois estornos concorrentes (com chaves diferentes) do mesmo lançamento: o índice único decide.
            return Result.Failure<LedgerEntry>(LedgerErrors.EntryAlreadyReversed(command.EntryId));
        }
    }

    private static async Task<Result<LedgerEntry>> AppendAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AppendRequest request,
        CancellationToken cancellationToken)
    {
        var delta = request.Type == EntryType.Credit ? request.Amount : -request.Amount;

        // O coração do desenho: regra de saldo + incremento de versão em um único statement.
        // O lock de linha serializa apenas operações da MESMA conta. clock_timestamp() é lido depois
        // de obtido o lock, então occurred_at acompanha a ordem de sequence dentro da conta.
        var applied = await connection.QuerySingleOrDefaultAsync<AppliedBalanceRow>(new CommandDefinition(
            """
            UPDATE accounts
               SET balance    = balance + @Delta,
                   version    = version + 1,
                   updated_at = clock_timestamp()
             WHERE id = @AccountId
               AND status = 'active'
               AND balance + @Delta >= -overdraft_limit
            RETURNING balance AS Balance, version AS Version, updated_at AS OccurredAt
            """,
            new { Delta = delta, request.AccountId },
            transaction,
            cancellationToken: cancellationToken));

        if (applied is null)
        {
            return Result.Failure<LedgerEntry>(await DiagnoseRejectionAsync(connection, transaction, request.AccountId, cancellationToken));
        }

        var entry = new LedgerEntry(
            Guid.CreateVersion7(),
            request.AccountId,
            applied.Version,
            request.Type,
            request.Amount,
            applied.Balance,
            request.Description,
            request.ReversalOf,
            DbTime.ToOffset(applied.OccurredAt));

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ledger_entries
                (id, account_id, sequence, type, amount, balance_after, description, reversal_of, occurred_at, created_by, correlation_id)
            VALUES
                (@Id, @AccountId, @Sequence, @Type, @Amount, @BalanceAfter, @Description, @ReversalOf, @OccurredAt, @CreatedBy, @CorrelationId)
            """,
            new
            {
                entry.Id,
                entry.AccountId,
                entry.Sequence,
                Type = entry.Type.ToCode(),
                entry.Amount,
                entry.BalanceAfter,
                entry.Description,
                entry.ReversalOf,
                OccurredAt = entry.OccurredAt.UtcDateTime,
                CreatedBy = request.Context.ClientId,
                request.Context.CorrelationId,
            },
            transaction,
            cancellationToken: cancellationToken));

        // Evento de integração sem dados pessoais (a descrição livre fica de fora de propósito).
        var payload = JsonSerializer.Serialize(new
        {
            entryId = entry.Id,
            accountId = entry.AccountId,
            sequence = entry.Sequence,
            type = entry.Type.ToCode(),
            amount = entry.Amount,
            balanceAfter = entry.BalanceAfter,
            reversalOf = entry.ReversalOf,
            occurredAt = entry.OccurredAt,
        });

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO outbox_messages (id, type, payload, occurred_at) VALUES (@Id, @Type, @Payload::jsonb, @OccurredAt)",
            new { Id = Guid.CreateVersion7(), Type = EntryRecordedEventType, Payload = payload, OccurredAt = entry.OccurredAt.UtcDateTime },
            transaction,
            cancellationToken: cancellationToken));

        return Result.Success(entry);
    }

    /// <summary>Executado só no caminho de falha: descobre por que o UPDATE condicional não afetou linhas.</summary>
    private static async Task<DomainError> DiagnoseRejectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var status = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT status FROM accounts WHERE id = @AccountId",
            new { AccountId = accountId },
            transaction,
            cancellationToken: cancellationToken));

        return status switch
        {
            null => LedgerErrors.AccountNotFound(accountId),
            "active" => LedgerErrors.InsufficientFunds(accountId),
            _ => LedgerErrors.AccountInactive(accountId),
        };
    }

    private sealed record AppendRequest(
        Guid AccountId,
        EntryType Type,
        decimal Amount,
        string? Description,
        Guid? ReversalOf,
        OperationContext Context);
}
