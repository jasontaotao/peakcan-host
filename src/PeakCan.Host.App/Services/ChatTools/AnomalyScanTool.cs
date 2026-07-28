using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core.Analysis;
using PeakCan.Host.Core.Analysis.Chat;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>
/// v12 Step 3: Tool <c>anomaly_scan</c> - scan a time window for signals
/// that behave differently from the rest of the trace. Two-stage strategy
/// (v12 C3): stage 1 coarse-filters by CAN ID frame-rate change, stage 2
/// decodes only the filtered CAN IDs' signals.
/// </summary>
public sealed class AnomalyScanTool : ChatToolBase
{
    private const string DefinitionSchema =
        """{"type":"object","properties":{"t_start":{"type":"number","description":"Window start time in seconds."},"t_end":{"type":"number","description":"Window end time in seconds."},"max_results":{"type":"integer","minimum":1,"maximum":50,"default":20,"description":"Max anomalous signals to return. Default 20."}},"required":["t_start","t_end"],"additionalProperties":false}""";

    private readonly IChatToolContext _context;

    public AnomalyScanTool(IChatToolContext context, ILogger<AnomalyScanTool> logger)
        : base(
            "anomaly_scan",
            "Scan a time window for signals that behave differently from the rest of the trace. Compares per-signal statistics (mean, min, max, transition count) in the window against the baseline outside it. Returns ranked anomalies. Use when the user highlights a suspicious time region but doesn't know which signals to investigate.",
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
        double tStart = args["t_start"]?.GetValue<double>() ?? 0;
        double tEnd = args["t_end"]?.GetValue<double>() ?? 0;
        if (tEnd <= tStart)
            return Task.FromResult("""{"error":"t_end must be greater than t_start"}""");

        int maxResults = args["max_results"]?.GetValue<int>() ?? 20;
        maxResults = Math.Clamp(maxResults, 1, 50);

        var traceInfo = _context.GetTraceInfo();
        if (traceInfo.SourceCount == 0)
            return Task.FromResult("""{"error":"no trace loaded"}""");

        // Check window vs trace bounds.
        if (tStart <= traceInfo.TotalDuration * 0.05 &&
            tEnd >= traceInfo.TotalDuration * 0.95)
        {
            return Task.FromResult(
                """{"error":"window covers entire trace","hint":"无基线可对比，请缩小时间窗口"}""");
        }

        // Gather all frames.
        var allFrames = new List<ReplayFrame>();
        foreach (var src in traceInfo.Sources)
            allFrames.AddRange(_context.GetFrames(src.SourceId));

        // === Stage 1: Coarse filter by CAN ID frame-rate change ===
        var windowFrames = new List<ReplayFrame>();
        var baselineFrames = new List<ReplayFrame>();
        foreach (var f in allFrames)
        {
            if (f.Timestamp >= tStart && f.Timestamp <= tEnd)
                windowFrames.Add(f);
            else
                baselineFrames.Add(f);
        }

        double windowDuration = tEnd - tStart;
        double baselineDuration = traceInfo.TotalDuration - windowDuration;
        if (baselineDuration <= 0)
            return Task.FromResult("""{"error":"baseline too short","hint":"请缩小时间窗口"}""");

        // Count frames per CAN ID in window vs baseline.
        var windowById = new Dictionary<uint, int>();
        var baselineById = new Dictionary<uint, int>();
        foreach (var f in windowFrames)
        {
            var id = f.Id & 0x7FFFFFFFu;
            windowById[id] = windowById.TryGetValue(id, out var c) ? c + 1 : 1;
        }
        foreach (var f in baselineFrames)
        {
            var id = f.Id & 0x7FFFFFFFu;
            baselineById[id] = baselineById.TryGetValue(id, out var c) ? c + 1 : 1;
        }

        // Filter: CAN IDs present in window AND with frame-rate change > 50%.
        var candidateIds = new List<uint>();
        foreach (var (id, winCount) in windowById)
        {
            double winRate = winCount / windowDuration;
            int baseCount = baselineById.TryGetValue(id, out var bc) ? bc : 0;
            double baseRate = baseCount / baselineDuration;
            if (baseRate <= 0 || Math.Abs(winRate - baseRate) / baseRate > 0.5)
                candidateIds.Add(id);
        }
        // Fallback: if coarse filter found nothing, use all window CAN IDs.
        if (candidateIds.Count == 0)
            candidateIds.AddRange(windowById.Keys);

        // === Stage 2: Decode signals for candidate CAN IDs ===
        var changes = new List<(double Score, string ChangeType, Message Msg, Signal Sig,
            double WinMean, double WinMin, double WinMax, int WinTrans,
            double BaseMean, double BaseMin, double BaseMax, int BaseTrans)>();

        foreach (var msg in dbc.Messages)
        {
            uint maskedId = msg.Id & 0x7FFFFFFFu;
            if (!candidateIds.Contains(maskedId)) continue;

            foreach (var sig in msg.Signals)
            {
                ct.ThrowIfCancellationRequested();

                var winDecoded = GetSignalOverviewTool.DecodeSignalFrames(windowFrames, msg.Id, sig, ct);
                var baseDecoded = GetSignalOverviewTool.DecodeSignalFrames(baselineFrames, msg.Id, sig, ct);

                if (winDecoded.Count == 0) continue;

                double winMean = winDecoded.Average(d => d.V);
                double winMin = winDecoded.Min(d => d.V);
                double winMax = winDecoded.Max(d => d.V);
                int winTrans = CountTransitions(winDecoded);

                if (baseDecoded.Count == 0)
                {
                    // Signal appeared in window but not in baseline.
                    changes.Add((0.85, "value_appeared", msg, sig,
                        winMean, winMin, winMax, winTrans,
                        0, 0, 0, 0));
                    continue;
                }

                double baseMean = baseDecoded.Average(d => d.V);
                double baseMin = baseDecoded.Min(d => d.V);
                double baseMax = baseDecoded.Max(d => d.V);
                int baseTrans = CountTransitions(baseDecoded);

                // Skip signals with no change.
                if (Math.Abs(winMean - baseMean) < 1e-9 && winTrans == baseTrans)
                    continue;

                double score = ComputeChangeScore(winMean, baseMean, winMin, winMax, baseMin, baseMax, winTrans, baseTrans);
                if (score <= 0) continue;

                string changeType = ClassifyChange(winMean, baseMean, winTrans, baseTrans);
                changes.Add((score, changeType, msg, sig,
                    winMean, winMin, winMax, winTrans,
                    baseMean, baseMin, baseMax, baseTrans));
            }
        }

        // Sort by score descending, take top N.
        changes.Sort((a, b) => b.Score.CompareTo(a.Score));
        var top = changes.Take(maxResults).ToList();

        var changesJson = new JsonArray();
        for (int i = 0; i < top.Count; i++)
        {
            var c = top[i];
            var canIdHex = FindRelatedSignalsTool.FormatCanId(c.Msg.Id);
            changesJson.Add(new JsonObject
            {
                ["rank"] = i + 1,
                ["signal_key"] = $"{canIdHex}.{c.Sig.Name}",
                ["signal_name"] = c.Sig.Name,
                ["unit"] = c.Sig.Unit,
                ["change_type"] = c.ChangeType,
                ["change_score"] = Math.Round(c.Score, 2),
                ["window"] = new JsonObject
                {
                    ["mean"] = Math.Round(c.WinMean, 4),
                    ["min"] = Math.Round(c.WinMin, 4),
                    ["max"] = Math.Round(c.WinMax, 4),
                    ["transitions"] = c.WinTrans,
                },
                ["baseline"] = new JsonObject
                {
                    ["mean"] = Math.Round(c.BaseMean, 4),
                    ["min"] = Math.Round(c.BaseMin, 4),
                    ["max"] = Math.Round(c.BaseMax, 4),
                    ["transitions"] = c.BaseTrans,
                },
            });
        }

        var root = new JsonObject
        {
            ["window"] = new JsonObject { ["t_start"] = tStart, ["t_start_label"] = TraceTimeFormatter.Format(tStart, traceInfo.WallClockOrigin), ["t_end"] = tEnd, ["t_end_label"] = TraceTimeFormatter.Format(tEnd, traceInfo.WallClockOrigin), ["frame_count"] = windowFrames.Count },
            ["total_signals_scanned"] = changes.Count,
            ["changed_signal_count"] = changes.Count,
            ["top_changes"] = changesJson,
        };
        return Task.FromResult(root.ToJsonString());
    }

