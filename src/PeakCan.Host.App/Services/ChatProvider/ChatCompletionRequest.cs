using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PeakCan.Host.App.Services.ChatProvider;

/// <summary>
/// DeepSeek (OpenAI-compatible) chat completion request with tool-calling
/// support. Sister of <c>DeepSeekRequest</c> but distinct - the legacy
/// <c>DeepSeekRequest</c> is single-shot (no <c>tools</c>), used by
/// <c>ILlmProvider</c>; this one carries <c>tools</c> +
/// <c>tool_calls</c> + <c>tool_call_id</c> for multi-round chat.
/// </summary>
public sealed class ChatCompletionRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("messages")]
    public List<ChatCompletionMessage> Messages { get; set; } = new();

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ChatCompletionTool>? Tools { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; } = 0.2;
}

public sealed class ChatCompletionMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; set; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ChatCompletionToolCall>? ToolCalls { get; set; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; set; }
}

public sealed class ChatCompletionToolCall
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public ChatCompletionFunction Function { get; set; } = new();
}

public sealed class ChatCompletionFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = "";
}

public sealed class ChatCompletionTool
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public ChatCompletionToolFunction Function { get; set; } = new();
}

public sealed class ChatCompletionToolFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("parameters")]
    public JsonNode? Parameters { get; set; }
}
