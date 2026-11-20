using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core.Analysis.Chat;
using PeakCan.Host.Core.Analysis;
using PeakCan.Host.Core.Analysis.Nm;

namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>
/// Chat tool <c>get_nm_events</c>: paginated NM events from the cached
/// report. Does not rescan frames.
/// </summary>
public sealed class GetNmEventsTool : ChatToolBase
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    private const string Schema = """
{"type":"object","properties":{
  "source_id":{"type":"string"},
  "channel":{"type":"integer","minimum":0,"maximum":65535},
  "episode_index":{"type":"integer"},
  "subject":{"type":"string","description":"sender/node name or CAN ID hex"},
  "types":{"type":"string","description":"comma-separated event types"},
  "page":{"type":"integer","minimum":1},
  "page_size":{"type":"integer","minimum":1,"maximum":200},
  "t_start":{"type":"number"},
  "t_end":{"type":"number"},
  "window_ref":{"type":"string"}
},"additionalProperties":false}
""";

    private readonly IChatToolContext _context;

    public GetNmEventsTool(IChatToolContext context, ILogger<GetNmEventsTool> logger)
        : base("get_nm_events",
            "Paginated NM events from the cached report (default 50, max 200 rows). Filter by episode_index, subject (node name or CAN ID) and types (comma-separated). Pass the same t_start/t_end/window_ref used by get_nm_overview to drill into that exact report. Does not rescan frames.",
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

        // 窗口参数必须与 get_nm_overview 完全一致：两者共用 NmFlow 的
        // (source, channel, start, end, windowRef) 缓存 key，窗口化钻取才能命中同一份
        // 报告（episode_index 同一编号空间，且不重新扫帧）——spec §8.1。
        var snapshot = await _context.GetNmSnapshotAsync(new NmToolQuery(
            SourceId: NmToolArgs.GetString(args, "source_id"),
            Channel: NmToolArgs.GetChannel(args),
            TStart: NmToolArgs.GetDouble(args, "t_start"),
            TEnd: NmToolArgs.GetDouble(args, "t_end"),
            WindowRef: NmToolArgs.GetString(args, "window_ref")), ct);

        var page = Math.Max(1, NmToolArgs.GetInt(args, "page") ?? 1);
        var pageSize = Math.Clamp(NmToolArgs.GetInt(args, "page_size") ?? DefaultPageSize, 1, MaxPageSize);
        var episode = NmToolArgs.GetInt(args, "episode_index");
        var subject = NmToolArgs.GetString(args, "subject");
        var types = NmToolArgs.GetString(args, "types")
            ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // subject 既可以是节点/发送者名，也可以是 CAN ID。按名字过滤时把该
        // sender 名下所有报文的 CAN ID 一并纳入，否则 content 事件
        // （byte_change/signal_change，Node 为 null）和 sender_silent
        // （Node 存 CAN ID）会被漏掉。
        // 同数值标准/扩展帧可加后缀区分：`0x500:ext` / `0x500(ext)` / `0x500:std`。
        var subjectExt = ParseSubjectExtendedSuffix(ref subject);
        HashSet<string>? subjectIds = null;
        if (!string.IsNullOrEmpty(subject))
        {
            subjectIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { subject };
            foreach (var message in snapshot.Profile.Messages)
            {
                if (string.Equals(message.Sender, subject, StringComparison.OrdinalIgnoreCase))
                    subjectIds.Add(message.CanIdHex);
            }
        }

        bool MatchesSubject(NmEvent e)
        {
            if (subjectIds is null) return true;
            if (string.Equals(e.Node, subject, StringComparison.OrdinalIgnoreCase)) return true;
            if (e.CanIdHex is null || !subjectIds.Contains(e.CanIdHex)) return false;
            // 只在 subject 本身带后缀时区分格式；按发送者名过滤仍匹配该报文的两种格式。
            return subjectExt is null || e.IsExtended == subjectExt;
        }

        var filtered = snapshot.Report.Events
            .Where(e => episode is null || e.EpisodeIndex == episode)
            .Where(MatchesSubject)
            .Where(e => types is null || types.Contains(e.Type))
            .ToArray();

        // 极大 page 时 (page-1)*pageSize 会溢出 int：用 long 计算并把起点夹到总数。
        var skip = (int)Math.Min((long)(page - 1) * pageSize, filtered.Length);
        var rows = filtered.Skip(skip).Take(pageSize).Select(e => new JsonObject
        {
            ["t"] = e.T,
            ["t_label"] = TraceTimeFormatter.Format(e.T, null),
            ["type"] = e.Type,
            ["node"] = e.Node,
            ["can_id"] = e.CanIdHex,
            ["is_extended"] = e.IsExtended,
            // detail 可能含原始 payload 取值：发给模型前脱敏，面板本地仍显示原文。
            ["detail"] = NmEventPrivacy.RedactDetail(e.Type, e.Detail),
            ["episode_index"] = e.EpisodeIndex,
            ["evidence_kind"] = e.EvidenceKind,
        });

        // 用 long 乘法避免 page*page_size 在极端 page 值下 int 溢出，导致 next_page 误判。
        var nextPage = (long)page * pageSize < filtered.Length ? page + 1 : (int?)null;
        var root = NmToolOutput.Root(snapshot);
        // spec §11：assumptions 可能带 DBC 告警中的节点地址十进制值，输出前脱敏。
        NmEventPrivacy.RedactAssumptions(root);
        root["detail_redacted"] = true;
        root["total"] = filtered.Length;
        root["page"] = page;
        root["page_size"] = pageSize;
        // 分页信息只由 next_page 表达；truncated 保持公共契约的空数组。
        root["next_page"] = nextPage;
        root["events"] = new JsonArray(rows.ToArray());
        return root.ToJsonString();
    }

    /// <summary>
    /// 解析 subject 末尾的格式后缀：<c>:ext</c>/<c>(ext)</c> → true，
    /// <c>:std</c>/<c>(std)</c> → false；无后缀返回 null（两种格式都匹配）。
    /// 后缀会从 <paramref name="subject"/> 中剥离，便于复用同一个 ID/名称集合。
    /// </summary>
    private static bool? ParseSubjectExtendedSuffix(ref string? subject)
    {
        if (string.IsNullOrEmpty(subject)) return null;

        var value = subject!;
        foreach (var (suffix, flag) in new (string Suffix, bool IsExtended)[]
        {
            (":extended", true), ("(extended)", true),
            (":ext", true), ("(ext)", true),
            (":standard", false), ("(standard)", false),
            (":std", false), ("(std)", false),
        })
        {
            if (!value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            subject = value[..^suffix.Length];
            return flag;
        }
        return null;
    }
}
