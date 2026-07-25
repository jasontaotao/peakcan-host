namespace PeakCan.Host.Core.Analysis.Chat;

/// <summary>
/// One message in the chat conversation. Mirrors the DeepSeek
/// (OpenAI-compatible) <c>messages</c> array shape so it serializes
/// 1:1 without a mapping layer.
/// <para>
/// <see cref="Role"/> is a <c>string</c> (not an enum) so the value
/// ("system" | "user" | "assistant" | "tool") serializes verbatim.
/// </para>
/// <para>
/// An assistant turn that requests tools carries <see cref="ToolCalls"/>
/// (and empty <see cref="Content"/>); the matching tool result turn
/// carries <see cref="ToolCallId"/> + result text in <see cref="Content"/>.
/// </para>
/// </summary>
public sealed record ChatMessage(
    string Role,
    string? Content,
    IReadOnlyList<ChatToolCall>? ToolCalls,
    string? ToolCallId);
