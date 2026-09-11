using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.Mobile.Core.Chat.Tools;

/// <summary>
/// Tool <c>get_dbc_signal</c> - definition of one signal plus its zero-order
/// hold value at the current anchor timestamp (read from the replay cache).
/// </summary>
public sealed class GetDbcSignalTool : MobileChatToolBase
{
    private const string DefinitionSchema =
        """{"type":"object","properties":{"message":{"type":"string","description":"DBC message name"},"signal":{"type":"string","description":"Signal name within the message"}},"required":["message","signal"],"additionalProperties":false}""";

    private readonly IMobileChatToolContext _context;

    public GetDbcSignalTool(IMobileChatToolContext context, ILogger logger)
        : base(
            "get_dbc_signal",
            "Get the definition of a single DBC signal (start bit, length, factor, offset, unit) and its value at the current anchor timestamp when an anchor is set. Use after search_signals to drill into a specific signal.",
            DefinitionSchema,
            logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    protected override async Task<string> ExecuteCoreAsync(string argsJson, CancellationToken ct)
    {
        var dbc = _context.Dbc;
        if (dbc is null)
            return """{"error":"未加载 DBC"}""";

        var args = ParseArgs(argsJson);
        var msgNode = args["message"];
        var sigNode = args["signal"];
        if (msgNode is null || sigNode is null)
            return """{"error":"missing 'message' or 'signal'"}""";
        var msgName = msgNode.GetValue<string>();
        var sigName = sigNode.GetValue<string>();

        var message = dbc.Document.Messages.FirstOrDefault(
            m => m.Name.Equals(msgName, StringComparison.OrdinalIgnoreCase));
        if (message is null)
            return $$"""{"error":"unknown message: {{msgName}}"}""";
        var signal = message.Signals.FirstOrDefault(
            s => s.Name.Equals(sigName, StringComparison.OrdinalIgnoreCase));
        if (signal is null)
            return $$"""{"error":"unknown signal: {{sigName}} in message {{msgName}}"}""";

        // Anchor value: zero-order hold from the replay cache at the anchor ts.
        string? anchorValue = null;
        double? anchorTs = null;
        if (_context.HasAnchor && _context.AnchorTimestamp is { } a)
        {
            anchorTs = a;
            var frames = await _context.GetFramesBeforeAsync(a, ct).ConfigureAwait(false);
            foreach (var frame in frames)
            {
                var decode = dbc.Decode(frame.CanId, frame.IsExtended, frame.Data, frame.Dlc);
                var display = decode?.Signals.FirstOrDefault(s => s.Name == signal.Name);
                if (display is not null)
                {
                    anchorValue = string.IsNullOrEmpty(display.Unit)
                        ? display.Value
                        : $"{display.Value} {display.Unit}";
                    break;
                }
            }
        }

        var root = new JsonObject
        {
            ["message"] = message.Name,
            ["signal"] = signal.Name,
            ["start_bit"] = signal.StartBit,
            ["length"] = signal.Length,
            ["factor"] = signal.Factor,
            ["offset"] = signal.Offset,
            ["unit"] = signal.Unit,
            ["anchor_timestamp"] = anchorTs,
            ["anchor_value"] = anchorValue,
        };
        return root.ToJsonString();
    }
}
