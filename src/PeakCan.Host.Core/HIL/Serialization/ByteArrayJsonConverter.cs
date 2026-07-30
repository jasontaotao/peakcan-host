using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeakCan.Host.Core.HIL.Serialization;

/// <summary>
/// JSON converter for byte[] that accepts both numeric arrays ([2, 62, 0])
/// and Base64 strings. Writes as numeric array for human readability.
/// System.Text.Json defaults to Base64-only for byte[], which breaks HIL
/// test suite JSON where data fields use numeric arrays.
/// </summary>
public sealed class ByteArrayJsonConverter : JsonConverter<byte[]?>
{
    public override byte[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType == JsonTokenType.String)
        {
            // Base64 string format
            return reader.GetBytesFromBase64();
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            // Numeric array format: [2, 62, 0]
            var list = new List<byte>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                list.Add(reader.GetByte());
            }
            return list.ToArray();
        }

        throw new JsonException(
            $"Expected byte array as JSON array or Base64 string, got {reader.TokenType}.");
    }

    public override void Write(Utf8JsonWriter writer, byte[]? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        // Write as numeric array for human readability
        writer.WriteStartArray();
        foreach (var b in value)
            writer.WriteNumberValue(b);
        writer.WriteEndArray();
    }
}
