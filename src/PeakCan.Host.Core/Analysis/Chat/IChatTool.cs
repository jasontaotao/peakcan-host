namespace PeakCan.Host.Core.Analysis.Chat;

/// <summary>
/// One executable tool the assistant may call. Registered in DI and
/// resolved by <c>ChatFlow</c> into the <c>tools</c> array sent to the
/// provider; executed when the provider yields
/// <see cref="ChatUpdate.ToolCallRoundDone"/>.
/// </summary>
/// <remarks>
/// <see cref="ExecuteAsync"/> runs on the thread-pool (via
/// <c>Parallel.ForEachAsync</c> in <c>ChatFlow</c>). Implementations
/// that touch UI-thread-affined state (e.g.
/// <c>ObservableCollection</c>) must marshal via the
/// <c>IChatToolContext</c> abstraction, not via
/// <c>Dispatcher.Invoke</c> directly - this keeps the tool testable
/// with a fake context and avoids hidden WPF coupling in Core.
/// </remarks>
public interface IChatTool
{
    string Name { get; }

    /// <summary>OpenAI function-calling schema definition sent to the
    /// provider.</summary>
    ChatToolDefinition Definition { get; }

    /// <summary>Execute the tool with raw JSON arguments from the
    /// assistant. Return a JSON string to feed back as the tool-result
    /// message content. On failure return <c>{"error": "..."}</c>
    /// (do not throw) so the assistant can react in the next round.
    /// </summary>
    Task<string> ExecuteAsync(string argsJson, CancellationToken ct);
}
