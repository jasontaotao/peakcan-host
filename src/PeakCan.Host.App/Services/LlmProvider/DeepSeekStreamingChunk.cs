using System.Text.Json.Serialization;

namespace PeakCan.Host.App.Services.LlmProvider;

/// <summary>v3.59.0 MINOR: per-chunk DTO for DeepSeek SSE streaming
/// response. Each <c>data: {...}</c> line in the SSE stream maps to
/// one <see cref="DeepSeekStreamingChunk"/>; the final line is
/// <c>data: [DONE]</c> (sentinel — not deserialized).</summary>
public sealed record DeepSeekStreamingChunk
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("object")]
    public string? Object { get; init; }

    [JsonPropertyName("created")]
    public long? Created { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("choices")]
    public List<DeepSeekStreamingChoice>? Choices { get; init; }
}

public sealed record DeepSeekStreamingChoice
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("delta")]
    public DeepSeekStreamingDelta? Delta { get; init; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }
}

public sealed record DeepSeekStreamingDelta
{
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }
}
