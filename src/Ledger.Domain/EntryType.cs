namespace Ledger.Domain;

public enum EntryType
{
    Credit,
    Debit,
}

public static class EntryTypeExtensions
{
    /// <summary>Delta aplicado ao saldo: créditos somam, débitos subtraem.</summary>
    public static decimal SignedAmount(this EntryType type, Money amount) =>
        type == EntryType.Credit ? amount.Amount : -amount.Amount;

    /// <summary>Um estorno é um lançamento do tipo oposto ao original.</summary>
    public static EntryType Opposite(this EntryType type) =>
        type == EntryType.Credit ? EntryType.Debit : EntryType.Credit;

    public static string ToCode(this EntryType type) => type switch
    {
        EntryType.Credit => "credit",
        EntryType.Debit => "debit",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Tipo de lançamento desconhecido."),
    };

    public static EntryType FromCode(string code) => code switch
    {
        "credit" => EntryType.Credit,
        "debit" => EntryType.Debit,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Tipo de lançamento desconhecido."),
    };
}
