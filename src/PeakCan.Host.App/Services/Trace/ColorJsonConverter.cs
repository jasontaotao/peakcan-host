using System.Text.Json;
using System.Text.Json.Serialization;
using ScottPlot;

namespace PeakCan.Host.App.Services.Trace;

/// <summary>
/// v3.5.0 MINOR: JSON converter for Color. Writes a
/// four-property object (<c>{"a":,"r":,"g":,"b":}</c>) so a human
/// inspecting a <c>.tmtrace</c> file can read each channel directly.
/// v3.62.0 MINOR: migrated from OxyColor → ScottPlot.Color.
/// </summary>
public sealed class ColorJsonConverter : JsonConverter<Color>
{
    public override Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            reader.Read();
            return new Color(0, 0, 0, 0);
        }
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"Expected StartObject for Color, got {reader.TokenType}.");

        byte a = 0, r = 0, g = 0, b = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException($"Expected property name in Color, got {reader.TokenType}.");
            var name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "a": a = reader.GetByte(); break;
                case "r": r = reader.GetByte(); break;
                case "g": g = reader.GetByte(); break;
                case "b": b = reader.GetByte(); break;
                default: reader.Skip(); break;
            }
        }
        return new Color(r, g, b, a);
    }

    public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("a", value.A);
        writer.WriteNumber("r", value.R);
        writer.WriteNumber("g", value.G);
        writer.WriteNumber("b", value.B);
        writer.WriteEndObject();
    }
}