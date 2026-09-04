using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using PeakCan.HIL.Core.J1939;
using PeakCan.Host.App.Services.Nodes;

namespace PeakCan.Host.App.ViewModels.Nodes;

/// <summary>
/// 编辑缓冲 → <see cref="NodeConfig"/> 组装（纯函数，编辑契约由 NodeEditorViewModelTests 钉住）。
/// 校验规则对标既有语义：<see cref="J1939SendViewModel"/>（hex 解析 / ≤8B 单帧指引 / RTS-CTS 必填 DA）
/// 与 <see cref="J1939NodeContext.SendCore"/>（模式-长度）。J1939MessageRef 的 Sa 恒为 null——
/// 匹配宽容（MessageRefMatcher：Sa 双非空才比较），发送时 J1939NodeContext 用节点自身 SA。
/// </summary>
internal static class NodeConfigAssembler
{
    public static bool TryAssemble(
        string name, string saHex,
        IReadOnlyList<NodeMessageRowViewModel> messages,
        IReadOnlyList<ResponseRuleRowViewModel> rules,
        NodeConfig? original,
        [NotNullWhen(true)] out NodeConfig? config,
        [NotNullWhen(false)] out string? error)
    {
        config = null;
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "节点名称不能为空";
            return false;
        }
        if (!TryParseHexByte(saHex, out var sa))
        {
            error = $"SA 无效: {saHex}";
            return false;
        }

        var messageList = new List<NodeMessage>(messages.Count);
        foreach (var m in messages)
        {
            if (!TryBuildJ1939Ref(m.PgnHex, m.PriorityText, m.DaHex, Mode(m.ModeIndex), requireDaForRtsCts: true, out var refr, out error))
                return false;
            if (!int.TryParse(m.IntervalMsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intervalMs) || intervalMs <= 0)
            {
                error = $"消息 PGN 0x{refr.Pgn:X4} 周期无效: {m.IntervalMsText}";
                return false;
            }
            if (!TryBuildPayload(m.PayloadKindIndex, m.PayloadHexText, m.PayloadDbcMessageName, m.PayloadScriptRefText, out var payload, out var byteLen, out error))
                return false;
            if (!ValidateModeLength(refr.Mode, byteLen, out error))
                return false;

            messageList.Add(new NodeMessage(refr, intervalMs, payload, m.Enabled));
        }

        var ruleList = new List<ResponseRule>(rules.Count);
        foreach (var r in rules)
        {
            if (!TryBuildJ1939Ref(r.TriggerPgnHex, r.TriggerPriorityText, r.TriggerDaHex, ModeNullable(r.TriggerModeIndex), requireDaForRtsCts: false, out var trigger, out error))
                return false;
            if (!TryBuildCondition(r, out error))
                return false;
            BytePattern? condition = null;
            if (!string.IsNullOrWhiteSpace(r.ConditionOffsetText)
                || !string.IsNullOrWhiteSpace(r.ConditionMaskHex)
                || !string.IsNullOrWhiteSpace(r.ConditionValueHex))
            {
                condition = new BytePattern(
                    int.Parse(r.ConditionOffsetText, CultureInfo.InvariantCulture),
                    ParseHexByteOr(r.ConditionMaskHex),
                    ParseHexByteOr(r.ConditionValueHex));
            }
            if (!TryBuildAction(r, out var action, out error))
                return false;
            if (!int.TryParse(r.DelayMsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var delayMs) || delayMs < 0)
            {
                error = $"规则 {DescribeTriggerShort(trigger)} 延迟无效: {r.DelayMsText}";
                return false;
            }

            ruleList.Add(new ResponseRule(trigger, condition, action, delayMs));
        }

