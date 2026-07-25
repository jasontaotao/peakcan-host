using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core.Analysis.Chat;

namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>
/// Tool <c>get_anchor_info</c>: reads the current watch list's green/blue/Δ
/// values for the assistant.
/// </summary>
/// <remarks>
/// <b>HIGH-1 fix (spec v2 patch):</b> reads <see cref="IChatToolContext.WatchedSignals"/>
/// row properties directly (<c>LatestValue</c>/<c>BlueLatestValue</c>/<c>DeltaValue</c>/
/// <c>LatestText</c>/<c>BlueText</c>/<c>DeltaText</c>) + the VM's anchor timestamps.
/// Does NOT read <c>CurrentAnchorSnapshot</c> - that snapshot is only populated by
/// <c>LockAnchor()</c> and does not update when <c>RefreshAtAnchor</c> runs, so
/// relying on it would never surface newly-added signals' anchor values.
/// </remarks>
public sealed class GetAnchorInfoTool : ChatToolBase
{
    private const string DefinitionSchema =
        """{"type":"object","properties":{},"additionalProperties":false}""";

    private readonly IChatToolContext _context;

    public GetAnchorInfoTool(IChatToolContext context, ILogger<GetAnchorInfoTool> logger)
        : base(
            "get_anchor_info",
            "Read the current watch list's green-anchor value, blue-anchor value, and delta (Δ) for each watched signal. Call after propose_to_watch_list to see the newly-added signals' anchor values.",
            DefinitionSchema,
            logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    protected override Task<string> ExecuteCoreAsync(string argsJson, CancellationToken ct)
    {
        var root = new JsonObject
        {
            ["green_ts"] = NanOrNull(_context.AnchorTimestampSeconds),
            ["blue_ts"] = NanOrNull(_context.BlueAnchorTimestampSeconds),
        };

        var signals = new JsonArray();
        foreach (var row in _context.WatchedSignals)
        {
            if (row.IsPlaceholder) continue;
            signals.Add(new JsonObject
            {
                ["key"] = row.SignalKey,
                ["latest"] = NanOrNull(row.LatestValue),
                ["blue"] = NanOrNull(row.BlueLatestValue),
                ["delta"] = NanOrNull(row.DeltaValue),
                ["latest_text"] = row.LatestText,
                ["blue_text"] = row.BlueText,
                ["delta_text"] = row.DeltaText,
            });
        }

        root["signal_count"] = signals.Count;
        root["signals"] = signals;

        return Task.FromResult(root.ToJsonString());
    }

    /// <summary>JSON has no NaN; emit null for unset anchor/value slots.</summary>
    private static JsonNode? NanOrNull(double v) => double.IsNaN(v) ? null : v;
}
