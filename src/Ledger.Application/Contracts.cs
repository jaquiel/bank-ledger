using Ledger.Domain;

namespace Ledger.Application;

/// <summary>Identidade de quem executa a operação (para escopo de idempotência e auditoria).</summary>
public sealed record OperationContext(string ClientId, string? CorrelationId);

public sealed record RegisterMovementCommand(
    Guid AccountId,
    Movement Movement,
    IdempotencyKey IdempotencyKey,
    OperationContext Context);

public sealed record ReverseEntryCommand(
    Guid AccountId,
    Guid EntryId,
    IdempotencyKey IdempotencyKey,
    OperationContext Context);

/// <summary>Resultado de uma escrita. <see cref="Replayed"/> indica que a chave já havia sido processada.</summary>
public sealed record MovementOutcome(LedgerEntry Entry, bool Replayed);

public sealed record BalanceSnapshot(Guid AccountId, string Currency, decimal Balance, DateTimeOffset AsOf, long LastSequence);

public sealed record StatementQuery(Guid AccountId, DateTimeOffset? From, DateTimeOffset? To, long? BeforeSequence, int Limit);

public sealed record StatementPage(IReadOnlyList<LedgerEntry> Items, long? NextBeforeSequence);

/// <summary>
/// Porta de escrita. A implementação precisa garantir, atomicamente:
/// (1) a regra de saldo, (2) a ordem por conta, (3) a idempotência e (4) o evento no outbox.
/// </summary>
public interface ILedgerWriter
{
    Task<Account> CreateAccountAsync(decimal overdraftLimit, CancellationToken cancellationToken);

    Task<Result<MovementOutcome>> RegisterMovementAsync(RegisterMovementCommand command, CancellationToken cancellationToken);

    Task<Result<MovementOutcome>> ReverseEntryAsync(ReverseEntryCommand command, CancellationToken cancellationToken);
}

/// <summary>Porta de publicação de eventos de integração (broker real em produção).</summary>
public interface IIntegrationEventPublisher
{
    Task PublishAsync(Guid messageId, string type, string payload, CancellationToken cancellationToken);
}

public interface ILedgerReader
{
    Task<Account?> GetAccountAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>
    /// Sem <paramref name="at"/>: saldo corrente (lido da conta, fortemente consistente).
    /// Com <paramref name="at"/>: posição consolidada naquele instante, a partir do ledger.
    /// </summary>
    Task<BalanceSnapshot?> GetBalanceAsync(Guid accountId, DateTimeOffset? at, CancellationToken cancellationToken);

    Task<StatementPage?> GetStatementAsync(StatementQuery query, CancellationToken cancellationToken);

    Task<LedgerEntry?> GetEntryAsync(Guid accountId, Guid entryId, CancellationToken cancellationToken);
}
