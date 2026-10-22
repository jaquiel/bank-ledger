namespace Ledger.Domain;

/// <summary>
/// Valor monetário positivo em BRL, com no máximo duas casas decimais.
/// Sempre <see cref="decimal"/>: ponto flutuante binário não representa centavos com exatidão.
/// O sinal (entrada/saída) não pertence ao valor, e sim ao tipo do lançamento.
/// </summary>
public readonly record struct Money
{
    public const string Currency = "BRL";
    public const int DecimalPlaces = 2;

    /// <summary>Teto por lançamento; protege contra erros grosseiros de digitação/integração e cabe em numeric(19,2).</summary>
    public const decimal MaxAmount = 1_000_000_000m;

    private Money(decimal amount) => Amount = amount;

    public decimal Amount { get; }

    public static Result<Money> Create(decimal amount)
    {
        if (amount <= 0)
        {
            return Result.Failure<Money>(LedgerErrors.Validation("invalid_amount", "O valor deve ser maior que zero."));
        }

        if (amount > MaxAmount)
        {
            return Result.Failure<Money>(LedgerErrors.Validation(
                "invalid_amount", $"O valor excede o máximo permitido por lançamento ({MaxAmount:N2})."));
        }

        if (decimal.Round(amount, DecimalPlaces) != amount)
        {
            return Result.Failure<Money>(LedgerErrors.Validation(
                "invalid_amount", $"O valor deve ter no máximo {DecimalPlaces} casas decimais."));
        }

        return Result.Success(new Money(decimal.Round(amount, DecimalPlaces)));
    }

    public override string ToString() => $"{Currency} {Amount:0.00}";
}
