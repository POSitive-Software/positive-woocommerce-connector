using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PA.Connectors.WooCommerce;

/// <summary>
/// JSON conventions of the Woo REST API (wc/v3): snake_case names, money as
/// STRINGS ("37.80", "" for unset) except the odd raw number (line item
/// <c>price</c>, coupon <c>nominal_amount</c>), and ids that are sometimes
/// quoted (image id "43"). Money is always <see cref="decimal"/>, parsed with
/// the invariant culture — never double, never the machine's culture.
/// </summary>
public static class WooJson
{
    private const NumberStyles MoneyStyle =
        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;

    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new WooMoneyConverter());
        options.Converters.Add(new WooNullableMoneyConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>Woo's wire format for an amount we send: invariant, no grouping, no exponent ("45", "16.5").</summary>
    public static string FormatMoney(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Parses a Woo money string. Blank → null. Throws <see cref="FormatException"/> on anything else that is not a number.</summary>
    public static decimal? ParseMoney(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        // No AllowThousands: "12,50" (a comma-decimal locale leaking through)
        // must fail, not silently become 1250.
        return decimal.Parse(value.Trim(), MoneyStyle, CultureInfo.InvariantCulture);
    }

    internal static decimal? ReadMoney(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.Number:
                return reader.GetDecimal();
            case JsonTokenType.String:
                try
                {
                    return ParseMoney(reader.GetString());
                }
                catch (FormatException ex)
                {
                    throw new JsonException($"'{reader.GetString()}' is not a money amount.", ex);
                }
            default:
                throw new JsonException($"Expected a money amount, got {reader.TokenType}.");
        }
    }
}

/// <summary>decimal ⇄ Woo money: reads "37.80", 37.8 or "" (→ 0); writes a string.</summary>
public sealed class WooMoneyConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        WooJson.ReadMoney(ref reader) ?? 0m;

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
        writer.WriteStringValue(WooJson.FormatMoney(value));
}

/// <summary>
/// Woo's <c>manage_stock</c>: a boolean, except on a variation whose parent manages
/// stock, where it is the string <c>"parent"</c> — read as false (the variation does
/// not manage its own). Writes a boolean.
/// </summary>
public sealed class WooManageStockConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False or JsonTokenType.Null => false,
            JsonTokenType.String => reader.GetString()?.Trim().ToLowerInvariant() is "true" or "yes" or "1",
            JsonTokenType.Number => reader.TryGetInt32(out var n) && n != 0,
            _ => throw new JsonException($"Expected manage_stock, got {reader.TokenType}."),
        };

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) =>
        writer.WriteBooleanValue(value);
}

/// <summary>decimal? ⇄ Woo money: "" and null read as null.</summary>
public sealed class WooNullableMoneyConverter : JsonConverter<decimal?>
{
    public override bool HandleNull => true;

    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        WooJson.ReadMoney(ref reader);

    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(WooJson.FormatMoney(value.Value));
    }
}
