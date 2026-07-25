namespace PeakCan.Host.Core.Analysis.Chat;

/// <summary>
/// Chat LLM provider contract: a streaming, multi-round,
/// tool-calling-capable conversation. Sister of
/// <see cref="PeakCan.Host.Core.Analysis.ILlmProvider"/> but distinct:
/// <see cref="ILlmProvider"/> is single-shot (one request -> one JSON
/// report); <see cref="IChatProvider"/> streams <see cref="ChatUpdate"/>
/// fragments and accumulates tool calls across rounds.
/// <para>
/// Per the v2 responsibility split (see spec §5): the provider only
/// owns the DeepSeek protocol (SSE read + tool_call accumulation +
/// yield). It does NOT execute tools - the consumer (<c>ChatFlow</c>)
/// executes the tools from <see cref="ChatUpdate.ToolCallRoundDone"/>
/// and appends tool-result messages before the next round.
/// </para>
/// <para>
/// Implementations MUST surface 401/429/timeout/JSON-parse errors as
/// <see cref="ChatUpdate.Error"/> (not exceptions) so the caller can
/// show a degraded message instead of crashing the chat loop.
/// </para>
/// </summary>
public interface IChatProvider
{
    string DisplayName { get; }

    /// <summary>Stream one round of the conversation. The caller drives
    /// the multi-round loop: on <see cref="ChatUpdate.ToolCallRoundDone"/>
    /// execute the tools, append tool-result <see cref="ChatMessage"/>s,
    /// then call again with the extended message list.</summary>
    IAsyncEnumerable<ChatUpdate> ChatStreamingAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatToolDefinition> tools,
        CancellationToken ct);
}
