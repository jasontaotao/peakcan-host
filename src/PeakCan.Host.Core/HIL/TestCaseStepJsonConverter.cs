using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeakCan.Host.Core.HIL;

/// <summary>
/// Custom JSON converter for TestCaseStep.
/// $kind discriminator lives inside parameters (written by JsonPolymorphic on StepParameters).
/// </summary>
internal sealed class TestCaseStepJsonConverter : JsonConverter<TestCaseStep>
{
    public override TestCaseStep Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        var parameters = JsonSerializer.Deserialize<StepParameters>(
            root.GetProperty("parameters").GetRawText(), Serialization.HILJsonOptions.Default)!;
        var label = root.TryGetProperty("label", out var l) ? l.GetString() : null;

        return TestCaseStep.Create(parameters, label);
    }

    public override void Write(Utf8JsonWriter writer, TestCaseStep value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.Label is not null)
            writer.WriteString("label", value.Label);
        writer.WritePropertyName("parameters");
        JsonSerializer.Serialize(writer, value.Parameters, typeof(StepParameters), Serialization.HILJsonOptions.Default);
        writer.WriteEndObject();
    }
}
