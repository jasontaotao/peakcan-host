using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>
/// Payload redaction for NM event details sent to the LLM.
/// <para>
/// Content events (<c>byte_change</c> / <c>signal_change</c>) embed raw bus
/// values. The chat boundary only forwards structure (byte/bit indices,
/// change counts, time ranges, DBC signal names) per the privacy default.
/// The panel keeps the unredacted <see cref="PeakCan.Host.Core.Analysis.Nm.NmEvent.Detail"/>;
/// only tool output is redacted.
/// </para>
/// </summary>
internal static partial class NmEventPrivacy
{
    private const string RedactedHexValue = "0x??";

    /// <summary>Returns a detail string safe to send to the LLM. Event types
    /// without payload values pass through unchanged.</summary>
    internal static string RedactDetail(string type, string detail) => type switch
    {
        // "byte0: 0x00 → 0x08，变化 N 次（..." → "byte0: 0x?? → 0x??，变化 N 次（..."
        "byte_change" or "bit_change" => HexValueRegex().Replace(detail, RedactedHexValue),
        // "WakeReason: 85 → 2" → "WakeReason: 变化"
        "signal_change" => RedactSignalChange(detail),
        // burst_observed / all_nm_silent / sender_silent / bus_wide_silence
        // 只含时间、时长、帧数，无需脱敏。
        _ => detail,
    };

    /// <summary>Redacts byte-sized hex values in free text (findings.message /
    /// conflicts / missing input text).
    /// <para>
    /// 未提供 <paramref name="allowedCanIds"/> 时保持原规则：只替换 1–2 位十六进制
    /// （NID、站地址、CBV 都是 1 字节），3 位及以上的报文 ID 视为结构信息保留。
    /// </para>
    /// <para>
    /// 提供 <paramref name="allowedCanIds"/>（本报告所有 <c>CanIdHex</c>）后，按数值
    /// 豁免集合内的 CAN ID，集合外的十六进制取值（包括 3 位以上的）一律脱敏。
    /// 这是<strong>按值豁免</strong>：若某个 NID 的数值恰好等于报文 ID，它也会被保留；
    /// 文本层无法区分同一数值的不同角色，M1 接受该取舍。
    /// </para>
    /// </summary>
    internal static string RedactText(string text, IReadOnlyCollection<string>? allowedCanIds = null)
    {
        if (allowedCanIds is null)
            return ByteValueRegex().Replace(text, RedactedHexValue);

        var allowed = NormalizeCanIds(allowedCanIds);
        // 集合为空说明本报告没有任何可保留的 CAN ID：退回旧规则（3 位以上视为 ID 保留），
        // 否则会把正文里的报文 ID 一并抹掉，反而丢失结构信息。
        if (allowed.Count == 0)
            return ByteValueRegex().Replace(text, RedactedHexValue);

        return HexValueRegex().Replace(text, match =>
            allowed.Contains(NormalizeHex(match.Value)) && IsIdPosition(text, match.Index)
                ? match.Value
                : RedactedHexValue);
    }

    /// <summary>汇总多个来源的 <c>CanIdHex</c>（messages 与 events.subject），
    /// 供 <see cref="RedactText"/> 做“集合内 ID 保留”判定。</summary>
    internal static IReadOnlyCollection<string> CollectCanIds(params IEnumerable<string?>[] sources)
    {
        var ids = new List<string>();
        foreach (var source in sources)
        {
            foreach (var id in source)
            {
                if (!string.IsNullOrWhiteSpace(id))
                    ids.Add(id);
            }
        }
        return ids;
    }

