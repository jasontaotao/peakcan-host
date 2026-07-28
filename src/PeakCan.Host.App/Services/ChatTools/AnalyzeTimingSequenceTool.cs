using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core.Analysis;
using PeakCan.Host.Core.Analysis.Chat;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>
/// v12 Step 3: Tool <c>analyze_timing_sequence</c> - extract the
/// value-change event chain for multiple signals over a time window.
/// Events are sorted by timestamp to reveal temporal causality.
/// </summary>
public sealed class AnalyzeTimingSequenceTool : ChatToolBase
{
    private const string DefinitionSchema =
        """{"type":"object","properties":{"signal_keys":{"type":"array","items":{"type":"string","pattern":"^0x[0-9A-Fa-f]+\\\\.[A-Za-z0-9_]+(\\\\.[A-Za-z0-9_-]+)?$"},"minItems":1,"maxItems":8,"description":"Signal keys in format CAN_ID_HEX.SignalName[.SourceId]. SourceId is optional. Use search_signals or anomaly_scan to discover keys."},"t_start":{"type":"number","description":"Window start time in seconds."},"t_end":{"type":"number","description":"Window end time in seconds."},"detect_types":{"type":"array","items":{"type":"string","enum":["sharp_drop","sharp_rise","step_change","jitter_start","jitter_stop","value_appeared","value_disappeared","flatline"]},"description":"Optional filter: only detect specific event types. Omit to detect all."}},"required":["signal_keys","t_start","t_end"],"additionalProperties":false}""";

    private readonly IChatToolContext _context;

    public AnalyzeTimingSequenceTool(IChatToolContext context, ILogger<AnalyzeTimingSequenceTool> logger)
        : base(
            "analyze_timing_sequence",
            "Analyze the timing chain of value-change events for multiple signals over a time window. Returns events sorted by timestamp with type, from/to values, and a human-readable sequence summary. Use AFTER adding signals to watch list to understand the temporal causality chain (e.g. 'voltage dropped first, then fault bit set, then power limited'). Each event is a real value change - no downsampling, all transitions preserved.",
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

        double tStart = args["t_start"]?.GetValue<double>() ?? 0;
        double tEnd = args["t_end"]?.GetValue<double>() ?? 0;
        if (tEnd <= tStart)
            return Task.FromResult("""{"error":"t_end must be greater than t_start"}""");

        // Optional type filter.
        HashSet<string>? detectTypes = null;
        var typesNode = args["detect_types"]?.AsArray();
        if (typesNode is not null)
        {
            detectTypes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in typesNode)
                detectTypes.Add(item?.GetValue<string>() ?? "");
        }

        var traceInfo = _context.GetTraceInfo();
        if (traceInfo.SourceCount == 0)
            return Task.FromResult("""{"error":"no trace loaded"}""");

        var allFrames = new List<ReplayFrame>();
        foreach (var src in traceInfo.Sources)
            allFrames.AddRange(_context.GetFrames(src.SourceId));

        var allEvents = new List<(double T, string Key, string Type, double From, double To, string Desc)>();

        foreach (var key in keys)
        {
            if (!GetSignalOverviewTool.TryResolveSignal(dbc, key, out var msg, out var sig, out var canId))
                continue;

            var decoded = GetSignalOverviewTool.DecodeSignalFrames(allFrames, canId, sig, ct);
            var windowed = decoded.Where(d => d.T >= tStart && d.T <= tEnd).ToList();
            if (windowed.Count < 2) continue;

            DetectEvents(key, windowed, detectTypes, allEvents);
        }

        // Sort by timestamp.
        allEvents.Sort((a, b) => a.T.CompareTo(b.T));

        var eventsJson = new JsonArray();
        foreach (var e in allEvents)
        {
            eventsJson.Add(new JsonObject
            {
                ["t"] = Math.Round(e.T, 4),
                ["t_label"] = TraceTimeFormatter.Format(e.T, traceInfo.WallClockOrigin),
                ["signal_key"] = e.Key,
                ["type"] = e.Type,
                ["from"] = Math.Round(e.From, 4),
                ["to"] = Math.Round(e.To, 4),
                ["description"] = e.Desc,
            });
        }

