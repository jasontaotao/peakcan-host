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
  "page_size":{"type":"integer","minimum":1,"maximum":200}
},"additionalProperties":false}
""";

    private readonly IChatToolContext _context;

    public GetNmEventsTool(IChatToolContext context, ILogger<GetNmEventsTool> logger)
        : base("get_nm_events",
            "Paginated NM events from the cached report (default 50, max 200 rows). Filter by episode_index, subject (node name or CAN ID) and types (comma-separated). Does not rescan frames.",
            Schema, logger)
    {
        _context = context;
    }

    protected override async Task<string> ExecuteCoreAsync(string argsJson, CancellationToken ct)
    {
        var args = ParseArgs(argsJson);
        var snapshot = await _context.GetNmSnapshotAsync(new NmToolQuery(
            SourceId: args["source_id"]?.GetValue<string>(),
            Channel: NmToolArgs.GetChannel(args)), ct);

        var page = Math.Max(1, args["page"]?.GetValue<int>() ?? 1);
        var pageSize = Math.Clamp(args["page_size"]?.GetValue<int>() ?? DefaultPageSize, 1, MaxPageSize);
        var episode = args["episode_index"]?.GetValue<int>();
        var subject = args["subject"]?.GetValue<string>();
        var types = args["types"]?.GetValue<string>()
            ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // subject 既可以是节点/发送者名，也可以是 CAN ID。按名字过滤时把该
        // sender 名下所有报文的 CAN ID 一并纳入，否则 content 事件
        // （byte_change/signal_change，Node 为 null）和 sender_silent
        // （Node 存 CAN ID）会被漏掉。
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
            return e.CanIdHex is not null && subjectIds.Contains(e.CanIdHex);
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
            // detail 可能含原始 payload 取值：发给模型前脱敏，面板本地仍显示原文。
            ["detail"] = NmEventPrivacy.RedactDetail(e.Type, e.Detail),
            ["episode_index"] = e.EpisodeIndex,
            ["evidence_kind"] = e.EvidenceKind,
        });

        // 用 long 乘法避免 page*page_size 在极端 page 值下 int 溢出，导致 next_page 误判。
        var nextPage = (long)page * pageSize < filtered.Length ? page + 1 : (int?)null;
        var root = new JsonObject
        {
            ["source_id"] = snapshot.Report.SourceId,
            ["channel"] = snapshot.Report.Channel,
            ["detail_redacted"] = true,
            ["total"] = filtered.Length,
            ["page"] = page,
            ["page_size"] = pageSize,
            ["next_page"] = nextPage,
            ["truncated"] = filtered.Length > pageSize,
            ["events"] = new JsonArray(rows.ToArray()),
        };
        return root.ToJsonString();
    }
}
