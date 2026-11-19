using System.Globalization;
using PeakCan.HIL.Core.Dbc;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 把 DBC 的 NM 属性翻译成 <see cref="NmProfile"/>。只陈述文件写了什么；
/// 用户补充输入的合并由 <see cref="NmSupplementMerger"/> 负责。
/// </summary>
public static class NmProfileBuilder
{
    // CA1861: constant array arguments must be static readonly, not re-allocated per call.
    private static readonly string[] MissingDbcField = ["dbc"];
    private static readonly string[] CycleConflictSources = ["NmAsrCanMsgCycleTime", "GenMsgCycleTime"];

    public static NmProfile Build(DbcDocument? dbc, string? dbcPath)
    {
        if (dbc is null)
        {
            return new NmProfile(
                NmProfileSource.None,
                Array.Empty<NmMessageDef>(),
                new NmTimings(),
                Array.Empty<NmNodeDef>(),
                Array.Empty<NmConfigConflict>(),
                MissingDbcField,
                new NmProvenance(null, null, "none"));
        }

        var globals = dbc.NmAttributes;
        var timings = new NmTimings(
            CycleMs: globals?.CanMsgCycleTimeMs,
            TimeoutMs: globals?.TimeoutTimeMs,
            RepeatMessageMs: globals?.RepeatMessageTimeMs,
            WaitBusSleepMs: globals?.WaitBusSleepTimeMs);

        var messages = new List<NmMessageDef>(dbc.Messages.Count);
        var conflicts = new List<NmConfigConflict>();

        foreach (var message in dbc.Messages)
        {
            var reasons = new List<string>();
            var authoritative = false;

            if (message.MsgTypeIndex is { } index
                && dbc.TryGetMessageEnumName("MsgType", index, out var enumName)
                && string.Equals(enumName, "CANNM", StringComparison.Ordinal))
            {
                reasons.Add("MsgType=CANNM");
                authoritative = true;
            }

            if (message.NmAsrMessage == true)
            {
                reasons.Add("NmAsrMessage=Yes");
                authoritative = true;
            }

            var cycle = message.GenMsgCycleTimeMs is { } msgCycle ? (int)msgCycle : timings.CycleMs;
            if (reasons.Count > 0 && timings.CycleMs is { } globalCycle
                && message.GenMsgCycleTimeMs is { } messageCycle
                && messageCycle != (uint)globalCycle)
            {
                conflicts.Add(new NmConfigConflict(
                    "CycleMs",
                    new[] { globalCycle.ToString(CultureInfo.InvariantCulture),
                            messageCycle.ToString(CultureInfo.InvariantCulture) },
                    CycleConflictSources));
            }

            messages.Add(new NmMessageDef(
                CanIdHex: $"0x{message.Id & 0x1FFFFFFFu:X}",
                IsExtended: (message.Id & 0x80000000u) != 0,
                Name: message.Name,
                Sender: message.Sender,
                Dlc: message.Dlc,
                IsNm: reasons.Count > 0,
                Reasons: reasons.ToArray(),
                CycleMs: cycle,
                TimeoutMs: timings.TimeoutMs,
                MarkerAuthoritative: authoritative));
        }

        var nodes = dbc.Nodes
            .Select(n => new NmNodeDef(n.Name, n.NmStationAddress, n.NmAsrNodeIdentifier, n.NmAsrNode == true))
            .ToArray();

        var missing = new List<string>();
        if (dbc.Messages.All(m => m.MsgTypeIndex is null && m.NmAsrMessage is null))
            missing.Add("nm_message_selection");

        return new NmProfile(
            NmProfileSource.Dbc,
            messages,
            timings,
            nodes,
            conflicts,
            missing,
            new NmProvenance(dbcPath, null, "dbc"));
    }
}
