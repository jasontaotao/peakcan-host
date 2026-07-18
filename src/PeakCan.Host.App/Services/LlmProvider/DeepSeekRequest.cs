using System.Text.Json.Serialization;

namespace PeakCan.Host.App.Services.LlmProvider;

/// <summary>v3.54.0 MINOR: DeepSeek chat completion request.
/// Sister of aspice-toolkit dashboard/backend.py LLM_PROVIDERS config.
/// Per v3.52.0 hard-boundary: stream=false, response_format=json_object,
/// temperature + max_tokens to bound response. NO Authorization header
/// property here — added per-request by DeepSeekProvider.</summary>
public sealed class DeepSeekRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "deepseek-chat";

    [JsonPropertyName("messages")]
    public List<DeepSeekMessage> Messages { get; set; } = new();

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 2048;

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; } = 0.2;

    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = false;

    /// <summary>v3.61.0 PATCH: nullable — null when streaming so SSE
    /// deltas are plain text fragments (human-readable), not JSON
    /// fragments like {"summary": "Anal...". Non-streaming requests
    /// keep json_object for structured FinalResult parsing.</summary>
    [JsonPropertyName("response_format")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DeepSeekResponseFormat? ResponseFormat { get; set; }
}

public sealed class DeepSeekMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}

public sealed class DeepSeekResponseFormat
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "json_object";
}