using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PeakCan.Host.Core.Analysis;
using PeakCan.Host.Core.Analysis.Chat;

namespace PeakCan.Host.App.Services.ChatProvider;

/// <summary>
/// <see cref="IChatProvider"/> implementation backed by the DeepSeek
/// (OpenAI-compatible) chat completions API with streaming + tool-calling.
/// </summary>
/// <remarks>
/// <b>Responsibility (spec §5 v2):</b> owns the DeepSeek protocol only -
/// SSE read + tool_call fragment accumulation + yield <see cref="ChatUpdate"/>.
/// Does NOT execute tools. The caller (<c>ChatFlow</c>) consumes
/// <see cref="ChatUpdate.ToolCallRoundDone"/>, runs the tools, appends
/// tool-result messages, then calls <see cref="ChatStreamingAsync"/> again
/// for the next round.
/// <para>
/// Reuses the SSE line-read pattern from <c>DeepSeekProvider</c> via
/// <see cref="SseLineReader"/>; builds its own tool-calling-capable DTOs
/// (<see cref="ChatCompletionRequest"/> / <see cref="ChatCompletionChunk"/>)
/// because the legacy <c>DeepSeekRequest</c> is single-shot and has no
/// <c>tools</c>/<c>tool_calls</c> fields.
/// </para>
/// <para>
/// Errors (missing key, 401/429, HTTP, parse) surface as
/// <see cref="ChatUpdate.Error"/> (never thrown) so the chat loop degrades
/// gracefully.
/// </para>
/// </remarks>
public sealed class DeepSeekChatProvider : IChatProvider
{
    private const string ApiKeyCredentialKey = "deepseek-api-key";
    private const string HttpClientName = "DeepSeek";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICredentialStore _credentialStore;
    private readonly IOptions<DeepSeekOptions> _options;
    private readonly ILogger<DeepSeekChatProvider> _logger;

    public DeepSeekChatProvider(
        IHttpClientFactory httpClientFactory,
        ICredentialStore credentialStore,
        IOptions<DeepSeekOptions> options,
        ILogger<DeepSeekChatProvider> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string DisplayName => $"DeepSeek Chat ({_options.Value.Model})";

    public async IAsyncEnumerable<ChatUpdate> ChatStreamingAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // 1. Read API key (never log the value). CS1631 forbids yield in a
        // catch body, so capture the error and yield after the try.
        string? apiKey = null;
        string? apiKeyError = null;
        try
        {
            apiKey = await _credentialStore.GetAsync(ApiKeyCredentialKey, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read API key from credential store");
            apiKeyError = "Failed to read API key from credential store";
        }
        if (apiKeyError is not null)
        {
            yield return new ChatUpdate.Error(apiKeyError);
            yield break;
        }
        if (string.IsNullOrEmpty(apiKey))
        {
            yield return new ChatUpdate.Error("API key not configured");
            yield break;
        }

        // 2. Build request (stream=true; tools only when non-empty)
        var request = new ChatCompletionRequest
        {
            Model = _options.Value.Model,
            Stream = true,
            Messages = messages.Select(ToCompletionMessage).ToList(),
            Tools = tools.Count > 0 ? tools.Select(ToCompletionTool).ToList() : null,
        };

        // 3. POST
        var http = _httpClientFactory.CreateClient(HttpClientName);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_options.Value.ApiBase}/chat/completions")
        {
            Content = JsonContent.Create(request),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        // CS1631: cannot yield inside a catch body; capture state + yield after.
        HttpResponseMessage? response = null;
        string? httpError = null;
        bool cancelled = false;
        try
        {
            response = await http.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "HTTP error calling DeepSeek chat");
            httpError = $"DeepSeek HTTP error: {ex.Message}";
        }
        if (cancelled) yield break;
        if (httpError is not null)
        {
            yield return new ChatUpdate.Error(httpError);
            yield break;
        }
        if (response is null) yield break;

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                _logger.LogWarning("DeepSeek chat returned non-success status: {StatusCode}", statusCode);
                var msg = response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
                        "DeepSeek API key invalid or revoked",
                    System.Net.HttpStatusCode.TooManyRequests =>
                        "DeepSeek rate limit exceeded; retry later",
                    _ => $"DeepSeek server error (HTTP {statusCode})",
                };
                yield return new ChatUpdate.Error(msg);
                yield break;
            }

            // 4. Read SSE stream
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var readTimeout = TimeSpan.FromSeconds(_options.Value.TimeoutSeconds);

            // Tool-call accumulators keyed by delta index
            var toolIds = new Dictionary<int, string>();
            var toolNames = new Dictionary<int, string>();
            var toolArgs = new Dictionary<int, StringBuilder>();

            await foreach (var payload in SseLineReader.ReadDataLinesAsync(reader, readTimeout, ct)
                             .ConfigureAwait(false))
            {
                ChatCompletionChunk? chunk;
                try
                {
                    chunk = JsonSerializer.Deserialize<ChatCompletionChunk>(payload, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse DeepSeek chat SSE chunk; skipping");
                    continue;
                }
                if (chunk?.Choices is null) continue;

                foreach (var choice in chunk.Choices)
                {
                    var delta = choice.Delta;
                    if (delta is not null)
                    {
                        if (!string.IsNullOrEmpty(delta.Content))
                            yield return new ChatUpdate.PartialDelta(delta.Content);

                        if (delta.ToolCalls is not null)
                        {
                            foreach (var tc in delta.ToolCalls)
                            {
                                if (!string.IsNullOrEmpty(tc.Id))
                                    toolIds[tc.Index] = tc.Id!;

                                if (tc.Function?.Name is { Length: > 0 } name &&
                                    !toolNames.ContainsKey(tc.Index))
                                {
                                    toolNames[tc.Index] = name;
                                    yield return new ChatUpdate.ToolCallStart(tc.Index, name);
                                }

                                if (tc.Function?.Arguments is { Length: > 0 } args)
                                {
                                    if (!toolArgs.ContainsKey(tc.Index))
                                        toolArgs[tc.Index] = new StringBuilder();
                                    toolArgs[tc.Index].Append(args);
                                    yield return new ChatUpdate.ToolCallArgDelta(tc.Index, args);
                                }
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(choice.FinishReason))
                    {
                        if (choice.FinishReason == "tool_calls")
                        {
                            var calls = toolNames.Keys
                                .OrderBy(i => i)
                                .Select(i => new ChatToolCall(
                                    toolIds.GetValueOrDefault(i) ?? string.Empty,
                                    toolNames[i],
                                    toolArgs.GetValueOrDefault(i)?.ToString() ?? string.Empty))
                                .ToList();
                            yield return new ChatUpdate.ToolCallRoundDone(calls);
                        }
                        else
                        {
                            yield return new ChatUpdate.Done();
                        }
                        yield break; // finish_reason ends this round
                    }
                }
            }

            // Stream ended without an explicit finish_reason
            yield return new ChatUpdate.Done();
        }
    }

    private static ChatCompletionMessage ToCompletionMessage(ChatMessage m) => new()
    {
        Role = m.Role,
        Content = m.Content,
        ToolCalls = m.ToolCalls?.Select(tc => new ChatCompletionToolCall
        {
            Id = tc.Id,
            Function = new ChatCompletionFunction { Name = tc.FunctionName, Arguments = tc.FunctionArgs },
        }).ToList(),
        ToolCallId = m.ToolCallId,
    };

    private static ChatCompletionTool ToCompletionTool(ChatToolDefinition d) => new()
    {
        Function = new ChatCompletionToolFunction
        {
            Name = d.Name,
            Description = d.Description,
            Parameters = d.Parameters,
        },
    };
}