        // Build sequence summary.
        var summary = allEvents.Count > 0
            ? string.Join(" -> ", allEvents.Select(e => $"{e.T:F4} {e.Key} {e.Type}"))
            : "No events detected in window.";

        var root = new JsonObject
        {
            ["window"] = new JsonObject { ["t_start"] = tStart, ["t_start_label"] = TraceTimeFormatter.Format(tStart, traceInfo.WallClockOrigin), ["t_end"] = tEnd, ["t_end_label"] = TraceTimeFormatter.Format(tEnd, traceInfo.WallClockOrigin) },
            ["signal_count"] = keys.Count,
            ["total_events"] = allEvents.Count,
            ["events"] = eventsJson,
            ["sequence_summary"] = summary,
        };
        return Task.FromResult(root.ToJsonString());
    }

    private static void DetectEvents(
        string key,
        List<(double T, double V)> data,
        HashSet<string>? detectTypes,
        List<(double T, string Key, string Type, double From, double To, string Desc)> events)
    {
        bool IsEnabled(string type) => detectTypes is null || detectTypes.Contains(type);

        double range = data.Max(d => d.V) - data.Min(d => d.V);
        double threshold = range > 0 ? range * 0.05 : 0;  // 5% of range

        for (int i = 1; i < data.Count; i++)
        {
            var (t, v) = data[i];
            var (prevT, prevV) = data[i - 1];
            double delta = v - prevV;

            // step_change: discrete value change or large jump.
            if (IsEnabled("step_change") && v != prevV)
            {
                double relDelta = range > 0 ? Math.Abs(delta) / range : 1;
                if (relDelta > 0.1 || v != prevV)
                {
                    events.Add((t, key, "step_change", prevV, v,
                        $"{key}: {prevV} -> {v}"));
                    continue;
                }
            }

            // sharp_drop / sharp_rise: check for monotonic run.
            if (i >= 3 && (IsEnabled("sharp_drop") || IsEnabled("sharp_rise")))
            {
                double runStart = data[i - 3].V;
                double runDelta = v - runStart;
                bool monotonic = (delta < 0 && data[i - 1].V < data[i - 2].V && data[i - 2].V < data[i - 3].V) ||
                                  (delta > 0 && data[i - 1].V > data[i - 2].V && data[i - 2].V > data[i - 3].V);
                if (monotonic && Math.Abs(runDelta) > threshold)
                {
                    if (runDelta < 0 && IsEnabled("sharp_drop"))
                    {
                        events.Add((data[i - 3].T, key, "sharp_drop", runStart, v,
                            $"{key}: {runStart} -> {v} (drop)"));
                        continue;
                    }
                    if (runDelta > 0 && IsEnabled("sharp_rise"))
                    {
                        events.Add((data[i - 3].T, key, "sharp_rise", runStart, v,
                            $"{key}: {runStart} -> {v} (rise)"));
                        continue;
                    }
                }
            }

            // value_appeared: was zero/default, now non-zero.
            if (IsEnabled("value_appeared") && prevV == 0 && v != 0)
            {
                events.Add((t, key, "value_appeared", prevV, v,
                    $"{key}: appeared ({v})"));
                continue;
            }

            // value_disappeared: was non-zero, now zero.
            if (IsEnabled("value_disappeared") && prevV != 0 && v == 0)
            {
                events.Add((t, key, "value_disappeared", prevV, v,
                    $"{key}: disappeared"));
                continue;
            }

            // flatline: was changing, now stuck.
            if (IsEnabled("flatline") && i >= 3 && v == prevV &&
                data[i - 1].V == data[i - 2].V &&
                data[i - 2].V != data[i - 3].V)
            {
                events.Add((t, key, "flatline", prevV, v,
                    $"{key}: flatlined at {v}"));
                continue;
            }
        }
    }
}
