using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ledger.Domain;

namespace Ledger.Application;

/// <summary>
/// Impressão digital do payload associado a uma Idempotency-Key. Detecta reuso da mesma chave
/// com uma operação diferente (erro do cliente que, se ignorado, esconderia um lançamento).
/// </summary>
public static class RequestFingerprint
{
    public static string ForMovement(Guid accountId, Movement movement)
    {
        ArgumentNullException.ThrowIfNull(movement);
        return Hash(string.Join(
            '|',
            "movement",
            accountId.ToString("N"),
            movement.Type.ToCode(),
            movement.Amount.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            movement.Description ?? string.Empty));
    }

    public static string ForReversal(Guid accountId, Guid entryId) =>
        Hash(string.Join('|', "reversal", accountId.ToString("N"), entryId.ToString("N")));

    private static string Hash(string canonical) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
}
