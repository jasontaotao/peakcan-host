using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeakCan.Host.Core.Uds.Database;

/// <summary>
/// JSON converter that accepts a UDS 16-bit id in any of these forms:
/// <list type="bullet">
///   <item>Decimal number: <c>6160</c></item>
///   <item>Hex string with prefix: <c>"0xF190"</c></item>
///   <item>Hex string without prefix: <c>"F190"</c></item>
/// </list>
/// Used by <see cref="DidDatabase"/> and <see cref="RoutineDatabase"/> so
/// user JSON files can use the natural hex representation.
/// </summary>
public sealed class HexUshortJsonConverter : JsonConverter<ushort>
{
    public override ushort Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            if (reader.TryGetUInt16(out var n))
                return n;
            throw new JsonException("UDS id numeric value is out of range for UInt16.");
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            if (string.IsNullOrWhiteSpace(s))
                throw new JsonException("Empty UDS id string.");

            var trimmed = s.Trim();
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed[2..];

            if (ushort.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
                return hex;
            if (ushort.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec))
                return dec;

            throw new JsonException($"Cannot parse '{s}' as a UDS 16-bit id.");
        }

        throw new JsonException($"Expected number or string for UDS id, got {reader.TokenType}.");
    }

    public override void Write(Utf8JsonWriter writer, ushort value, JsonSerializerOptions options)
    {
        writer.WriteStringValue($"0x{value:X4}");
    }
}
