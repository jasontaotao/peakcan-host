using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core.Analysis.Chat;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.Replay;

namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>
/// v12 Step 3: Tool <c>get_signal_overview</c> - real-time lifecycle
/// statistics for signals over the entire trace. Decodes every frame
/// per signal to compute min/max/timestamps/transitions/events.
/// </summary>
/// <remarks>
/// No pre-computed cache. Performance: 5-8 signals x 100K frames < 1s;
/// 8 signals x 1M frames < 5s (SignalDecoder.Decode < 1us per frame).
/// </remarks>
public sealed class GetSignalOverviewTool : ChatToolBase
{
    private const string DefinitionSchema =
        """{"type":"object","properties":{"signal_keys":{"type":"array","items":{"type":"string","pattern":"^0x[0-9A-Fa-f]+\\\\.[A-Za-z0-9_]+(\\\\.[A-Za-z0-9_-]+)?$"},"minItems":1,"maxItems":8,"description":"Signal keys in format CAN_ID_HEX.SignalName[.SourceId]. SourceId is optional (for multi-source traces, use the key returned by get_anchor_info or search_signals). Use search_signals to discover keys."}},"required":["signal_keys"],"additionalProperties":false}""";

    private readonly IChatToolContext _context;

    public GetSignalOverviewTool(IChatToolContext context, ILogger<GetSignalOverviewTool> logger)
        : base(
            "get_signal_overview",
            "Get lifecycle statistics for signals over the entire trace. Returns min/max with timestamps, trend, transition count, and detected events. Use BEFORE search_signal_trace to identify WHERE to zoom in.",
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
        var keys = ParseSignalKeys(args);
        if (keys.Count == 0)
            return Task.FromResult("""{"error":"missing or invalid 'signal_keys'"}""");

        // Get all frames across all sources.
        var traceInfo = _context.GetTraceInfo();
        var allFrames = new List<ReplayFrame>();
        foreach (var src in traceInfo.Sources)
            allFrames.AddRange(_context.GetFrames(src.SourceId));

        if (allFrames.Count == 0)
            return Task.FromResult("""{"error":"no trace loaded"}""");

        double tMin = allFrames[0].Timestamp;
        double tMax = allFrames[^1].Timestamp;

        var signals = new JsonArray();
        foreach (var key in keys)
        {
            if (!TryResolveSignal(dbc, key, out var msg, out var sig, out var canId))
            {
                signals.Add(new JsonObject { ["key"] = key, ["error"] = "signal not found" });
                continue;
            }

            var decoded = DecodeSignalFrames(allFrames, canId, sig, ct);
            if (decoded.Count == 0)
            {
                signals.Add(new JsonObject { ["key"] = key, ["error"] = "no frames for this signal" });
                continue;
            }

            var stats = ComputeStatistics(decoded);
            stats["key"] = key;
            stats["unit"] = sig.Unit;
            stats["total_frames"] = decoded.Count;
            signals.Add(stats);
        }

        var root = new JsonObject
        {
            ["window"] = new JsonObject
            {
                ["t_min"] = tMin,
                ["t_max"] = tMax,
                ["frame_count"] = allFrames.Count,
            },
            ["signals"] = signals,
        };
        return Task.FromResult(root.ToJsonString());
    }

    // === Shared decode + statistics logic (reused by SearchSignalTraceTool) ===

    internal static List<(double T, double V)> DecodeSignalFrames(
        IReadOnlyList<ReplayFrame> frames, uint canId, Signal sig, CancellationToken ct)
    {
        var result = new List<(double T, double V)>();
        // Strip IDE bit for matching (DBC stores extended IDs with bit 31 set,
        // ASC frame ids are 29-bit without IDE bit).
        uint maskedId = canId & 0x7FFFFFFFu;
        foreach (var f in frames)
        {
            if ((f.Id & 0x7FFFFFFFu) != maskedId)
                continue;
            double v = SignalDecoder.Decode(f.Data, sig);
            result.Add((f.Timestamp, v));
        }
        return result;
    }

