using System.Text.Json.Serialization;

namespace PeakCan.Host.App.Services.ChatProvider;

/// <summary>
/// SSE chunk DTO for a streaming chat completion. Each <c>data: {...}</c>
/// line maps to one <see cref="ChatCompletionChunk"/>; the final line is
/// <c>data: [DONE]</c> (sentinel - not deserialized).
/// <para>
/// Tool calls arrive in fragments: <c>delta.tool_calls[].index</c>
/// identifies which call, <c>function.name</c> comes once at the start,
/// and <c>function.arguments</c> arrives across multiple chunks. The
/// provider accumulates by <c>index</c> until <c>finish_reason=tool_calls</c>.
/// </para>
/// </summary>
public sealed class ChatCompletionChunk
{
    [JsonPropertyName("choices")]
    public List<ChatCompletionChunkChoice>? Choices { get; init; }
}

public sealed class ChatCompletionChunkChoice
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("delta")]
    public ChatCompletionDelta? Delta { get; init; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }
}

public sealed class ChatCompletionDelta
{
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("tool_calls")]
    public List<ChatCompletionDeltaToolCall>? ToolCalls { get; init; }
}

public sealed class ChatCompletionDeltaToolCall
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("function")]
    public ChatCompletionDeltaFunction? Function { get; init; }
}

public sealed class ChatCompletionDeltaFunction
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; init; }
}
