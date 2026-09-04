using System.Globalization;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.Replay;
using PeakCan.HIL.Core.HIL.Environment;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// 过滤条各字段文本 → <see cref="TraceFilterSpec"/> 的解析器（spec §5.3）。
/// 纯函数：输入字段文本 + 当前 DBC（<c>DbcDocument?</c>），输出
/// <c>(TraceFilterSpec?, error)</c>。任一字段非法 → 整体返回 <c>null</c> spec +
/// 错误文本（调用方沿用上一有效 spec，不做"非法字段按缺席处理"）。
/// </summary>
internal static class TraceFilterParser
{
    /// <summary>PGN 列表分隔符集合，对齐 <see cref="CanIdListParser"/>（其 Separators 是 private）。</summary>
    private static readonly char[] PgnSeparators = { ',', ' ', '\t', '\n', '\r' };

    public static (TraceFilterSpec? Spec, string? Error) TryParse(
        string? idList,
        string? pgnList,
        string? sa,
        string? da,
        string? dbcMessageName,
        DbcDocument? dbc,
        string? payloadOffset,
        string? payloadMask,
        string? payloadValue)
    {
        // —— ID 列表（CanIdListParser：无前缀十进制 / 0x=hex，用户输入不掩码）——
        IReadOnlySet<uint>? allowList = null;
        if (!string.IsNullOrWhiteSpace(idList))
        {
            var parsed = CanIdListParser.Parse(idList);
            if (parsed.HasInvalidTokens)
                return (null, $"ID 无效: {string.Join(", ", parsed.InvalidTokens)}");
            allowList = parsed.AllowList; // null=空输入已排除；空集=全非法已在上面返回。
        }

        // —— DBC 消息名（case-insensitive，命中并入 IdAllowList 取并集）——
        if (!string.IsNullOrWhiteSpace(dbcMessageName))
        {
            if (dbc is null)
                return (null, "DBC 未加载，无法解析消息名");
            var message = dbc.Messages.FirstOrDefault(m =>
                string.Equals(m.Name, dbcMessageName, StringComparison.OrdinalIgnoreCase));
            if (message is null)
                return (null, $"DBC 中不存在消息 '{dbcMessageName}'");
            // Message.Id 带 merged IDE 位（bit31），掩码后得裸 ID。
            var bare = message.Id & 0x7FFF_FFFFu;
            allowList = allowList is null
                ? (IReadOnlySet<uint>)new HashSet<uint> { bare }
                : allowList.Concat(new[] { bare }).ToHashSet();
        }

        // —— PGN 列表（hex，0x 可选，无前缀按 hex，≤0x3FFFF）——
        IReadOnlySet<uint>? pgns = null;
        if (!string.IsNullOrWhiteSpace(pgnList))
        {
            var set = new HashSet<uint>();
            foreach (var raw in pgnList.Split(PgnSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!TryParseHexUInt32(raw.Trim(), out var pgn) || pgn > 0x3FFFF)
                    return (null, $"PGN 无效: {raw.Trim()}");
                set.Add(pgn);
            }
            pgns = set;
        }

        // —— SA / DA（单 hex 字节，0x 可选；空=不过滤）——
        byte? saValue = null;
        if (!string.IsNullOrWhiteSpace(sa))
        {
            if (!TryParseHexByte(sa, out var v))
                return (null, $"SA 无效: {sa}");
            saValue = v;
        }
        byte? daValue = null;
        if (!string.IsNullOrWhiteSpace(da))
        {
            if (!TryParseHexByte(da, out var v))
                return (null, $"DA 无效: {da}");
            daValue = v;
        }

        // —— payload（三小字段，全空=无条件，部分填=错误）——
        BytePattern? payload = null;
        bool offsetFilled = !string.IsNullOrWhiteSpace(payloadOffset);
        bool maskFilled = !string.IsNullOrWhiteSpace(payloadMask);
        bool valueFilled = !string.IsNullOrWhiteSpace(payloadValue);
        if (offsetFilled || maskFilled || valueFilled)
        {
            if (!offsetFilled || !maskFilled || !valueFilled)
                return (null, "payload 三个字段需同时填写");
            // 负数 offset 拒绝（NumberStyles.Integer 允许负号；负值会使 Filter 谓词
            // entry.Data[-1] 抛 IndexOutOfRangeException → TraceService 后台循环死亡。
            // review 2026-08-31 CRITICAL。）
            if (!int.TryParse(payloadOffset, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset)
                || offset < 0)
                return (null, $"payload offset 无效: {payloadOffset}");
            if (!TryParseHexByte(payloadMask!, out var mask))
                return (null, $"payload mask 无效: {payloadMask}");
            if (!TryParseHexByte(payloadValue!, out var value))
                return (null, $"payload value 无效: {payloadValue}");
            payload = new BytePattern(offset, mask, value);
        }

        return (new TraceFilterSpec
        {
            IdAllowList = allowList,
            PgnList = pgns,
            Sa = saValue,
            Da = daValue,
            Payload = payload,
        }, null);
    }

    /// <summary>hex（0x 可选，无前缀按 hex），镜像 <see cref="NodeConfigAssembler.TryParseHexUInt32"/>。</summary>
    private static bool TryParseHexUInt32(string text, out uint value)
    {
        var s = text.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>单 hex 字节（0x 可选，无前缀按 hex），镜像 <see cref="NodeConfigAssembler.TryParseHexByte"/>。</summary>
    private static bool TryParseHexByte(string text, out byte value)
    {
        var s = text.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        return byte.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}



