using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core.Analysis.Chat;
using PeakCan.Host.Core.Analysis.Nm;

namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>
/// Chat tool <c>get_nm_profile</c>: returns the NM configuration derived
/// from the loaded DBC plus user supplements (NM messages, timings, nodes,
/// conflicts, capability availability, missing inputs).
/// </summary>
public sealed class GetNmProfileTool : ChatToolBase
{
    private const string Schema = """
{"type":"object","properties":{
  "source_id":{"type":"string"},
  "channel":{"type":"integer"}
},"additionalProperties":false}
""";

    private readonly IChatToolContext _context;

    public GetNmProfileTool(IChatToolContext context, ILogger<GetNmProfileTool> logger)
        : base("get_nm_profile",
            "Get the network-management (NM) configuration derived from the loaded DBC plus user supplements: NM messages, timings, nodes, conflicts, which capabilities are available, and what inputs are missing. Call this FIRST for any NM/sleep/wakeup/dropout question.",
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

        var messages = snapshot.Profile.Messages.Take(50).Select(m => new JsonObject
        {
            ["can_id"] = m.CanIdHex,
            ["extended"] = m.IsExtended,
            ["name"] = m.Name,
            ["sender"] = m.Sender,
            ["dlc"] = m.Dlc,
            ["is_nm"] = m.IsNm,
            ["reasons"] = new JsonArray(m.Reasons.Select(r => (JsonNode)r).ToArray()),
            ["cycle_ms"] = m.CycleMs,
            ["timeout_ms"] = m.TimeoutMs,
            ["marker_authoritative"] = m.MarkerAuthoritative,
        });

        var root = new JsonObject
        {
            ["source_id"] = snapshot.SourceId,
            ["channel"] = snapshot.Channel,
            ["dbc_path"] = snapshot.DbcPath,
            ["dbc_hash"] = snapshot.DbcHash,
            ["semantics_available"] = false,
            ["messages"] = new JsonArray(messages.ToArray()),
            ["timings"] = new JsonObject
            {
                ["cycle_ms"] = snapshot.Profile.Timings.CycleMs,
                ["timeout_ms"] = snapshot.Profile.Timings.TimeoutMs,
                ["repeat_message_ms"] = snapshot.Profile.Timings.RepeatMessageMs,
                ["wait_bus_sleep_ms"] = snapshot.Profile.Timings.WaitBusSleepMs,
            },
            ["nodes"] = new JsonArray(snapshot.Profile.Nodes.Select(n => (JsonNode)new JsonObject
            {
                ["name"] = n.Name,
                ["station_address"] = n.StationAddress,
                ["is_nm_node"] = n.IsNmNode,
            }).ToArray()),
            ["conflicts"] = new JsonArray(snapshot.Profile.Conflicts.Select(c => (JsonNode)new JsonObject
            {
                ["field"] = c.Field,
                ["values"] = new JsonArray(c.Values.Select(v => (JsonNode)v).ToArray()),
                ["sources"] = new JsonArray(c.Sources.Select(v => (JsonNode)v).ToArray()),
            }).ToArray()),
            ["capabilities"] = new JsonArray(snapshot.Report.Capabilities.Select(c => (JsonNode)new JsonObject
            {
                ["capability"] = c.Capability.ToString(),
                ["available"] = c.Available,
            }).ToArray()),
            ["missing_inputs"] = new JsonArray(snapshot.Report.MissingInputs.Select(m => (JsonNode)new JsonObject
            {
                ["capability"] = m.Capability.ToString(),
                ["input"] = m.InputKey,
                ["why"] = m.Why,
                ["suggestion"] = m.Suggestion,
                ["suggestion_source"] = m.SuggestionSource,
            }).ToArray()),
            ["truncated"] = snapshot.Profile.Messages.Count > 50 ? new JsonObject
            {
                ["section"] = "messages",
                ["returned"] = 50,
                ["total"] = snapshot.Profile.Messages.Count,
            } : null,
            ["next_step"] = snapshot.Report.MissingInputs.Count > 0
                ? "缺少输入时先说明缺什么、影响哪个能力，再引导用户打开「网络管理 → 补全」对话框。"
                : "输入齐备，可以调用 get_nm_overview 查看时间轴结论。",
        };
        return root.ToJsonString();
    }
}

/// <summary>
/// Shared argument helpers for the NM chat tools.
/// </summary>
internal static class NmToolArgs
{
    /// <summary>Reads the optional <c>channel</c> argument. Values outside the
    /// ushort range are rejected with a clear error instead of silently
    /// wrapping around (e.g. 70000 must not become 4464).</summary>
    internal static ushort? GetChannel(JsonObject args)
    {
        if (args["channel"] is not { } node)
            return null;

        var value = node.GetValue<int>();
        if (value < 0 || value > ushort.MaxValue)
        {
            throw new ArgumentException(
                $"channel must be within 0..{ushort.MaxValue}, got {value}.",
                nameof(args));
        }

        return (ushort)value;
    }
}
