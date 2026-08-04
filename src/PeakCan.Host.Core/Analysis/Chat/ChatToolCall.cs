namespace PeakCan.HIL.Core.Analysis.Chat;

/// <summary>
/// One tool call requested by the assistant in a chat round. DeepSeek
/// (OpenAI-compatible) streams tool calls in fragments across multiple
/// SSE chunks; the provider accumulates <c>function.name</c> +
/// <c>function.arguments</c> by <c>index</c> and yields a complete
/// <see cref="ChatToolCall"/> via <see cref="ChatUpdate.ToolCallRoundDone"/>.
/// </summary>
public sealed record ChatToolCall(
    string Id,
    string FunctionName,
    string FunctionArgs);
