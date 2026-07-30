using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeakCan.Host.Core;

/// <summary>
/// JSON converter for CanId. Serializes as {"raw":..., "format":..., "type":...}.
/// </summary>
public sealed class CanIdJsonConverter : JsonConverter<CanId>
{
    public override CanId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        uint raw = 0;
        FrameFormat format = FrameFormat.Standard;
        FrameType type = FrameType.Data;

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected StartObject for CanId.");

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected PropertyName.");

            var propName = reader.GetString();
            reader.Read();

            switch (propName)
            {
                case "raw":
                    raw = reader.GetUInt32();
                    break;
                case "format":
                    format = JsonSerializer.Deserialize<FrameFormat>(ref reader, options);
                    break;
                case "type":
                    type = JsonSerializer.Deserialize<FrameType>(ref reader, options);
                    break;
                case "isExtended":
                    reader.Skip();
                    break;
            }
        }

        return new CanId(raw, format, type);
    }

    public override void Write(Utf8JsonWriter writer, CanId value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("raw", value.Raw);
        writer.WriteString("format", value.Format.ToString());
        writer.WriteString("type", value.Type.ToString());
        writer.WriteEndObject();
    }
}