    private static int CountTransitions(List<(double T, double V)> data)
    {
        int count = 0;
        for (int i = 1; i < data.Count; i++)
            if (data[i].V != data[i - 1].V) count++;
        return count;
    }

    private static double ComputeChangeScore(
        double winMean, double baseMean,
        double winMin, double winMax,
        double baseMin, double baseMax,
        int winTrans, int baseTrans)
    {
        double range = Math.Max(Math.Abs(baseMax - baseMin), 1e-9);
        double meanShift = Math.Abs(winMean - baseMean) / range;

        double transChange = baseTrans > 0
            ? Math.Abs(winTrans - baseTrans) / (double)baseTrans
            : (winTrans > 0 ? 1.0 : 0.0);

        // Weighted combination: mean shift 70%, transition change 30%.
        return Math.Min(meanShift * 0.7 + transChange * 0.3, 1.0);
    }

    private static string ClassifyChange(double winMean, double baseMean, int winTrans, int baseTrans)
    {
        if (baseTrans == 0 && winTrans > 0) return "value_appeared";
        if (winTrans == 0 && baseTrans > 0) return "value_disappeared";
        if (Math.Abs(winTrans - baseTrans) > baseTrans * 0.5) return "transition_change";
        if (winMean != baseMean) return "mean_shift";
        return "jitter_change";
    }
}
