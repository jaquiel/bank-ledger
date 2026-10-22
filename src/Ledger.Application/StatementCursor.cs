using System.Buffers.Binary;
using Ledger.Domain;

namespace Ledger.Application;

/// <summary>
/// Cursor opaco para paginação do extrato (keyset pagination sobre <c>sequence</c>).
/// Diferente de offset, não degrada com o volume e não pula/duplica itens quando chegam lançamentos novos.
/// </summary>
public static class StatementCursor
{
    public static string Encode(long sequence)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(buffer, sequence);
        return Convert.ToBase64String(buffer).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static Result<long> Decode(string cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        var invalid = Result.Failure<long>(LedgerErrors.Validation("invalid_cursor", "Cursor de paginação inválido."));

        var base64 = cursor.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');

        Span<byte> buffer = stackalloc byte[16];
        if (!Convert.TryFromBase64String(base64, buffer, out var written) || written != sizeof(long))
        {
            return invalid;
        }

        var sequence = BinaryPrimitives.ReadInt64BigEndian(buffer[..sizeof(long)]);
        return sequence > 0 ? Result.Success(sequence) : invalid;
    }
}
