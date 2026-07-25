using System.Text.Json.Nodes;

namespace PeakCan.Host.Core.Analysis.Chat;

/// <summary>
/// Definition of one tool the assistant may call. Serialized into the
/// DeepSeek <c>tools</c> array (OpenAI function-calling schema).
/// </summary>
/// <remarks>
/// <see cref="Parameters"/> is a <see cref="JsonNode"/> (not
/// <c>IReadOnlyDictionary&lt;string, object?&gt;</c>) so it round-trips
/// through <c>System.Text.Json</c> without <c>object?</c> values being
/// serialized as <c>"System.Object"</c>. Build it from a JSON schema
/// string via <see cref="JsonNode.Parse(string)"/>.
/// </remarks>
public sealed record ChatToolDefinition(
    string Name,
    string Description,
    JsonNode Parameters);
