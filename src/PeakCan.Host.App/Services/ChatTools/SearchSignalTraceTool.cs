using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core.Analysis;
using PeakCan.Host.Core.Analysis.Chat;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>
/// v12 Step 3: Tool <c>search_signal_trace</c> - extract time-series
/// data for signals over a time window with LTTB downsampling.
/// </summary>
public sealed class SearchSignalTraceTool : ChatToolBase
{
    private const string DefinitionSchema =
        """{"type":"object","properties":{"signal_keys":{"type":"array","items":{"type":"string","pattern":"^0x[0-9A-Fa-f]+\\\\.[A-Za-z0-9_]+(\\\\.[A-Za-z0-9_-]+)?$"},"minItems":1,"maxItems":8,"description":"Signal keys in format CAN_ID_HEX.SignalName[.SourceId]. SourceId is optional (for multi-source traces, use the key returned by get_anchor_info or search_signals). Use search_signals to discover keys."},"t_start":{"type":"number","description":"Window start time in seconds."},"t_end":{"type":"number","description":"Window end time in seconds."},"window_ref":{"type":"string","enum":["absolute","green_anchor","blue_anchor"],"default":"absolute","description":"Reference mode."},"max_points":{"type":"integer","minimum":10,"maximum":1000,"default":200,"description":"Target sample count per signal."}},"required":["signal_keys"],"additionalProperties":false}""";

    private readonly IChatToolContext _context;

    public SearchSignalTraceTool(IChatToolContext context, ILogger<SearchSignalTraceTool> logger)
        : base(
            "search_signal_trace",
            "Extract time-series data for given signals over a time window. Returns sampled physical values at uniform intervals with statistics. Use for timing analysis, transition detection, and correlating multiple signals. Call get_signal_overview FIRST to identify where to zoom in.",
            DefinitionSchema,
            logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    protected override Task<string> ExecuteCoreAsync(string argsJson, CancellationToken ct)
    {
        var dbc = _context.CurrentDbc;
        if (dbc is null)
            return Task.FromResult("""{"error":"no DBC loaded"}""");

        var args = ParseArgs(argsJson);
        var keys = GetSignalOverviewTool.ParseSignalKeys(args);
        if (keys.Count == 0)
            return Task.FromResult("""{"error":"missing 'signal_keys'"}""");

        var traceInfo = _context.GetTraceInfo();
        if (traceInfo.SourceCount == 0)
            return Task.FromResult("""{"error":"no trace loaded"}""");

        // Resolve window bounds.
        double tStart = args["t_start"]?.GetValue<double>() ?? 0;
        double tEnd = args["t_end"]?.GetValue<double>() ?? traceInfo.TotalDuration;
        string windowRef = args["window_ref"]?.GetValue<string>() ?? "absolute";

        if (windowRef == "green_anchor")
        {
            if (double.IsNaN(_context.AnchorTimestampSeconds))
                return Task.FromResult("""{"error":"anchor not set","hint":"请先设置绿锚，或使用 absolute 模式"}""");
            tStart += _context.AnchorTimestampSeconds;
            tEnd += _context.AnchorTimestampSeconds;
        }
        else if (windowRef == "blue_anchor")
        {
            if (double.IsNaN(_context.BlueAnchorTimestampSeconds))
                return Task.FromResult("""{"error":"anchor not set","hint":"请先设置蓝锚，或使用 absolute 模式"}""");
            tStart += _context.BlueAnchorTimestampSeconds;
            tEnd += _context.BlueAnchorTimestampSeconds;
        }

        int maxPoints = args["max_points"]?.GetValue<int>() ?? 200;
        maxPoints = Math.Clamp(maxPoints, 10, 1000);

        // Gather all frames.
        var allFrames = new List<ReplayFrame>();
        foreach (var src in traceInfo.Sources)
            allFrames.AddRange(_context.GetFrames(src.SourceId));

        var signals = new JsonArray();
        foreach (var key in keys)
        {
            if (!GetSignalOverviewTool.TryResolveSignal(dbc, key, out var msg, out var sig, out var canId))
            {
                signals.Add(new JsonObject { ["key"] = key, ["error"] = "signal not found" });
                continue;
            }

            // Decode + window filter.
            var decoded = GetSignalOverviewTool.DecodeSignalFrames(allFrames, canId, sig, ct);
            var windowed = decoded.Where(d => d.T >= tStart && d.T <= tEnd).ToList();
            if (windowed.Count == 0)
            {
                signals.Add(new JsonObject { ["key"] = key, ["error"] = "no frames in window" });
                continue;
            }

            // LTTB downsample.
            var downsampled = LttbDownsampler.Downsample(
                windowed.Select(d => (d.T, d.V)).ToList(),
                maxPoints);

            // Statistics on windowed data.
            var stats = GetSignalOverviewTool.ComputeStatistics(windowed);
            stats["key"] = key;
            stats["unit"] = sig.Unit;
            stats["sample_count"] = downsampled.Count;

            var samples = new JsonArray();
            foreach (var (t, v) in downsampled)
                samples.Add(new JsonObject { ["t"] = Math.Round(t, 4), ["v"] = Math.Round(v, 4) });

            stats["samples"] = samples;
            stats["t_range"] = new JsonObject { ["start"] = windowed[0].T, ["end"] = windowed[^1].T };
            signals.Add(stats);
        }

        var root = new JsonObject
        {
            ["signals"] = signals,
            ["backend_info"] = new JsonObject
            {
                ["raw_frame_count"] = allFrames.Count,
                ["downsample_method"] = "LTTB",
                ["window_ref"] = windowRef,
                ["t_start"] = tStart,
                ["t_end"] = tEnd,
            },
        };
        return Task.FromResult(root.ToJsonString());
    }
}
