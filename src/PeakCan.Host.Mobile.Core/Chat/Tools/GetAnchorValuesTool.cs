using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Mobile.Core.Models;

namespace PeakCan.Host.Mobile.Core.Chat.Tools;

/// <summary>
/// Tool <c>get_anchor_values</c> - all decoded signal values at the current
/// anchor timestamp (zero-order hold snapshot from the replay cache). This
/// is the mobile counterpart of the desktop watch-list anchor read; mobile
/// has a single anchor, so the tool returns the full table rather than a
/// user-selected subset.
/// </summary>
public sealed class GetAnchorValuesTool : MobileChatToolBase
{
    private const string DefinitionSchema =
        """{"type":"object","properties":{},"additionalProperties":false}""";

    private readonly IMobileChatToolContext _context;

    public GetAnchorValuesTool(IMobileChatToolContext context, ILogger logger)
        : base(
            "get_anchor_values",
            "Get every decoded signal value at the current anchor timestamp (zero-order hold from the replay cache). Call after the user has placed an anchor, to read the state of all signals at that moment. Returns one entry per signal; frames without a DBC definition appear as raw hex.",
            DefinitionSchema,
            logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    protected override async Task<string> ExecuteCoreAsync(string argsJson, CancellationToken ct)
    {
        if (!_context.HasAnchor || _context.AnchorTimestamp is not { } a)
            return """{"error":"no anchor set"}""";

        var dbc = _context.Dbc;
        var frames = await _context.GetFramesBeforeAsync(a, ct).ConfigureAwait(false);
        var signals = new JsonArray();
        foreach (var frame in frames)
        {
            var idText = FrameRow.FromCached(frame).IdText;
            var decode = dbc?.Decode(frame.CanId, frame.IsExtended, frame.Data, frame.Dlc);
            if (decode is null)
            {
                signals.Add(new JsonObject
                {
                    ["can_id"] = idText,
                    ["message"] = null,
                    ["signal"] = null,
                    ["value"] = null,
                    ["unit"] = null,
                    ["raw"] = FrameRow.FromCached(frame).DataText,
                });
                continue;
            }

            foreach (var s in decode.Signals)
            {
                signals.Add(new JsonObject
                {
                    ["can_id"] = idText,
                    ["message"] = decode.MessageName,
                    ["signal"] = s.Name,
                    ["value"] = s.Value,
                    ["unit"] = s.Unit,
                });
            }
        }

        var root = new JsonObject
        {
            ["anchor_timestamp"] = a,
            ["frame_count"] = frames.Count,
            ["signal_count"] = signals.Count,
            ["signals"] = signals,
            ["message"] = frames.Count == 0 ? "该区域尚未缓存" : null,
        };
        return root.ToJsonString();
    }
}