    /// <summary>把 <c>0x</c> 前缀、大小写、前导零不同的 CAN ID 归一化成同一字符串键。</summary>
    internal static string NormalizeHex(string value)
    {
        var span = value.AsSpan();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            span = span[2..];
        span = span.TrimStart('0');
        return span.IsEmpty ? "0" : span.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// 只有出现在“报文 ID 位置”的十六进制 token 才允许按白名单保留：
    /// 句首，或紧跟在 <c>ID</c> / <c>报文</c> / <c>共享</c> 之后。
    /// 这样即使某个 NID 的数值恰好等于某条报文的 ID，<c>NID 0x500</c> 仍会被脱敏，
    /// 而 <c>共享 ID 0x500</c>、<c>0x500（...）</c> 这类结构信息得以保留。
    /// </summary>
    private static bool IsIdPosition(string text, int index)
    {
        var position = index;
        while (true)
        {
            if (position == 0) return true;

            var trimmed = text[..position].TrimEnd();
            if (trimmed.Length == 0) return true;

            if (trimmed.EndsWith("报文", StringComparison.Ordinal)
                || trimmed.EndsWith("共享", StringComparison.Ordinal))
                return true;

            if (trimmed.EndsWith("CAN", StringComparison.OrdinalIgnoreCase)
                && (trimmed.Length == 3 || !char.IsLetter(trimmed[^4])))
                return true;

            if (trimmed.EndsWith("ID", StringComparison.OrdinalIgnoreCase))
            {
                // 必须是独立的 "ID"："NID 0x500" 里的 ID 前一个字符是字母，不能算 ID 位置。
                var beforeId = trimmed[..^2];
                return beforeId.Length == 0 || !char.IsLetter(beforeId[^1]);
            }

            // ID 列表（"报文 0x50/0x500"）：分隔符前的 token 若是 ID 位置，后一个也算。
            if (trimmed.EndsWith('/') || trimmed.EndsWith('、')
                || trimmed.EndsWith(',') || trimmed.EndsWith('，'))
            {
                var previous = trimmed[..^1].TrimEnd();
                var start = previous.LastIndexOf("0x", StringComparison.OrdinalIgnoreCase);
                if (start < 0) return false;
                position = start;
                continue;
            }

            return false;
        }
    }

    private static HashSet<string> NormalizeCanIds(IReadOnlyCollection<string> ids)
    {
        var set = new HashSet<string>(ids.Count, StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!string.IsNullOrWhiteSpace(id))
                set.Add(NormalizeHex(id));
        }
        return set;
    }

    /// <summary>
    /// spec §11：DBC 解析告警（进入报告 assumptions）可能带十进制节点地址，
    /// 例如 <c>NmStationAddress=244</c>。只脱敏地址/标识类属性的赋值，保留属性名；
    /// 计数、周期、超时等 §11 允许发送的数值不动。
    /// </summary>
    internal static string RedactAttributeValue(string text)
        => NmAttributeValueRegex().Replace(text, "${attr}=??");

    /// <summary>Assumption 自由文本同时做字节 hex 与属性十进制脱敏。</summary>
    internal static string RedactAssumption(string text)
        => RedactAttributeValue(RedactText(text));

    /// <summary>原地脱敏工具输出根对象的 <c>assumptions</c> 数组。</summary>
    internal static void RedactAssumptions(JsonObject root)
    {
        if (root["assumptions"] is not JsonArray assumptions) return;

        for (var i = 0; i < assumptions.Count; i++)
        {
            if (assumptions[i] is JsonValue value && value.TryGetValue<string>(out var text))
                assumptions[i] = RedactAssumption(text);
        }
    }

    private static string RedactSignalChange(string detail)
    {
        var colon = detail.IndexOf(':');
        return colon < 0 ? detail : detail[..colon] + ": 变化";
    }

    [GeneratedRegex("0x[0-9A-Fa-f]+")]
    private static partial Regex HexValueRegex();

    /// <summary>Byte-width hex literal: 1–2 digits, not followed by another hex
    /// digit (so the "0x50" prefix of a CAN ID "0x500" is not matched).
    /// <para>
    /// 这是“无白名单”时的保守规则：合法的 2 位十六进制 CAN ID（如 <c>0x50</c>）
    /// 也会被当成字节值脱敏。工具层传入本报告的 <c>CanIdHex</c> 集合后改走
    /// <see cref="HexValueRegex"/>，集合内 ID 不再被误伤。
    /// </para></summary>
    [GeneratedRegex("0x[0-9A-Fa-f]{1,2}(?![0-9A-Fa-f])")]
    private static partial Regex ByteValueRegex();

    /// <summary>地址/标识类 NM 属性的十进制赋值。显式列名，避免把
    /// <c>NmAsrMessageCount=256</c> 这类允许发送的计数也改掉。</summary>
    [GeneratedRegex(@"\b(?<attr>Nm(?:StationAddress|AsrNodeIdentifier|BaseAddress|AsrBaseAddress))\s*=\s*-?\d+")]
    private static partial Regex NmAttributeValueRegex();
}
