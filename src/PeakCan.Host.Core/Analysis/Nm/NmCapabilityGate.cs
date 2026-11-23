using System.Globalization;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 按"所需输入是否齐备"决定每项能力是否可用。缺项不是拒绝分析，而是降级 + 提示。
/// <para>
/// M1 已知限制：<see cref="NmLayout"/> 没有 CBV 位角色与 PNI 位定义，因此 CBV 解码只能
/// 给出原始字节，PNI 判定仅依赖 <see cref="NmFeatures.PnEnabled"/> 声明；位级解读
/// （如 bit6 即 PNI）在 Task 9 才引入，当前不做门控。
/// </para>
/// </summary>
public static class NmCapabilityGate
{
    public static (IReadOnlyList<NmCapabilityState> Capabilities, IReadOnlyList<NmInputMissing> Missing) Evaluate(
        NmProfile profile, IReadOnlyList<ReplayFrame> frames, DbcDocument? dbc)
    {
        var nmMessages = profile.Messages.Where(m => m.IsNm).ToArray();
        var missing = new List<NmInputMissing>();
        var states = new List<NmCapabilityState>();

        void Add(NmCapability capability, bool available, params NmInputMissing[] items)
        {
            states.Add(new NmCapabilityState(capability, available, items));
            missing.AddRange(items);
        }

        if (nmMessages.Length > 0)
        {
            Add(NmCapability.IdentifyMessages, true);
        }
        else
        {
            // 名称候选 + 实测周期稳定的 8 字节报文候选（spec §6.6.4）。
            var candidates = NmSuggestionEngine.SuggestNmMessageCandidates(profile)
                .Concat(NmSuggestionEngine.SuggestNmMessageCandidatesByObservedCycle(frames))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var suggestion = candidates.Length > 0
                ? "按名称/实测周期候选：" + string.Join(", ", candidates)
                : "从 DBC 报文清单勾选，或按名称/区间/实测周期选择";
            Add(NmCapability.IdentifyMessages, false, new NmInputMissing(
                NmCapability.IdentifyMessages, "nm_message_selection",
                "DBC 没有 MsgType=CANNM 或 NmAsrMessage=Yes 的报文",
                suggestion, "dbc"));
        }

        // Timing：周期与超时是两个可补字段。缺超时时给 3×cycle 建议并明确标注“假设”。
        var profileCycle = profile.Timings.CycleMs;
        var messageCycle = nmMessages.Select(m => m.CycleMs).FirstOrDefault(c => c is not null);
        var measuredCycle = nmMessages.Length > 0
            ? NmSuggestionEngine.SuggestCycleMs(
                frames, ParseId(nmMessages[0].CanIdHex), nmMessages[0].IsExtended)
            : null;
        var knownCycle = profileCycle ?? messageCycle ?? measuredCycle;
        var configuredCycle = profileCycle ?? messageCycle;
        var knownTimeout = profile.Timings.TimeoutMs
            ?? nmMessages.Select(m => m.TimeoutMs).FirstOrDefault(t => t is not null);

        var timingMissing = new List<NmInputMissing>();
        if (knownCycle is null)
        {
            timingMissing.Add(new NmInputMissing(
                NmCapability.Timing, "cycle_ms",
                "DBC 没有 NM 周期属性",
                measuredCycle is null ? "用实测间隔中位数" : $"{measuredCycle} ms（实测中位）",
                measuredCycle is null ? null : "measured"));
        }
        if (knownTimeout is null)
        {
            timingMissing.Add(new NmInputMissing(
                NmCapability.Timing, "timeout_ms",
                "DBC 没有 NM 超时属性",
                knownCycle is { } cycle
                    ? $"{cycle * 3} ms（3×cycle 假设）"
                    : "先补周期，再按 3×cycle 估算",
                knownCycle is null ? null : "assumption"));
        }
        states.Add(new NmCapabilityState(
            NmCapability.Timing, configuredCycle is not null, timingMissing));
        missing.AddRange(timingMissing);

        Add(NmCapability.Activity, true);
        Add(NmCapability.RawContent, true);

        var nmIds = new HashSet<uint>(nmMessages.Select(m => ParseId(m.CanIdHex)));
        var signalDecode = dbc is not null && dbc.Messages
            .Any(m => nmIds.Contains(m.Id & 0x1FFFFFFFu) && m.Signals.Count > 0);
        // SignalDecode 缺失不产生 missing_inputs 条目：spec §6.6.1 对它的建议是"—"，
        // 不是用户可补的输入。
        Add(NmCapability.SignalDecode, signalDecode);

        if (NmProfilePrecedence.EffectiveNidByte(profile) is { } nidByte)
        {
            if (nidByte >= nmMessages.Select(m => (int)m.Dlc).DefaultIfEmpty(0).Max())
            {
                Add(NmCapability.NidCheck, false, new NmInputMissing(
                    NmCapability.NidCheck, "nid_byte",
                    $"NID 字节 {nidByte} 超出 DLC", "选择小于 DLC 的字节", "user"));
            }
            else if (!profile.Nodes.Any(n => n.StationAddress is not null))
            {
                // 有 NID 字节但没有任何站地址，无从比对。
                Add(NmCapability.NidCheck, false, new NmInputMissing(
                    NmCapability.NidCheck, "nm_node_mapping",
                    "没有带站地址的节点，无法比对 NID",
                    "在节点表中补充各节点的 StationAddress",
                    "user"));
            }
            else
            {
                Add(NmCapability.NidCheck, true);
            }
        }
        else
        {
            // spec §6.6.4：NID 字节给 top-2 候选，证据保持众数 + 比例。
            var nidCandidates = nmMessages.Length > 0
                ? NmSuggestionEngine.SuggestNidByteCandidates(
                    frames, ParseId(nmMessages[0].CanIdHex), nmMessages[0].IsExtended,
                    profile.Nodes.FirstOrDefault(n => n.Name == nmMessages[0].Sender)?.StationAddress)
                : Array.Empty<NmByteCandidate>();
            var what = "未配置 NID 字节，无法校验源节点标识";
            string? nidSuggestion = null;
            if (nidCandidates.Count > 0)
            {
                nidSuggestion = $"建议 byte{nidCandidates[0].Byte}：{nidCandidates[0].Evidence}";
                if (nidCandidates.Count > 1)
                {
                    nidSuggestion +=
                        $"；备选 byte{nidCandidates[1].Byte}：{nidCandidates[1].Evidence}";
                }
            }
            var nidWhy = nidCandidates.Count > 0
                ? what
                : $"{what}（{(nmMessages.Length > 0 ? "未找到取值等于站地址/ID 低字节的字节" : "没有 NM 报文")}）";
            Add(NmCapability.NidCheck, false, new NmInputMissing(
                NmCapability.NidCheck, "nid_byte",
                nidWhy, nidSuggestion,
                nidCandidates.Count > 0 ? "measured" : null));
        }

        if (NmProfilePrecedence.EffectiveCbvByte(profile) is not null)
        {
            Add(NmCapability.CbvDecode, true);
        }
        else
        {
            (byte? Byte, string Evidence) cbv = nmMessages.Length > 0
                ? NmSuggestionEngine.SuggestCbvByte(
                    frames, ParseId(nmMessages[0].CanIdHex), nmMessages[0].IsExtended)
                : (null, "没有 NM 报文");
            var cbvSuggestion = cbv.Byte is { } cbvByte
                ? $"建议 byte{cbvByte}：{cbv.Evidence}；位角色需要你确认"
                : "选一个取值少、位翻转明显的字节；位角色需要你确认";
            Add(NmCapability.CbvDecode, false, new NmInputMissing(
                NmCapability.CbvDecode, "cbv_byte",
                "未配置 CBV 字节，无法解码控制位",
                cbvSuggestion,
                cbv.Byte is null ? null : "measured"));
        }

        var pnKnown = NmProfilePrecedence.EffectivePnEnabled(profile) is not null
            && NmProfilePrecedence.EffectiveCbvByte(profile) is not null;
        Add(NmCapability.PnCheck, pnKnown,
            pnKnown
                ? Array.Empty<NmInputMissing>()
                : new[] { new NmInputMissing(NmCapability.PnCheck, "pn_enabled",
                    "PN 开关或 CBV 字节缺失，无法判定 PNI", "不会自动建议 PN 开关", null) });

        var userDataKnown = NmProfilePrecedence.EffectiveUserDataOffset(profile) is not null
            && NmProfilePrecedence.EffectiveUserDataLength(profile) is not null;
        (byte? Offset, int? Length, string Evidence) userData = nmMessages.Length > 0
            ? NmSuggestionEngine.SuggestUserData(
                frames, ParseId(nmMessages[0].CanIdHex), nmMessages[0].IsExtended)
            : (null, null, "没有 NM 报文");
        Add(NmCapability.UserData, userDataKnown,
            userDataKnown
                ? Array.Empty<NmInputMissing>()
                : new[] { new NmInputMissing(NmCapability.UserData, "user_data_range",
                    "用户数据 offset/length 缺失",
                    userData.Evidence,
                    userData.Offset is null ? null : "measured") });

        // 企标规则属于 M2，M1 不可用，但不列为"可补输入"。
        Add(NmCapability.OemRules, false);

        return (states, missing);
    }

    private static uint ParseId(string canIdHex)
        => uint.Parse(canIdHex.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
