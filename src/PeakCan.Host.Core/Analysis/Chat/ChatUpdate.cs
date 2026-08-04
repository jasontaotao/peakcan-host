namespace PeakCan.HIL.Core.Analysis.Chat;

/// <summary>
/// Streaming notifications emitted by <see cref="IChatProvider.ChatStreamingAsync"/>.
/// The consumer (<c>ChatFlow</c>) dispatches via pattern matching.
/// <list type="bullet">
///   <item><see cref="PartialDelta"/> - incremental assistant text fragment
///         (SSE <c>delta.content</c>) to append to the current AI bubble.</item>
///   <item><see cref="ToolCallStart"/> - the assistant began a tool call at
///         <c>index</c>; carry the function name for live UI display.</item>
///   <item><see cref="ToolCallArgDelta"/> - incremental <c>arguments</c>
///         fragment for the tool call at <c>index</c> (streamed in chunks;
///         accumulate until the round completes).</item>
///   <item><see cref="ToolCallRoundDone"/> - one round of tool calls is
///         fully accumulated; carries the complete <see cref="ChatToolCall"/>
///         list. The consumer executes them and appends tool-result
///         messages before the next provider round.</item>
///   <item><see cref="Done"/> - terminal: assistant replied
///         <c>finish_reason=stop</c>.</item>
///   <item><see cref="Error"/> - terminal: a non-recoverable failure
///         (HTTP/parse/cancellation). Surface the message to the user.</item>
/// </list>
/// </summary>
public abstract record ChatUpdate
{
    private ChatUpdate() { }

    /// <summary>Incremental assistant text fragment to append.</summary>
    public sealed record PartialDelta(string Text) : ChatUpdate;

    /// <summary>The assistant began a tool call at <paramref name="Index"/>.</summary>
    public sealed record ToolCallStart(int Index, string Name) : ChatUpdate;

    /// <summary>Incremental <c>arguments</c> fragment for the tool call at
    /// <paramref name="Index"/>.</summary>
    public sealed record ToolCallArgDelta(int Index, string ArgsDelta) : ChatUpdate;

    /// <summary>One round of tool calls fully accumulated. The consumer
    /// executes <see cref="ToolCalls"/> and appends tool-result messages
    /// before re-invoking the provider for the next round.</summary>
    public sealed record ToolCallRoundDone(IReadOnlyList<ChatToolCall> ToolCalls) : ChatUpdate;

    /// <summary>Terminal: assistant finished (<c>finish_reason=stop</c>).</summary>
    public sealed record Done : ChatUpdate;

    /// <summary>Terminal: non-recoverable failure.</summary>
    public sealed record Error(string Message) : ChatUpdate;
}
