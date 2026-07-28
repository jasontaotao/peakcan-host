using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core.Analysis;
using PeakCan.Host.Core.Analysis.Chat;

namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>
/// v12 Step 3: Tool <c>get_trace_info</c> - returns trace session metadata
/// (duration, sources, DBC status, current timestamp).
/// </summary>
public sealed class GetTraceInfoTool : ChatToolBase
{
    private const string DefinitionSchema =
        """{"type":"object","properties":{},"additionalProperties":false}""";

    private readonly IChatToolContext _context;

    public GetTraceInfoTool(IChatToolContext context, ILogger<GetTraceInfoTool> logger)
        : base(
            "get_trace_info",
            "Get metadata about the currently loaded trace session: total duration, number of sources, whether a DBC is loaded, current playback timestamp, and per-source details. Use at the start of a diagnostic session to understand what you're working with.",
            DefinitionSchema,
            logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    protected override Task<string> ExecuteCoreAsync(string argsJson, CancellationToken ct)
    {
        var info = _context.GetTraceInfo();
        var sources = new JsonArray();
        foreach (var s in info.Sources)
        {
            sources.Add(new JsonObject
            {
                ["source_id"] = s.SourceId,
                ["display_name"] = s.DisplayName,
                ["path"] = s.Path,
                ["frame_count"] = s.FrameCount,
                ["can_id_filter"] = s.CanIdFilter,
            });
        }

        var root = new JsonObject
        {
            ["total_duration"] = info.TotalDuration,
            ["source_count"] = info.SourceCount,
            ["dbc_loaded"] = info.DbcLoaded,
            ["dbc_path"] = info.DbcPath,
            ["current_timestamp"] = info.CurrentTimestamp,
            ["current_timestamp_label"] = TraceTimeFormatter.Format(info.CurrentTimestamp, info.WallClockOrigin),
            ["wall_clock_origin"] = info.WallClockOrigin?.ToString("o"),
            ["sources"] = sources,
        };
        return Task.FromResult(root.ToJsonString());
    }
}
