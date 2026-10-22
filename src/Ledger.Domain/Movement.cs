namespace Ledger.Domain;

/// <summary>Intenção validada de movimentar uma conta (ainda não registrada).</summary>
public sealed record Movement
{
    public const int MaxDescriptionLength = 140;

    private Movement(EntryType type, Money amount, string? description)
    {
        Type = type;
        Amount = amount;
        Description = description;
    }

    public EntryType Type { get; }

    public Money Amount { get; }

    public string? Description { get; }

    /// <summary>Variação que este movimento provoca no saldo.</summary>
    public decimal Delta => Type.SignedAmount(Amount);

    public static Result<Movement> Create(EntryType type, decimal amount, string? description)
    {
        if (!Enum.IsDefined(type))
        {
            return Result.Failure<Movement>(LedgerErrors.Validation("invalid_type", "Tipo deve ser 'credit' ou 'debit'."));
        }

        var money = Money.Create(amount);
        if (!money.IsSuccess)
        {
            return Result.Failure<Movement>(money.Error!);
        }

        var normalized = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (normalized is { Length: > MaxDescriptionLength })
        {
            return Result.Failure<Movement>(LedgerErrors.Validation(
                "invalid_description", $"A descrição deve ter no máximo {MaxDescriptionLength} caracteres."));
        }

        return Result.Success(new Movement(type, money.Value, normalized));
    }
}
