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
        // spec §8.1：多源且未显式 source_id 时不得静默取 master。
        if (NmToolArgs.CheckSourceSelection(args, _context) is { } sourceError)
            return sourceError;

        var snapshot = await _context.GetNmSnapshotAsync(new NmToolQuery(
            SourceId: NmToolArgs.GetString(args, "source_id"),
            Channel: NmToolArgs.GetChannel(args),
            TStart: NmToolArgs.GetDouble(args, "t_start"),
            TEnd: NmToolArgs.GetDouble(args, "t_end"),
            WindowRef: NmToolArgs.GetString(args, "window_ref")), ct);

        var report = snapshot.Report;
        // 本报告涉及过的 CAN ID 是结构信息，正文里可保留；其余十六进制取值按字节脱敏。
        // 必须并入 profile 里的 NM 报文：窗口内从未出现的报文只产 NM_MESSAGE_ABSENT
        // finding、不产 NmMessageReport，漏掉它会把该 finding 的报文 ID 误脱敏成 0x??。
        var allowedCanIds = NmEventPrivacy.CollectCanIds(
            report.Messages.Select(m => m.CanIdHex),
            report.Events.Select(e => e.CanIdHex),
            snapshot.Profile.Messages.Select(m => m.CanIdHex));

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
            // 四态静默判定（spec §7.1.2）：AI 必须能区分「全网静默」与「单节点静默」。
            ["silence_verdict"] = m.SilenceVerdict.ToString(),
            ["silent_beyond_timeout"] = m.SilentBeyondTimeout,
        });

        var findings = report.Findings.Take(30).Select(f => new JsonObject
        {
            ["code"] = f.Code,
            ["severity"] = f.Severity,
            // findings 文本可能带 NID/站地址等字节取值（§11 隐私旁路）：发给模型前脱敏。
            // CanIdHex 是独立的结构字段，不受影响。
            ["message"] = NmEventPrivacy.RedactText(f.Message, allowedCanIds),
            ["evidence_times"] = new JsonArray(f.EvidenceTimes.Select(t => (JsonNode)t).ToArray()),
            ["is_inference"] = f.IsInference,
            ["suggested_next_check"] = f.SuggestedNextCheck is null
                ? null
                : NmEventPrivacy.RedactText(f.SuggestedNextCheck, allowedCanIds),
        });

        var root = NmToolOutput.Root(snapshot);
        // spec §11：assumptions 可能带 DBC 告警中的节点地址十进制值，输出前脱敏。
        NmEventPrivacy.RedactAssumptions(root);
        root["window"] = new JsonObject
        {
            ["start"] = report.Window.Start,
            ["end"] = report.Window.End,
            ["ref"] = report.Window.WindowRef,
        };
        root["direction"] = report.Direction is null ? null : new JsonObject
        {
            ["transmit"] = report.Direction.Transmit,
            ["receive"] = report.Direction.Receive,
            ["unknown"] = report.Direction.Unknown,
        };
        root["episodes"] = new JsonArray(episodes.ToArray());
        root["messages"] = new JsonArray(messages.ToArray());
        root["findings"] = new JsonArray(findings.ToArray());
        // spec §7.1.1：观测事实独立于违规结论输出，AI 不得据此直接断言故障。
        root["observations"] = report.Observations is null
            ? new JsonArray()
            : new JsonArray(report.Observations.Select(o => (JsonNode)new JsonObject
            {
                ["kind"] = o.Kind,
                ["node"] = o.Node,
                ["can_id"] = o.CanIdHex,
                ["start"] = o.Start,
                ["end"] = o.End,
                ["detail"] = NmEventPrivacy.RedactText(o.Detail, allowedCanIds),
                ["evidence_kind"] = o.EvidenceKind,
                ["is_inference"] = o.IsInference,
                ["rule_id"] = o.RuleId,
            }).ToArray());
        // spec §15：Unmodeled 只发条款号，不发条款正文。
        root["unmodeled_rules"] = report.UnmodeledRules is null
            ? new JsonArray()
            : new JsonArray(report.UnmodeledRules.Select(u => (JsonNode)new JsonObject
            {
                ["clause"] = u.Clause,
                ["reason"] = u.Reason,
                ["requirement_id"] = u.RequirementId,
            }).ToArray());
        root["capabilities"] = new JsonArray(report.Capabilities.Select(c => (JsonNode)new JsonObject
        {
            ["capability"] = c.Capability.ToString(),
            ["available"] = c.Available,
        }).ToArray());
        root["missing_inputs"] = new JsonArray(report.MissingInputs.Select(m => (JsonNode)new JsonObject
        {
            ["capability"] = m.Capability.ToString(),
            ["input"] = m.InputKey,
            // 建议文本可能含 NID 候选等字节取值（§11）：发给模型前脱敏。
            ["why"] = NmEventPrivacy.RedactText(m.Why, allowedCanIds),
            ["suggestion"] = m.Suggestion is null ? null : NmEventPrivacy.RedactText(m.Suggestion, allowedCanIds),
        }).ToArray());
        // 分析器自身的截断（如 signal_change 事件超上限），让模型知道结果并非全量。
        root["truncations"] = new JsonArray(report.Truncations.Select(t => (JsonNode)new JsonObject
        {
            ["section"] = t.Section,
            ["returned"] = t.Returned,
            ["total"] = t.Total,
        }).ToArray());

        if (report.Episodes.Count > 20)
            NmToolOutput.AddTruncation(root, "episodes", 20, report.Episodes.Count);
        if (report.Messages.Count > 50)
            NmToolOutput.AddTruncation(root, "messages", 50, report.Messages.Count);
        if (report.Findings.Count > 30)
            NmToolOutput.AddTruncation(root, "findings", 30, report.Findings.Count);

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
