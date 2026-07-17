using System.Text.Json.Serialization;

namespace PeakCan.Host.App.Services.LlmProvider;

/// <summary>v3.54.0 MINOR: DeepSeek chat completion response.
/// Usage object captures token counts for logging (NOT persisted).</summary>
public sealed class DeepSeekResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("object")]
    public string Object { get; set; } = "";

    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("choices")]
    public List<DeepSeekChoice> Choices { get; set; } = new();

    [JsonPropertyName("usage")]
    public DeepSeekUsage? Usage { get; set; }
}

public sealed class DeepSeekChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("message")]
    public DeepSeekMessage Message { get; set; } = new();

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

public sealed class DeepSeekUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }
}