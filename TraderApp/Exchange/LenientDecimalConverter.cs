using System.Globalization;
using Newtonsoft.Json;

namespace TraderApp.Exchange;

/// <summary>
/// Reads WhiteBIT numeric fields defensively.
///
/// The API returns numbers as strings in most places, but also returns JSON
/// null for fields that are not yet meaningful - most importantly
/// <c>basePrice</c> on a position whose entry order is still resting unfilled.
/// The default Newtonsoft behaviour throws on null-to-decimal, which took down
/// the whole tick: positions/open failed, so no position could be read and no
/// exit could be managed while an entry order was pending.
///
/// Null, empty and unparseable values become 0 for <c>decimal</c> and null for
/// <c>decimal?</c>. Callers must therefore treat 0 as "not available" - see the
/// guards in AutoTrader that skip positions with no base price or size.
/// </summary>
internal sealed class LenientDecimalConverter : JsonConverter
{
    public override bool CanWrite => false;

    public override bool CanConvert(Type objectType) =>
        objectType == typeof(decimal) || objectType == typeof(decimal?);

    public override object? ReadJson(
        JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        var nullable = objectType == typeof(decimal?);
        object? Fallback() => nullable ? null : 0m;

        switch (reader.TokenType)
        {
            case JsonToken.Null:
            case JsonToken.Undefined:
                return Fallback();

            case JsonToken.Integer:
            case JsonToken.Float:
                try { return Convert.ToDecimal(reader.Value, CultureInfo.InvariantCulture); }
                catch (OverflowException) { return Fallback(); }

            case JsonToken.String:
                var s = reader.Value as string;
                if (string.IsNullOrWhiteSpace(s)) return Fallback();
                return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
                    ? d
                    : Fallback();

            case JsonToken.Boolean:
                return (bool)reader.Value! ? 1m : 0m;

            default:
                return Fallback();
        }
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) =>
        throw new NotSupportedException("CanWrite is false; serialisation uses the default behaviour.");
}
