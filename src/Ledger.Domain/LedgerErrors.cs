namespace Ledger.Domain;

/// <summary>Catálogo dos erros de negócio. O <c>Code</c> é contrato estável da API; a mensagem pode mudar.</summary>
public static class LedgerErrors
{
    public static DomainError AccountNotFound(Guid accountId) =>
        new(ErrorKind.NotFound, "account_not_found", $"Conta {accountId} não encontrada.");

    public static DomainError AccountInactive(Guid accountId) =>
        new(ErrorKind.BusinessRule, "account_inactive", $"A conta {accountId} não está ativa para movimentações.");

    public static DomainError InsufficientFunds(Guid accountId) =>
        new(ErrorKind.BusinessRule, "insufficient_funds", $"Saldo insuficiente na conta {accountId}.");

    public static DomainError EntryNotFound(Guid entryId) =>
        new(ErrorKind.NotFound, "entry_not_found", $"Lançamento {entryId} não encontrado nesta conta.");

    public static DomainError EntryAlreadyReversed(Guid entryId) =>
        new(ErrorKind.Conflict, "entry_already_reversed", $"O lançamento {entryId} já foi estornado.");

    public static DomainError CannotReverseReversal(Guid entryId) =>
        new(ErrorKind.BusinessRule, "cannot_reverse_reversal", $"O lançamento {entryId} é um estorno e não pode ser estornado.");

    public static DomainError IdempotencyKeyReused() =>
        new(ErrorKind.BusinessRule, "idempotency_key_reused",
            "Esta Idempotency-Key já foi usada com um payload diferente. Gere uma nova chave para uma nova operação.");

    public static DomainError IdempotencyKeyInProgress() =>
        new(ErrorKind.Conflict, "idempotency_key_in_progress",
            "Uma requisição com esta Idempotency-Key ainda está em processamento. Tente novamente em instantes.");

    public static DomainError Validation(string code, string message) =>
        new(ErrorKind.Validation, code, message);
}
