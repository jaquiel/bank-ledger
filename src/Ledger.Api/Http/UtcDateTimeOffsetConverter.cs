using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ledger.Api.Http;

/// <summary>
/// Instantes sempre em UTC com sufixo 'Z' e precisão de microssegundos (a do PostgreSQL).
/// Evita o "+00:00", cujo '+' vira espaço quando o valor é reenviado em query string (ex.: ?at=...).
/// </summary>
public sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.FFFFFF'Z'";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTimeOffset.Parse(reader.GetString() ?? string.Empty, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture));
    }
}
