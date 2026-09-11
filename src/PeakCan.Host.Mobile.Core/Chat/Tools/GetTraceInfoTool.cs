using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.Mobile.Core.Chat.Tools;

/// <summary>
/// Tool <c>get_trace_info</c> - trace session metadata (source, duration,
/// current playback time, DBC status, anchor, filter).
/// </summary>
public sealed class GetTraceInfoTool : MobileChatToolBase
{
    private const string DefinitionSchema =
        """{"type":"object","properties":{},"additionalProperties":false}""";

    private readonly IMobileChatToolContext _context;

    public GetTraceInfoTool(IMobileChatToolContext context, ILogger logger)
        : base(
            "get_trace_info",
            "Get metadata about the currently open trace session: source name, duration (when known), current playback timestamp, whether a DBC is loaded, whether an anchor is set, and the current ID/PGN filter. Use at the start of a diagnostic session to understand what you're working with.",
            DefinitionSchema,
            logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    protected override Task<string> ExecuteCoreAsync(string argsJson, CancellationToken ct)
    {
        var current = _context.CurrentTimestamp;
        var root = new JsonObject
        {
            ["source_name"] = _context.SourceName,
            ["duration_known"] = _context.IsDurationKnown,
            ["duration_seconds"] = _context.IsDurationKnown ? _context.DurationSeconds : null,
            ["current_timestamp"] = NanOrNull(current),
            ["current_timestamp_s"] = TsLabel(current),
            ["dbc_loaded"] = _context.Dbc is not null,
            ["dbc_name"] = _context.Dbc?.SourceName,
            ["anchor_set"] = _context.HasAnchor,
            ["anchor_timestamp"] = _context.AnchorTimestamp,
            ["filter"] = _context.FilterText,
        };
        return Task.FromResult(root.ToJsonString());
    }
}