    internal static JsonObject ComputeStatistics(List<(double T, double V)> decoded)
    {
        double first = decoded[0].V, firstT = decoded[0].T;
        double last = decoded[^1].V, lastT = decoded[^1].T;
        double min = double.MaxValue, minT = 0, max = double.MinValue, maxT = 0;
        double sum = 0;
        int transitions = 0;
        var events = new JsonArray();

        for (int i = 0; i < decoded.Count; i++)
        {
            var (t, v) = decoded[i];
            sum += v;
            if (v < min) { min = v; minT = t; }
            if (v > max) { max = v; maxT = t; }
            if (i > 0 && decoded[i].V != decoded[i - 1].V)
                transitions++;
        }

        double mean = sum / decoded.Count;

        // Detect significant events (sharp drops/rises, step changes).
        for (int i = 1; i < decoded.Count; i++)
        {
            var (t, v) = decoded[i];
            var (_, prevV) = decoded[i - 1];
            double range = max - min;
            if (range <= 0) continue;
            double delta = v - prevV;
            double relDelta = Math.Abs(delta) / range;

            if (relDelta > 0.1)
            {
                string type = delta < 0 ? "sharp_drop" : "sharp_rise";
                events.Add(new JsonObject
                {
                    ["type"] = type,
                    ["t"] = t,
                    ["from"] = prevV,
                    ["to"] = v,
                });
            }
        }

        string trend = DetermineTrend(decoded, min, max);

        return new JsonObject
        {
            ["statistics"] = new JsonObject
            {
                ["first"] = first,
                ["first_t"] = firstT,
                ["last"] = last,
                ["last_t"] = lastT,
                ["min"] = min,
                ["min_t"] = minT,
                ["max"] = max,
                ["max_t"] = maxT,
                ["mean"] = Math.Round(mean, 4),
                ["transition_count"] = transitions,
                ["trend"] = trend,
            },
            ["events"] = events,
        };
    }

    private static string DetermineTrend(List<(double T, double V)> decoded, double min, double max)
    {
        if (max == min) return "stable";
        int n = decoded.Count;
        int halfN = n / 2;
        double firstHalfMean = 0, secondHalfMean = 0;
        for (int i = 0; i < halfN; i++) firstHalfMean += decoded[i].V;
        for (int i = halfN; i < n; i++) secondHalfMean += decoded[i].V;
        firstHalfMean /= halfN > 0 ? halfN : 1;
        secondHalfMean /= (n - halfN) > 0 ? (n - halfN) : 1;

        double range = max - min;
        if (Math.Abs(secondHalfMean - firstHalfMean) < range * 0.05)
            return "stable";
        return secondHalfMean > firstHalfMean ? "rising" : "falling";
    }

    // === Signal key parsing (shared pattern) ===

    internal static List<string> ParseSignalKeys(JsonObject args)
    {
        var keysNode = args["signal_keys"]?.AsArray();
        if (keysNode is null) return new List<string>();
        var keys = new List<string>();
        foreach (var item in keysNode)
        {
            var k = item?.GetValue<string>() ?? "";
            if (!string.IsNullOrWhiteSpace(k))
                keys.Add(k);
        }
        return keys;
    }

    internal static bool TryResolveSignal(
        DbcDocument dbc, string signalKey,
        out Message msg, out Signal sig, out uint canId)
    {
        msg = null!;
        sig = null!;
        canId = 0;
        var dot1 = signalKey.IndexOf('.');
        if (dot1 <= 0) return false;
        var canIdHex = signalKey[..dot1];
        var rest = signalKey[(dot1 + 1)..];
        var dot2 = rest.IndexOf('.');
        var signalName = dot2 < 0 ? rest : rest[..dot2];

        if (!FindRelatedSignalsTool.TryParseCanId(canIdHex, out canId))
            return false;
        // DBC stores extended IDs with IDE bit set; mask to match.
        if (!dbc.MessagesById.TryGetValue(canId, out msg))
        {
            // Try with IDE bit stripped (DBC convention).
            if (!dbc.MessagesById.TryGetValue(canId & 0x7FFFFFFFu, out msg))
                return false;
        }
        sig = msg.Signals.FirstOrDefault(s =>
            string.Equals(s.Name, signalName, StringComparison.Ordinal));
        return sig is not null;
    }
}
