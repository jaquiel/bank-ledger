using Ledger.Domain;

namespace Ledger.Application;

/// <summary>
/// Chave enviada pelo cliente no header <c>Idempotency-Key</c>. Escopo: (cliente, chave).
/// Reenviar a mesma chave com o mesmo payload devolve o resultado original sem novo efeito.
/// </summary>
public sealed record IdempotencyKey
{
    public const int MaxLength = 100;

    private IdempotencyKey(string value) => Value = value;

    public string Value { get; }

    public static Result<IdempotencyKey> Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result.Failure<IdempotencyKey>(LedgerErrors.Validation(
                "idempotency_key_required", "O header Idempotency-Key é obrigatório em operações de escrita."));
        }

        if (value.Length > MaxLength || value.Any(c => c < 0x21 || c > 0x7E))
        {
            return Result.Failure<IdempotencyKey>(LedgerErrors.Validation(
                "idempotency_key_invalid", $"Idempotency-Key deve ter até {MaxLength} caracteres ASCII visíveis (ex.: um UUID)."));
        }

        return Result.Success(new IdempotencyKey(value));
    }

    public override string ToString() => Value;
}
