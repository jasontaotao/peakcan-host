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

        // 防御性检查：parameters 字段必须存在
        if (!root.TryGetProperty("parameters", out var parametersElement))
        {
            throw new JsonException("Missing required property 'parameters' in test case step.");
        }

        var parameters = JsonSerializer.Deserialize<StepParameters>(
            parametersElement.GetRawText(), Serialization.HILJsonOptions.Default)
            ?? throw new JsonException("Failed to deserialize 'parameters' — unknown $kind or null value.");

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
