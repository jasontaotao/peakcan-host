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
            // 多态静默判定（spec §7.1.2）：AI 必须能区分「全网静默」与「单节点静默」。
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
            // 条款追溯：规则/需求/出处标识，不是字节取值，按原样输出。
            ["rule_id"] = f.RuleId,
            ["requirement_id"] = f.RequirementId,
            ["source_ref"] = f.SourceRef,
            // spec §4.0：条款是列表（burst_shape 覆盖 NM_03/NM_04/NM_16），
            // 单值 requirement_id 会造假归因，仅为兼容旧消费方保留。
            // 条款号是标识而非字节取值，按 rule_id/source_ref 的口径不脱敏。
            ["clauses"] = new JsonArray(
                f.Clauses.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
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
        // spec §6.1 rule 6：报告必须能自述判定依据的企标（standard_id@version）。
        root["standard_id"] = report.StandardId;
        root["direction"] = report.Direction is null ? null : new JsonObject
        {
            ["transmit"] = report.Direction.Transmit,
            ["receive"] = report.Direction.Receive,
            ["unknown"] = report.Direction.Unknown,
        };
        root["episodes"] = new JsonArray(episodes.ToArray());
        root["messages"] = new JsonArray(messages.ToArray());
        // spec §9 / §9.4：数据体检先于结论输出，并用独立布尔位标注「数据可疑」，
        // 让消费方不必扫描数组就能知道下方的 findings 是否可当结论使用。
        // 「没查」不算「不通过」：只有 Failed 才置 true。
        var dataQuality = report.DataQualityChecks ?? Array.Empty<NmDataQualityCheck>();
        root["data_quality_failed"] =
            dataQuality.Any(c => c.Status == NmDataQualityStatus.Failed);
        // 状态用显式字符串映射，枚举改名时线上格式不变。
        root["data_quality"] = new JsonArray(
            dataQuality
            .Select(c => (JsonNode?)new JsonObject
            {
                ["code"] = c.Code,
                ["status"] = c.Status switch
                {
                    NmDataQualityStatus.Passed => "passed",
                    NmDataQualityStatus.Failed => "failed",
                    _ => "not_checked",
                },
                ["detail"] = c.Detail,
                ["affected_can_ids"] = new JsonArray(
                    c.AffectedCanIds.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
            }).ToArray());
        // spec §7.1.1：逐规则求值状态。合规规则**零输出**——只看 findings 为空，
        // AI 会说「没有违规」，但真相可能是「这些规则根本没跑」。
        // 只有显式的 evaluated_no_finding 才能让 AI 说出「查过，没问题」。
        root["rule_evaluations"] = new JsonArray(
            (report.RuleEvaluations ?? Array.Empty<NmRuleEvaluation>())
            .Select(e => (JsonNode?)new JsonObject
            {
                ["rule_id"] = e.RuleId,
                // 枚举改名时线上格式不变。
                ["status"] = e.Status switch
                {
                    NmRuleVerdict.EvaluatedNoFinding => "evaluated_no_finding",
                    NmRuleVerdict.EvaluatedWithFinding => "evaluated_with_finding",
                    NmRuleVerdict.Partial => "partial",
                    NmRuleVerdict.NotApplicable => "not_applicable",
                    NmRuleVerdict.Unmodeled => "unmodeled",
                    // 显式列全，不用 `_ =>` 兜底：将来新增枚举值时编译期就暴露，
                    // 而不是静默归一到「未建模」让线上格式悄悄漂移。
                    _ => throw new InvalidOperationException(
                        $"未映射的规则求值状态：{e.Status}"),
                },
                ["reason_code"] = e.ReasonCode,
                ["evidence_refs"] = new JsonArray(
                    e.EvidenceRefs.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
            }).ToArray());
        // spec §5.5：条款覆盖。AI 最需要这一项判断「哪些规范要求这次根本没验」——
        // 只看 findings 无法区分「验过没问题」与「没验」。
        root["clause_coverage"] = new JsonArray(
            (report.ClauseCoverage ?? Array.Empty<NmClauseCoverageRow>())
            .Select(c => (JsonNode?)new JsonObject
            {
                ["clause"] = c.Clause,
                ["status"] = c.Status switch
                {
                    NmClauseStatus.Covered => "covered",
                    NmClauseStatus.Partial => "partial",
                    NmClauseStatus.NotCovered => "not_covered",
                    _ => throw new InvalidOperationException(
                        $"未映射的条款覆盖状态：{c.Status}"),
                },
                ["rule_ids"] = new JsonArray(
                    c.RuleIds.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
                ["reason_code"] = c.ReasonCode,
            }).ToArray());
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
        // 静默相位：全部 inferred；AI 不得把窗口写成节点已睡眠。
        // 与 episodes/messages/findings 同口径截断，避免长记录撑大工具输出。
        root["silence_phases"] = new JsonArray(report.SilencePhases.Take(20).Select(p => (JsonNode)new JsonObject
        {
            ["phase"] = NmSilencePhaseText.PhaseName(p.Kind),
            ["basis"] = NmSilencePhaseText.BasisName(p.Basis),
            ["start"] = p.Start,
            ["end"] = p.End,
            ["evidence_kind"] = p.EvidenceKind,
            ["is_inference"] = p.IsInference,
            ["non_nm_frame_count"] = p.NonNmFrameCount,
            ["nm_sender_app_frame_count"] = p.NmSenderAppFrameCount,
            ["other_sender_frame_count"] = p.OtherSenderFrameCount,
            ["unattributed_frame_count"] = p.UnattributedFrameCount,
            ["bus_max_gap_ms"] = p.BusMaxGapSec is { } gap ? gap * 1000.0 : null,
            ["nmsts"] = p.NmSts.ToString(),
            ["is_window_truncated"] = p.IsWindowTruncated,
            ["after_episode_index"] = p.AfterEpisodeIndex,
            ["detail"] = NmEventPrivacy.RedactText(p.Detail, allowedCanIds),
            ["senders"] = new JsonArray(p.Senders.Select(s => (JsonNode)new JsonObject
            {
                ["sender"] = s.Sender,
                ["is_nm_sender"] = s.IsNmSender,
                ["frame_count"] = s.FrameCount,
            }).ToArray()),
            ["senders_truncated"] = p.SendersTruncated,
        }).ToArray());
        // spec §15：Unmodeled 只发条款号，不发条款正文。
        root["unmodeled_rules"] = report.UnmodeledRules is null
            ? new JsonArray()
            : new JsonArray(report.UnmodeledRules.Select(u => (JsonNode)new JsonObject
            {
                ["clause"] = u.Clause,
                // 完整条款列表：多条款规则（NM_STOP_SENDING → NM_09/NM_12）只读
                // ordinal-first 的单值 `clause` 会丢条款。空列表表示该规则不属于
                // 条款空间（base 规则），此时用 rule_id 标识规则身份。
                ["clauses"] = new JsonArray(
                    u.Clauses.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
                // 原因里可能混入 CAN ID/NID 等取值（如「byte0 无法读取」旁的实测值），按字节脱敏。
                ["reason"] = NmEventPrivacy.RedactText(u.Reason, allowedCanIds),
                ["requirement_id"] = u.RequirementId,
                // 条款号（clause）与规则身份（rule_id）是两回事，AI 归因必须用 rule_id。
                ["rule_id"] = u.RuleId,
                ["reason_code"] = u.ReasonCode,
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
        if (report.SilencePhases.Count > 20)
            NmToolOutput.AddTruncation(root, "silence_phases", 20, report.SilencePhases.Count);

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
