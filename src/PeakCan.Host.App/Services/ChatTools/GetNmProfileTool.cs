using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core.Analysis.Chat;

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
  "channel":{"type":"integer","minimum":0,"maximum":65535}
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
        // spec §8.1：多源且未显式 source_id 时不得静默取 master。
        if (NmToolArgs.CheckSourceSelection(args, _context) is { } sourceError)
            return sourceError;

        var snapshot = await _context.GetNmSnapshotAsync(new NmToolQuery(
            SourceId: NmToolArgs.GetString(args, "source_id"),
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

        var root = NmToolOutput.Root(snapshot);
        // 本报告出现过的 CAN ID 是结构信息，正文里可保留；其余十六进制取值按字节脱敏。
        var allowedCanIds = NmEventPrivacy.CollectCanIds(
            snapshot.Profile.Messages.Select(m => m.CanIdHex),
            snapshot.Report.Events.Select(e => e.CanIdHex));
        // spec §11：assumptions 可能带 DBC 告警中的节点地址十进制值，输出前脱敏。
        NmEventPrivacy.RedactAssumptions(root);
        root["messages"] = new JsonArray(messages.ToArray());
        root["timings"] = new JsonObject
        {
            ["cycle_ms"] = snapshot.Profile.Timings.CycleMs,
            ["timeout_ms"] = snapshot.Profile.Timings.TimeoutMs,
            ["repeat_message_ms"] = snapshot.Profile.Timings.RepeatMessageMs,
            ["wait_bus_sleep_ms"] = snapshot.Profile.Timings.WaitBusSleepMs,
        };
        root["nodes"] = new JsonArray(snapshot.Profile.Nodes.Select(n => (JsonNode)new JsonObject
        {
            ["name"] = n.Name,
            // spec §11：站地址属于字节取值，默认不发给模型；节点名保留以便定位。
            ["station_address"] = null,
            ["is_nm_node"] = n.IsNmNode,
            ["station_address_redacted"] = true,
        }).ToArray());
        root["conflicts"] = new JsonArray(snapshot.Profile.Conflicts.Select(c => (JsonNode)new JsonObject
        {
            ["field"] = c.Field,
            // 冲突值可能来自 NID/站地址等字节输入（§11）：发给模型前脱敏。
            ["values"] = new JsonArray(c.Values.Select(v => (JsonNode)NmEventPrivacy.RedactText(v, allowedCanIds)).ToArray()),
            ["sources"] = new JsonArray(c.Sources.Select(v => (JsonNode)NmEventPrivacy.RedactText(v, allowedCanIds)).ToArray()),
        }).ToArray());
        root["capabilities"] = new JsonArray(snapshot.Report.Capabilities.Select(c => (JsonNode)new JsonObject
        {
            ["capability"] = c.Capability.ToString(),
            ["available"] = c.Available,
        }).ToArray());
        root["missing_inputs"] = new JsonArray(snapshot.Report.MissingInputs.Select(m => (JsonNode)new JsonObject
        {
            ["capability"] = m.Capability.ToString(),
            ["input"] = m.InputKey,
            // 建议文本可能含 NID 候选等字节取值（§11）：发给模型前脱敏。
            ["why"] = NmEventPrivacy.RedactText(m.Why, allowedCanIds),
            ["suggestion"] = m.Suggestion is null ? null : NmEventPrivacy.RedactText(m.Suggestion, allowedCanIds),
            ["suggestion_source"] = m.SuggestionSource,
        }).ToArray());
        if (snapshot.Profile.Messages.Count > 50)
            NmToolOutput.AddTruncation(root, "messages", 50, snapshot.Profile.Messages.Count);
        root["next_step"] = snapshot.Report.MissingInputs.Count > 0
            ? "缺少输入时先说明缺什么、影响哪个能力，再引导用户打开「网络管理 → 补全」对话框。"
            : "输入齐备，可以调用 get_nm_overview 查看时间轴结论。";
        return root.ToJsonString();
    }
}

/// <summary>
/// Shared argument helpers for the NM chat tools.
/// </summary>
internal static class NmToolArgs
{
    /// <summary>Reads an optional string argument. JSON type mismatches are
    /// converted to <see cref="ArgumentException"/> so the base class returns
    /// the parameter hint instead of the data-source hint (spec §8.1).</summary>
    internal static string? GetString(JsonObject args, string key)
    {
        if (args[key] is not { } node)
            return null;

        try
        {
            return node.GetValue<string>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            throw new ArgumentException($"parameter '{key}' must be a string.", nameof(args), ex);
        }
    }

    /// <summary>Reads an optional integer argument; see <see cref="GetString"/>.</summary>
    internal static int? GetInt(JsonObject args, string key)
    {
        if (args[key] is not { } node)
            return null;

        try
        {
            return node.GetValue<int>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            throw new ArgumentException($"parameter '{key}' must be an integer.", nameof(args), ex);
        }
    }

    /// <summary>Reads an optional number argument; see <see cref="GetString"/>.</summary>
    internal static double? GetDouble(JsonObject args, string key)
    {
        if (args[key] is not { } node)
            return null;

        try
        {
            return node.GetValue<double>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            throw new ArgumentException($"parameter '{key}' must be a number.", nameof(args), ex);
        }
    }

    /// <summary>Reads the optional <c>channel</c> argument. Values outside the
    /// ushort range are rejected with a clear error instead of silently
    /// wrapping around (e.g. 70000 must not become 4464).</summary>
    internal static ushort? GetChannel(JsonObject args)
    {
        if (GetInt(args, "channel") is not { } value)
            return null;

        if (value < 0 || value > ushort.MaxValue)
        {
            throw new ArgumentException(
                $"channel must be within 0..{ushort.MaxValue}, got {value}.",
                nameof(args));
        }

        return (ushort)value;
    }

    /// <summary>
    /// spec §8.1：可枚举多个 source 且调用方未显式给 <c>source_id</c> 时返回
    /// <c>{"error":..., "hint":"请显式指定 source_id"}</c>，不静默分析 master。
    /// 单源/未加载任何源时返回 null，保持既有 master 默认行为。
    /// </summary>
    internal static string? CheckSourceSelection(JsonObject args, IChatToolContext context)
    {
        if (!string.IsNullOrWhiteSpace(GetString(args, "source_id")))
            return null;

        var sourceCount = context.LoadedSourceIds.Count;
        if (sourceCount <= 1) return null;

        // ChatToolBase 的通用 hint 无法表达这条 spec 专用提示，因此直接返回错误信封。
        return JsonSerializer.Serialize(new
        {
            error = $"检测到 {sourceCount} 个已加载 trace source，未指定 source_id，拒绝默认分析 master",
            hint = "请显式指定 source_id",
        });
    }
}
