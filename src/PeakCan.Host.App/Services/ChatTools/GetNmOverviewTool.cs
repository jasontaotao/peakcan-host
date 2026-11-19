using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core.Analysis.Chat;
using PeakCan.Host.Core.Analysis;
using PeakCan.Host.Core.Analysis.Nm;

namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>
/// Chat tool <c>get_nm_overview</c>: deterministic NM summary for a trace
/// window (episodes, per-message statistics, findings, capabilities).
/// </summary>
public sealed class GetNmOverviewTool : ChatToolBase
{
    private const string Schema = """
{"type":"object","properties":{
  "source_id":{"type":"string"},
  "channel":{"type":"integer","minimum":0,"maximum":65535},
  "t_start":{"type":"number"},
  "t_end":{"type":"number"},
  "window_ref":{"type":"string"}
},"additionalProperties":false}
""";

    private readonly IChatToolContext _context;

    public GetNmOverviewTool(IChatToolContext context, ILogger<GetNmOverviewTool> logger)
        : base("get_nm_overview",
            "Deterministic NM overview for a trace/window: episodes, per-message frame counts/cycle/first-last seen, top findings, capabilities, missing inputs, unknowns and assumptions. Use this instead of anomaly_scan for NM behaviour.",
            Schema, logger)
    {
        _context = context;
    }

    protected override async Task<string> ExecuteCoreAsync(string argsJson, CancellationToken ct)
    {
        var args = ParseArgs(argsJson);
        var snapshot = await _context.GetNmSnapshotAsync(new NmToolQuery(
            SourceId: args["source_id"]?.GetValue<string>(),
            Channel: NmToolArgs.GetChannel(args),
            TStart: args["t_start"]?.GetValue<double>(),
            TEnd: args["t_end"]?.GetValue<double>(),
            WindowRef: args["window_ref"]?.GetValue<string>()), ct);

        var report = snapshot.Report;

        var episodes = report.Episodes.Take(20).Select(e => new JsonObject
        {
            ["index"] = e.Index,
            ["start"] = e.Start,
            ["start_label"] = TraceTimeFormatter.Format(e.Start, null),
            ["end"] = e.End,
            ["end_label"] = TraceTimeFormatter.Format(e.End, null),
            ["kind"] = e.Kind,
            ["participants"] = new JsonArray(e.Participants.Select(p => (JsonNode)p).ToArray()),
            ["boundary_start"] = e.BoundaryStart,
            ["boundary_end"] = e.BoundaryEnd,
        });

        var messages = report.Messages.Take(50).Select(m => new JsonObject
        {
            ["can_id"] = m.CanIdHex,
            ["name"] = m.Name,
            ["sender"] = m.Sender,
            ["frame_count"] = m.FrameCount,
            ["first_seen"] = m.FirstSeen,
            ["first_seen_label"] = TraceTimeFormatter.Format(m.FirstSeen, null),
            ["last_seen"] = m.LastSeen,
            ["last_seen_label"] = TraceTimeFormatter.Format(m.LastSeen, null),
            ["cycle"] = CycleJson(m.Cycle),
            ["silent_beyond_timeout"] = m.SilentBeyondTimeout,
        });

        var findings = report.Findings.Take(30).Select(f => new JsonObject
        {
            ["code"] = f.Code,
            ["severity"] = f.Severity,
            ["message"] = f.Message,
            ["evidence_times"] = new JsonArray(f.EvidenceTimes.Select(t => (JsonNode)t).ToArray()),
            ["is_inference"] = f.IsInference,
            ["suggested_next_check"] = f.SuggestedNextCheck,
        });

        var root = new JsonObject
        {
            ["source_id"] = report.SourceId,
            ["channel"] = report.Channel,
            ["window"] = new JsonObject
            {
                ["start"] = report.Window.Start,
                ["end"] = report.Window.End,
                ["ref"] = report.Window.WindowRef,
            },
            ["direction"] = report.Direction is null ? null : new JsonObject
            {
                ["transmit"] = report.Direction.Transmit,
                ["receive"] = report.Direction.Receive,
                ["unknown"] = report.Direction.Unknown,
            },
            ["episodes"] = new JsonArray(episodes.ToArray()),
            ["messages"] = new JsonArray(messages.ToArray()),
            ["findings"] = new JsonArray(findings.ToArray()),
            ["capabilities"] = new JsonArray(report.Capabilities.Select(c => (JsonNode)new JsonObject
            {
                ["capability"] = c.Capability.ToString(),
                ["available"] = c.Available,
            }).ToArray()),
            ["missing_inputs"] = new JsonArray(report.MissingInputs.Select(m => (JsonNode)new JsonObject
            {
                ["capability"] = m.Capability.ToString(),
                ["input"] = m.InputKey,
                ["why"] = m.Why,
                ["suggestion"] = m.Suggestion,
            }).ToArray()),
            ["unknowns"] = new JsonArray(report.Unknowns.Select(u => (JsonNode)u).ToArray()),
            ["assumptions"] = new JsonArray(report.Assumptions.Select(a => (JsonNode)a).ToArray()),
            // 分析器自身的截断（如 signal_change 事件超上限），让模型知道结果并非全量。
            ["truncations"] = new JsonArray(report.Truncations.Select(t => (JsonNode)new JsonObject
            {
                ["section"] = t.Section,
                ["returned"] = t.Returned,
                ["total"] = t.Total,
            }).ToArray()),
        };

        var truncated = new JsonArray();
        if (report.Episodes.Count > 20)
            truncated.Add(new JsonObject { ["section"] = "episodes", ["returned"] = 20, ["total"] = report.Episodes.Count });
        if (report.Messages.Count > 50)
            truncated.Add(new JsonObject { ["section"] = "messages", ["returned"] = 50, ["total"] = report.Messages.Count });
        if (report.Findings.Count > 30)
            truncated.Add(new JsonObject { ["section"] = "findings", ["returned"] = 30, ["total"] = report.Findings.Count });
        root["truncated"] = truncated;

        return root.ToJsonString();
    }

    internal static JsonObject CycleJson(NmCycleStats c) => new()
    {
        ["median_ms"] = c.MedianMs,
        ["p95_ms"] = c.P95Ms,
        ["min_ms"] = c.MinMs,
        ["max_ms"] = c.MaxMs,
        ["phase_ms"] = c.PhaseMs,
        ["deviation_count"] = c.DeviationCount,
        ["consecutive_deviation_count"] = c.ConsecutiveDeviationCount,
        ["interval_count"] = c.IntervalCount,
        ["expected_ms"] = c.ExpectedMs,
        ["tolerance_ms"] = c.ToleranceMs,
    };
}
