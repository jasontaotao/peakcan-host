using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core.Analysis;
using PeakCan.Host.Mobile.Core.Services;
using PeakCan.HIL.Core.Dbc;

namespace PeakCan.Host.Mobile.Core.Chat.Tools;

/// <summary>
/// Tool <c>search_signal_trace</c> - time series of one or more signals over
/// a time window, decoded from the SQLite replay cache and LTTB-downsampled
/// to <c>max_points</c> samples with window statistics. Mobile counterpart
/// of the desktop tool of the same name: arguments use {message, signal}
/// naming (no hex keys) and window_ref supports a single anchor instead of
/// the desktop green/blue anchor pair.
/// </summary>
public sealed class SearchSignalTraceTool : MobileChatToolBase
{
    private const int MaxSignals = 8;

    private const string DefinitionSchema =
        """{"type":"object","properties":{"signals":{"type":"array","minItems":1,"maxItems":8,"items":{"type":"object","properties":{"message":{"type":"string","description":"DBC message name"},"signal":{"type":"string","description":"Signal name within the message"}},"required":["message","signal"],"additionalProperties":false},"description":"Signals to extract (use search_signals to discover names)"},"t_start":{"type":"number","description":"Window start in seconds (default 0)"},"t_end":{"type":"number","description":"Window end in seconds (default: trace duration)"},"window_ref":{"type":"string","enum":["absolute","anchor"],"default":"absolute","description":"anchor = t_start/t_end are offsets from the anchor timestamp"},"max_points":{"type":"integer","minimum":10,"maximum":1000,"default":200,"description":"Target LTTB sample count per signal"}},"required":["signals"],"additionalProperties":false}""";

    private readonly IMobileChatToolContext _context;

