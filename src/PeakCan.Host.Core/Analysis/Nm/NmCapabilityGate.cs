using System.Globalization;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 按"所需输入是否齐备"决定每项能力是否可用。缺项不是拒绝分析，而是降级 + 提示。
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
            var candidates = NmSuggestionEngine.SuggestNmMessageCandidates(profile);
            var suggestion = candidates.Count > 0
                ? "按名称候选：" + string.Join(", ", candidates)
                : "从 DBC 报文清单勾选，或按名称/区间/实测周期选择";
            Add(NmCapability.IdentifyMessages, false, new NmInputMissing(
                NmCapability.IdentifyMessages, "nm_message_selection",
                "DBC 没有 MsgType=CANNM 或 NmAsrMessage=Yes 的报文",
                suggestion, "dbc"));
        }

        if (nmMessages.Any(m => m.CycleMs is not null))
        {
            Add(NmCapability.Timing, true);
        }
        else
        {
            var suggested = nmMessages.Length > 0
                ? NmSuggestionEngine.SuggestCycleMs(frames, ParseId(nmMessages[0].CanIdHex))
                : null;
            Add(NmCapability.Timing, false, new NmInputMissing(
                NmCapability.Timing, "cycle_ms",
                "DBC 没有 NM 周期属性",
                suggested is null ? "用实测间隔中位数" : $"{suggested} ms（实测中位）",
                suggested is null ? null : "measured"));
        }

        Add(NmCapability.Activity, true);
        Add(NmCapability.RawContent, true);

        var nmIds = new HashSet<uint>(nmMessages.Select(m => ParseId(m.CanIdHex)));
        var signalDecode = dbc is not null && dbc.Messages
            .Any(m => nmIds.Contains(m.Id & 0x1FFFFFFFu) && m.Signals.Count > 0);
        // SignalDecode 缺失不产生 missing_inputs 条目：spec §6.6.1 对它的建议是"—"，
        // 不是用户可补的输入。
        Add(NmCapability.SignalDecode, signalDecode);

        if (profile.Layout?.NidByte is { } nidByte)
        {
            if (nidByte >= nmMessages.Select(m => (int)m.Dlc).DefaultIfEmpty(0).Max())
            {
                Add(NmCapability.NidCheck, false, new NmInputMissing(
                    NmCapability.NidCheck, "nid_byte",
                    $"NID 字节 {nidByte} 超出 DLC", "选择小于 DLC 的字节", "user"));
            }
            else
            {
                Add(NmCapability.NidCheck, true);
            }
        }
        else
        {
            (byte? Byte, string Evidence) suggestion = nmMessages.Length > 0
                ? NmSuggestionEngine.SuggestNidByte(
                    frames, ParseId(nmMessages[0].CanIdHex),
                    profile.Nodes.FirstOrDefault(n => n.Name == nmMessages[0].Sender)?.StationAddress)
                : (null, "没有 NM 报文");
            Add(NmCapability.NidCheck, false, new NmInputMissing(
                NmCapability.NidCheck, "nid_byte",
                "未配置 NID 字节，无法校验源节点标识",
                suggestion.Byte is { } b ? $"建议 byte{b}" : suggestion.Evidence,
                suggestion.Byte is null ? null : "measured"));
        }

        if (profile.Layout?.CbvByte is not null)
        {
            Add(NmCapability.CbvDecode, true);
        }
        else
        {
            Add(NmCapability.CbvDecode, false, new NmInputMissing(
                NmCapability.CbvDecode, "cbv_byte",
                "未配置 CBV 字节，无法解码控制位",
                "选一个取值少、位翻转明显的字节；位角色需要你确认",
                "measured"));
        }

        var pnKnown = profile.Features?.PnEnabled is not null && profile.Layout?.CbvByte is not null;
        Add(NmCapability.PnCheck, pnKnown,
            pnKnown
                ? Array.Empty<NmInputMissing>()
                : new[] { new NmInputMissing(NmCapability.PnCheck, "pn_enabled",
                    "PN 开关或 CBV 字节缺失，无法判定 PNI", "不会自动建议 PN 开关", null) });

        var userDataKnown = profile.Layout is { UserDataOffset: not null, UserDataLength: not null };
        Add(NmCapability.UserData, userDataKnown,
            userDataKnown
                ? Array.Empty<NmInputMissing>()
                : new[] { new NmInputMissing(NmCapability.UserData, "user_data_range",
                    "用户数据 offset/length 缺失", "尾部全零时建议 length 0", "measured") });

        // 企标规则属于 M2，M1 不可用，但不列为"可补输入"。
        Add(NmCapability.OemRules, false);

        return (states, missing);
    }

    private static uint ParseId(string canIdHex)
        => uint.Parse(canIdHex.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
