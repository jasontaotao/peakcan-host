using System.Text.Json;
using System.Text.Json.Serialization;
using OxyPlot;

namespace PeakCan.Host.App.Services.Trace;

/// <summary>
/// v3.5.0 MINOR: JSON converter for <see cref="OxyColor"/>. Writes a
/// four-property object (<c>{"a":,"r":,"g":,"b":}</c>) so a human
/// inspecting a <c>.tmtrace</c> file can read each channel directly —
/// alternative shapes (packed uint, hex string) require reading the
/// source to interpret them. Bundle format is human-editable.
/// <para>
/// <see cref="OxyColor.Value"/> packs four bytes (A,R,G,B) into one
/// <c>uint</c>. Round-trip losslessness is the only correctness
/// invariant — see <c>OxyColorJsonConverterTests</c>.
/// </para>
/// </summary>
public sealed class OxyColorJsonConverter : JsonConverter<OxyColor>
{
    public override OxyColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"Expected StartObject for OxyColor, got {reader.TokenType}.");

        byte a = 0, r = 0, g = 0, b = 0;
        bool sawA = false, sawR = false, sawG = false, sawB = false;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException($"Expected property name in OxyColor, got {reader.TokenType}.");
            var name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "a":
                    a = reader.GetByte(); sawA = true; break;
                case "r":
                    r = reader.GetByte(); sawR = true; break;
                case "g":
                    g = reader.GetByte(); sawG = true; break;
                case "b":
                    b = reader.GetByte(); sawB = true; break;
                default:
                    reader.Skip();
                    break;
            }
        }
        if (!(sawA && sawR && sawG && sawB))
            throw new JsonException("OxyColor requires all four channels (a, r, g, b).");
        return OxyColor.FromArgb(a, r, g, b);
    }

    public override void Write(Utf8JsonWriter writer, OxyColor value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("a", value.A);
        writer.WriteNumber("r", value.R);
        writer.WriteNumber("g", value.G);
        writer.WriteNumber("b", value.B);
        writer.WriteEndObject();
    }
}