        config = new NodeConfig
        {
            Name = name,
            Identity = new J1939NodeIdentity(sa),
            // 编辑区不呈现的字段从原配置透传（review 修复：否则 ApplyConfig 会静默抹掉
            // Tag 分组与 AddressClaimEnabled，破坏 StartAll(tag) 与地址声明行为）。
            Tag = original?.Tag,
            AddressClaimEnabled = original?.AddressClaimEnabled ?? false,
            Messages = messageList.ToArray(),
            Rules = ruleList.ToArray(),
        };
        error = null;
        return true;
    }

    /// <summary>消息行模式索引（必填 0/1/2）；规则 Trigger 允许 -1（模式通配）。</summary>
    private static TpMode? Mode(int index) => index switch
    {
        0 => TpMode.Single,
        1 => TpMode.Bam,
        2 => TpMode.RtsCts,
        _ => null,
    };

    /// <summary>规则 Trigger 模式（ComboBox 含"通配"项：0=通配/1=Single/2=Bam/3=RtsCts）。</summary>
    private static TpMode? ModeNullable(int index) => index switch
    {
        0 => null,              // 通配（Matcher：Mode 双非空才比较）
        1 => TpMode.Single,
        2 => TpMode.Bam,
        3 => TpMode.RtsCts,
        _ => null,              // 非法索引防御性归通配
    };

    /// <summary>
    /// J1939 引用组装（Sa 恒 null——发送时用节点自身 SA；DA 空 → null）。
    /// <paramref name="requireDaForRtsCts"/>：发送面（消息 / send 动作）引用必须满足
    /// "RTS-CTS 必有 DA"（<see cref="J1939SendViewModel"/> 同款入口校验；SendCore 对
    /// RtsCts Da=null 会每周期失败）；触发引用是匹配面（Matcher 宽容），不强制。
    /// </summary>
    private static bool TryBuildJ1939Ref(string pgnHex, string priorityText, string daHex, TpMode? mode,
        bool requireDaForRtsCts,
        out J1939MessageRef refr, [NotNullWhen(false)] out string? error)
    {
        refr = null!;
        if (!TryParseHexUInt32(pgnHex, out var pgn))
        {
            error = $"PGN 无效: {pgnHex}";
            return false;
        }
        if (!byte.TryParse(priorityText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var priority))
        {
            error = $"优先级无效: {priorityText}";
            return false;
        }
        byte? da = null;
        if (!string.IsNullOrWhiteSpace(daHex))
        {
            if (!TryParseHexByte(daHex, out var parsedDa))
            {
                error = $"目标地址无效: {daHex}";
                return false;
            }
            da = parsedDa;
        }
        if (requireDaForRtsCts && mode == TpMode.RtsCts && da is null)
        {
            error = "RTS-CTS 模式必须提供目标地址（DA）";
            return false;
        }
        refr = new J1939MessageRef(pgn, priority, mode, null, da);
        error = null;
        return true;
    }

    /// <summary>载荷多态组装；<paramref name="byteLen"/> 供模式-长度校验复用（FixedHex 实际字节数）。</summary>
    private static bool TryBuildPayload(int kindIndex, string hexText, string dbcMessageName, string scriptRef,
        out NodePayloadSource payload, out int byteLen, [NotNullWhen(false)] out string? error)
    {
        payload = null!;
        byteLen = 0;
        switch (kindIndex)
        {
            case 0:
            {
                byte[] bytes;
                try
                {
                    bytes = ParseHex(hexText);
                }
                catch (FormatException ex)
                {
                    error = $"载荷无效: {ex.Message}";
                    return false;
                }
                if (bytes.Length == 0)
                {
                    error = "载荷不能为空";
                    return false;
                }
                payload = new FixedHexSource(string.Join(" ", bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture))));
                byteLen = bytes.Length;
                break;
            }
            case 1:
                if (string.IsNullOrWhiteSpace(dbcMessageName))
                {
                    error = "DBC 载荷必须选择消息名";
                    return false;
                }
                payload = new DbcSignalsSource(dbcMessageName);
                break;
            case 2:
                if (string.IsNullOrWhiteSpace(scriptRef))
                {
                    error = "脚本载荷必须提供引用";
                    return false;
                }
                payload = new ScriptCallbackSource(scriptRef);
                break;
            default:
                error = $"载荷类型无效: {kindIndex}";
                return false;
        }
        error = null;
        return true;
    }

    /// <summary>模式-长度规则（对齐 J1939SendViewModel:253-258 与 J1939TpLayer.TryValidatePayload）。</summary>
    private static bool ValidateModeLength(TpMode? mode, int byteLen, [NotNullWhen(false)] out string? error)
    {
        if (mode == TpMode.Single && byteLen > 8)
        {
            error = "单帧载荷超过 8 字节，请改用 BAM/RTS-CTS 模式";
            return false;
        }
        if (mode is TpMode.Bam or TpMode.RtsCts && byteLen is >= 1 and <= 8)
        {
            error = "≤8 字节请选择单帧模式";
            return false;
        }
        error = null;
        return true;
    }

    private static bool TryBuildCondition(ResponseRuleRowViewModel r, [NotNullWhen(false)] out string? error)
    {
        error = null;
        var allEmpty = string.IsNullOrWhiteSpace(r.ConditionOffsetText)
                       && string.IsNullOrWhiteSpace(r.ConditionMaskHex)
                       && string.IsNullOrWhiteSpace(r.ConditionValueHex);
        if (allEmpty)
            return true;
        if (!int.TryParse(r.ConditionOffsetText, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            error = $"条件偏移无效: {r.ConditionOffsetText}";
            return false;
        }
        if (!TryParseHexByte(r.ConditionMaskHex, out _))
        {
            error = $"条件掩码无效: {r.ConditionMaskHex}";
            return false;
        }
        if (!TryParseHexByte(r.ConditionValueHex, out _))
        {
            error = $"条件值无效: {r.ConditionValueHex}";
            return false;
        }
        return true;
    }

    private static bool TryBuildAction(ResponseRuleRowViewModel r, out NodeAction action, [NotNullWhen(false)] out string? error)
    {
        action = null!;
        switch (r.ActionKindIndex)
        {
            case 0:   // send：Ref + 载荷（多态）
            {
                if (!TryBuildJ1939Ref(r.ActionRefPgnHex, r.ActionRefPriorityText, r.ActionRefDaHex, Mode(r.ActionRefModeIndex), requireDaForRtsCts: true, out var refr, out error))
                    return false;
                if (!TryBuildPayload(r.ActionPayloadKindIndex, r.ActionPayloadHexText, r.ActionPayloadDbcMessageName, r.ActionPayloadScriptRefText, out var payload, out var byteLen, out error))
                    return false;
                if (!ValidateModeLength(refr.Mode, byteLen, out error))
                    return false;
                action = new SendMessageAction(refr, payload);
                break;
            }
            case 1:   // setSignal
                if (string.IsNullOrWhiteSpace(r.ActionSignalMessageName) || string.IsNullOrWhiteSpace(r.ActionSignalName))
                {
                    error = "setSignal 动作必须提供消息名与信号名";
                    return false;
                }
                if (!double.TryParse(r.ActionSignalValueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    error = $"setSignal 值无效: {r.ActionSignalValueText}";
                    return false;
                }
                action = new SetSignalAction(r.ActionSignalMessageName, r.ActionSignalName, value);
                break;
            case 2:   // start（匹配面：启停周期表消息，经 MessageRefMatcher 匹配，不发送）
            {
                if (!TryBuildJ1939Ref(r.ActionRefPgnHex, r.ActionRefPriorityText, r.ActionRefDaHex, Mode(r.ActionRefModeIndex), requireDaForRtsCts: false, out var refr, out error))
                    return false;
                action = new StartMessageAction(refr);
                break;
            }
            case 3:   // stop（匹配面，同 start）
            {
                if (!TryBuildJ1939Ref(r.ActionRefPgnHex, r.ActionRefPriorityText, r.ActionRefDaHex, Mode(r.ActionRefModeIndex), requireDaForRtsCts: false, out var refr, out error))
                    return false;
                action = new StopMessageAction(refr);
                break;
            }
            case 4:   // script（plan 修订 10：编辑支持，运行时降级报错）
                if (string.IsNullOrWhiteSpace(r.ActionScriptRefText))
                {
                    error = "script 动作必须提供引用";
                    return false;
                }
                action = new ScriptAction(r.ActionScriptRefText);
                break;
            default:
                error = $"动作类型无效: {r.ActionKindIndex}";
                return false;
        }
        error = null;
        return true;
    }

    private static string DescribeTriggerShort(J1939MessageRef t) => $"PGN 0x{t.Pgn:X4}";

    /// <summary>Parse hex string（镜像 J1939SendViewModel.ParseHex）：空格/连字符分隔，奇长前补 0。</summary>
    internal static byte[] ParseHex(string s)
    {
        var stripped = s.Replace(" ", string.Empty, StringComparison.Ordinal)
                        .Replace("-", string.Empty, StringComparison.Ordinal);
        if (stripped.Length == 0)
            throw new FormatException("hex 为空（仅分隔符或无输入）");
        if ((stripped.Length & 1) == 1)
            stripped = "0" + stripped;
        var bytes = new byte[stripped.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = byte.Parse(stripped.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return bytes;
    }

    internal static bool TryParseHexUInt32(string text, out uint value)
    {
        var s = text.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    internal static bool TryParseHexByte(string text, out byte value)
    {
        var s = text.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        return byte.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static byte ParseHexByteOr(string text)
        => TryParseHexByte(text, out var v) ? v : (byte)0;
}