    public SearchSignalTraceTool(IMobileChatToolContext context, ILogger logger)
        : base(
            "search_signal_trace",
            "Extract time-series samples for given signals over a time window from the replay cache, LTTB-downsampled, with window statistics (min/max/mean/first/last). Use for trend, transition and correlation analysis. Call search_signals first to find message/signal names.",
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
        if (_context.TraceId is null)
            return """{"error":"no cache"}""";

        var args = ParseArgs(argsJson);
        if (args["signals"] is not JsonArray signalsNode || signalsNode.Count == 0)
            return """{"error":"missing 'signals'"}""";

        var maxPoints = args["max_points"] is { } maxPointsNode
            ? Math.Clamp(maxPointsNode.GetValue<int>(), 10, 1000)
            : 200;
        var windowRef = args["window_ref"]?.GetValue<string>() ?? "absolute";
        if (windowRef is not ("absolute" or "anchor"))
            return $$"""{"error":"unknown window_ref: {{windowRef}}"}""";

        var summary = await _context.GetCacheSummaryAsync(ct).ConfigureAwait(false);
        double tStart = args["t_start"]?.GetValue<double>() ?? 0;
        double tEnd = args["t_end"]?.GetValue<double>()
            ?? _context.DurationSeconds
            ?? summary?.Duration
            ?? 0;

        if (windowRef == "anchor")
        {
            if (_context.AnchorTimestamp is not { } anchor)
                return """{"error":"no anchor set","hint":"请先设置锚点，或使用 absolute 模式"}""";
            tStart += anchor;
            tEnd += anchor;
        }

        // 同一消息的多个信号共用一次窗口查询
        var windowCache = new Dictionary<uint, FramePage>();
        var signals = new JsonArray();
        var rawCount = 0;
        var truncated = false;

        foreach (var entryNode in signalsNode.Take(MaxSignals))
        {
            if (entryNode is not JsonObject entry)
            {
                signals.Add(new JsonObject { ["error"] = "invalid entry" });
                continue;
            }
            var msgName = entry["message"]?.GetValue<string>();
            var sigName = entry["signal"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(msgName) || string.IsNullOrWhiteSpace(sigName))
            {
                signals.Add(new JsonObject
                {
                    ["message"] = msgName,
                    ["signal"] = sigName,
                    ["error"] = "missing 'message' or 'signal'",
                });
                continue;
            }

            var message = dbc.Document.Messages.FirstOrDefault(
                m => m.Name.Equals(msgName, StringComparison.OrdinalIgnoreCase));
            if (message is null)
            {
                signals.Add(new JsonObject { ["message"] = msgName, ["error"] = "message not found" });
                continue;
            }
            var signal = message.Signals.FirstOrDefault(
                s => s.Name.Equals(sigName, StringComparison.OrdinalIgnoreCase));
            if (signal is null)
            {
                signals.Add(new JsonObject { ["message"] = msgName, ["signal"] = sigName, ["error"] = "signal not found" });
                continue;
            }

            // DBC Id 的 bit31 是 IDE 约定位（PCAN 惯例）；缓存存 29 位裸值
            var canId = (message.Id & 0x80000000u) != 0 ? message.Id & 0x7FFFFFFFu : message.Id;

            if (!windowCache.TryGetValue(canId, out var page))
            {
                page = await _context.GetFramesForCanIdAsync(canId, tStart, tEnd, ct).ConfigureAwait(false);
                windowCache[canId] = page;
                rawCount += page.Frames.Count;
                truncated |= page.HasMore;
            }

            // 物理值走 SignalDecoder（double 原值）而非 DbcCatalog.Decode（0.### 格式化
            // 字符串，会毁掉时间序列精度）；multiplexed 信号跳过选择子不匹配的帧。
            var points = new List<(double T, double V)>();
            foreach (var frame in page.Frames)
            {
                var payload = frame.Data.AsSpan(0, Math.Min(frame.Dlc, frame.Data.Length));
                if (!DbcCatalog.IsSignalActive(message, signal, payload)) continue;
                points.Add((frame.Timestamp, SignalDecoder.Decode(payload, signal)));
            }
            if (points.Count == 0)
            {
                signals.Add(new JsonObject { ["message"] = message.Name, ["signal"] = signal.Name, ["error"] = "no frames in window" });
                continue;
            }

            var downsampled = LttbDownsampler.Downsample(points, maxPoints);
            var values = points.Select(p => p.V).ToList();

            var samples = new JsonArray();
            foreach (var (t, v) in downsampled)
            {
                samples.Add(new JsonObject
                {
                    ["t"] = Math.Round(t, 4),
                    ["t_label"] = TsLabel(t),
                    ["v"] = Math.Round(v, 4),
                });
            }

            signals.Add(new JsonObject
            {
                ["message"] = message.Name,
                ["signal"] = signal.Name,
                ["unit"] = signal.Unit,
                ["sample_count"] = downsampled.Count,
                ["stats"] = new JsonObject
                {
                    ["min"] = Math.Round(values.Min(), 4),
                    ["max"] = Math.Round(values.Max(), 4),
                    ["mean"] = Math.Round(values.Average(), 4),
                    ["first"] = Math.Round(points[0].V, 4),
                    ["last"] = Math.Round(points[^1].V, 4),
                },
                ["samples"] = samples,
                ["t_range"] = new JsonObject
                {
                    ["start"] = Math.Round(points[0].T, 4),
                    ["start_label"] = TsLabel(points[0].T),
                    ["end"] = Math.Round(points[^1].T, 4),
                    ["end_label"] = TsLabel(points[^1].T),
                },
            });
        }

        var root = new JsonObject
        {
            ["signals"] = signals,
            ["backend_info"] = new JsonObject
            {
                ["raw_frame_count"] = rawCount,
                ["truncated"] = truncated,
                ["downsample_method"] = "LTTB",
                ["window_ref"] = windowRef,
                ["t_start"] = Math.Round(tStart, 4),
                ["t_end"] = Math.Round(tEnd, 4),
            },
        };
        if (summary is { Complete: false } && tEnd > summary.Duration)
            root["warning"] = "cache incomplete";
        return root.ToJsonString();
    }
}
