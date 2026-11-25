using System.Globalization;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>静默相位分析输入。只处理 episode 间隙或尾部静默，首部由调用方排除。</summary>
public sealed record NmSilencePhaseInput(
    NmEpisode PrecedingEpisode,
    double GapStart,
    double GapEnd,
    bool IsTail,
    IReadOnlyList<NmFrame> NmFrames,
    IReadOnlyList<ReplayFrame> AllFrames,
    NmProfile Profile,
    NmGlobalTiming Timing,
    double WindowEnd,
    DbcDocument? Dbc = null);

public sealed record NmSilencePhaseResult(
    IReadOnlyList<NmSilencePhase> Phases,
    IReadOnlyList<string> Unknowns);

/// <summary>相位分析的 unknown 原因码（M1 spec §7.5 的静默相位小节）。</summary>
public static class NmSilencePhaseUnknowns
{
    public const string NoBusActivity = "silence_phase_no_bus_activity";
    public const string Unattributed = "silence_phase_unattributed";
    public const string NmStsRepeat = "silence_phase_nmsts_repeat";
    public const string AppTxAfterTimeout = "silence_phase_app_tx_after_timeout";
    public const string WaitBusSleepDefaulted = "silence_phase_wait_bus_sleep_defaulted";
    public const string WindowTooShort = "silence_phase_window_too_short";
    public const string NonNmMayIncludeUndeclaredNm = "silence_phase_non_nm_may_include_undeclared_nm";
    public const string SenderAttributionUsesJ1939Sa = "silence_phase_sender_attribution_uses_j1939_sa";
}

/// <summary>
/// 全网 NM 静默空档的相位推断（spec `2026-11-25-nm-silence-phase-design.md`）。
/// <para>
/// <b>只做推断，不做判定</b>：所有输出相位都是 <c>inferred</c>，不产 finding。
/// 判据是「NM 超时边界 + 发送者归属 + 总线是否静止」，不是「有没有应用报文」。
/// </para>
/// <para>
/// OEM §5.3.2：预睡眠期间 NM/应用报文禁止发送（允许清空发送缓存），总线活动需静止。
/// 因此 w2/w3 的矛盾判据是**任何帧**，不是只看 NM 发送者的应用帧。
/// </para>
/// </summary>
public static class NmSilencePhaseAnalyzer
{
    private const int MaxSenders = 8;

    public static NmSilencePhaseResult Analyze(NmSilencePhaseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var phases = new List<NmSilencePhase>();
        var unknowns = new List<string>();
        var s = input.GapStart;
        var e = input.GapEnd;
        if (e <= s) return new NmSilencePhaseResult(phases, unknowns);

        var timeoutSec = Math.Max(input.Timing.TimeoutMs, 1) / 1000.0;
        var waitSec = Math.Max(input.Timing.WaitBusSleepMs, 0) / 1000.0;
        var cycleSec = Math.Max(input.Timing.CycleMs, 1) / 1000.0;
        // 清缓存宽限取 1 个 NM 周期；唤醒宽限取 max(周期, T_WakeUp)。
        var drainGrace = cycleSec;
        var wakeupGrace = Math.Max(input.Timing.CycleMs, input.Timing.WakeUpMs) / 1000.0;

        var nmKeys = BuildNmKeys(input.NmFrames);
        var dbcSenders = BuildDbcSenderMap(input.Dbc);

        // 前一个 episode 内的 NM 发送者与各报文末帧。
        var nmSenders = new HashSet<string>(StringComparer.Ordinal);
        var lastFrames = new Dictionary<(uint Id, bool IsExtended), NmFrame>();
        foreach (var frame in input.NmFrames)
        {
            if (frame.Frame.Timestamp < input.PrecedingEpisode.Start
                || frame.Frame.Timestamp > input.PrecedingEpisode.End)
            {
                continue;
            }

            var key = Attribute(frame.Frame, dbcSenders);
            if (key is not null) nmSenders.Add(key);

            var defKey = (ParseId(frame.Def.CanIdHex), frame.Def.IsExtended);
            if (!lastFrames.TryGetValue(defKey, out var previous)
                || frame.Frame.Timestamp >= previous.Frame.Timestamp)
            {
                lastFrames[defKey] = frame;
            }
        }

        // gap 内全部帧。严格大于 s：s 处的最后一帧 NM 是锚点，不是静默期活动。
        var gapFrames = new List<ReplayFrame>();
        foreach (var frame in input.AllFrames)
        {
            if (frame.Timestamp > s && frame.Timestamp < e) gapFrames.Add(frame);
        }
        gapFrames.Sort(static (a, b) => a.Timestamp.CompareTo(b.Timestamp));

        var nonNmFrames = new List<ReplayFrame>(gapFrames.Count);
        foreach (var frame in gapFrames)
        {
            if (!nmKeys.Contains((frame.Id & 0x1FFFFFFFu, frame.IsExtended)))
                nonNmFrames.Add(frame);
        }
        var hasUnattributed = nonNmFrames.Exists(f => Attribute(f, dbcSenders) is null);
        if (hasUnattributed) unknowns.Add(NmSilencePhaseUnknowns.NonNmMayIncludeUndeclaredNm);
        foreach (var sender in nmSenders)
        {
            if (sender.StartsWith("SA:", StringComparison.Ordinal))
            {
                unknowns.Add(NmSilencePhaseUnknowns.SenderAttributionUsesJ1939Sa);
                break;
            }
        }

        var nmSts = ReadNmStsEvidence(input, lastFrames, timeoutSec, out var nmStsRepeat);
        if (nmStsRepeat)
        {
            unknowns.Add(NmSilencePhaseUnknowns.NmStsRepeat);
            phases.Add(BuildPhase(
                input, s, e, NmSilencePhaseKind.Unknown, NmSilenceBasis.NmStsRepeatMessage,
                gapFrames, nonNmFrames, nmSenders, dbcSenders, nmSts,
                IsWindowTruncated(input, e),
                "无法判定：停发前最后一帧处于 Repeat Message State（NMSts=0），不是 NM_09 的合规释放。"));
            return new NmSilencePhaseResult(phases, unknowns);
        }

        if (nmSenders.Count == 0)
        {
            unknowns.Add(NmSilencePhaseUnknowns.Unattributed);
            phases.Add(BuildPhase(
                input, s, e, NmSilencePhaseKind.Unknown, NmSilenceBasis.Unattributed,
                gapFrames, nonNmFrames, nmSenders, dbcSenders, nmSts,
                IsWindowTruncated(input, e),
                "无法判定：NM 发送者无法归属（无 J1939 SA / DBC sender），不能判断发送者是否仍醒着。"));
            return new NmSilencePhaseResult(phases, unknowns);
        }

        if (gapFrames.Count == 0)
        {
            unknowns.Add(NmSilencePhaseUnknowns.NoBusActivity);
            phases.Add(BuildPhase(
                input, s, e, NmSilencePhaseKind.Unknown, NmSilenceBasis.NoBusActivity,
                gapFrames, nonNmFrames, nmSenders, dbcSenders, nmSts,
                IsWindowTruncated(input, e),
                "无法判定：区间内总线没有任何帧；下电/记录停止/睡眠不可区分。"));
            return new NmSilencePhaseResult(phases, unknowns);
        }

        var t1 = s + timeoutSec;
        var t2 = t1 + waitSec;

        // —— w1：释放等待 / RSS 窗口 ——
        var w1Start = s;
        var w1End = Math.Min(t1, e);
        if (w1End > w1Start)
        {
            var stats = WindowStats.Build(gapFrames, nonNmFrames, w1Start, w1End, nmSenders, dbcSenders);
            if (stats.NmSenderAppFrames > 0)
            {
                phases.Add(BuildPhase(
                    input, w1Start, w1End, NmSilencePhaseKind.ReleaseWait,
                    NmSilenceBasis.NmSenderStillActive, stats.Frames, stats.NonNmFrames,
                    nmSenders, dbcSenders, nmSts, false,
                    string.Create(CultureInfo.InvariantCulture,
                        $"释放等待（推断）：NM 停发后 {timeoutSec:F3}s 内，发送者仍在发送 {stats.NmSenderAppFrames} 帧应用报文 → 仍处于网络模式。")));
            }
            else if (stats.OtherFrames > 0)
            {
                phases.Add(BuildPhase(
                    input, w1Start, w1End, NmSilencePhaseKind.ReleaseWait,
                    NmSilenceBasis.OtherSenderActive, stats.Frames, stats.NonNmFrames,
                    nmSenders, dbcSenders, nmSts, false,
                    string.Create(CultureInfo.InvariantCulture,
                        $"释放等待（推断）：NM 停发后未见本网络发送者的应用报文；其它已归属发送者 {stats.OtherFrames} 帧。")));
            }
            else if (stats.UnattributedFrames > 0)
            {
                // 未归属帧可能包含本网络发送者自己的应用报文，不能作为释放推断。
                unknowns.Add(NmSilencePhaseUnknowns.Unattributed);
                phases.Add(BuildPhase(
                    input, w1Start, w1End, NmSilencePhaseKind.Unknown,
                    NmSilenceBasis.Unattributed, stats.Frames, stats.NonNmFrames,
                    nmSenders, dbcSenders, nmSts, false,
                    string.Create(CultureInfo.InvariantCulture,
                        $"无法判定：窗口内有 {stats.UnattributedFrames} 帧但发送者无法归属，不能判断本网络发送者是否仍在发报文。")));
            }
            else
            {
                phases.Add(BuildPhase(
                    input, w1Start, w1End, NmSilencePhaseKind.Unknown,
                    NmSilenceBasis.NoBusActivity, stats.Frames, stats.NonNmFrames,
                    nmSenders, dbcSenders, nmSts, false,
                    "无法判定：该窗口总线无任何帧。"));
            }
        }

        // —— w2：准备休眠窗口（OEM §5.3.2：总线需静止，只允许清缓存）——
        var w2Start = Math.Max(s, t1);
        var w2End = Math.Min(t2, e);
        if (w2End > w2Start)
        {
            var stats = WindowStats.Build(gapFrames, nonNmFrames, w2Start, w2End, nmSenders, dbcSenders);
            var w2Duration = w2End - w2Start;
            var quietStart = w2Start + drainGrace;
            // 尾部没有下一 episode 的唤醒序列，不能裁掉末尾的「唤醒宽限」。
            var quietEnd = Math.Max(quietStart, w2End - (input.IsTail ? 0 : wakeupGrace));
            var framesInQuiet = CountFrames(gapFrames, quietStart, quietEnd);
            var maxQuietStretch = MaxQuietStretch(gapFrames, w2Start, w2End);
            var hasLivenessAfterStart = gapFrames.Exists(f => f.Timestamp >= w2Start);

            if (w2Duration < drainGrace)
            {
                // 窗口短于清缓存宽限：看不到发送者是否在宽限后停发。
                // 有帧时不得升级为 PrepareSleep（短窗口连续流量误判，review HIGH-1/2）。
                if (stats.TotalFrames > 0)
                {
                    unknowns.Add(NmSilencePhaseUnknowns.WindowTooShort);
                    phases.Add(BuildPhase(
                        input, w2Start, w2End, NmSilencePhaseKind.Unknown,
                        NmSilenceBasis.WindowTooShort, stats.Frames, stats.NonNmFrames,
                        nmSenders, dbcSenders, nmSts, IsWindowTruncated(input, w2End),
                        string.Create(CultureInfo.InvariantCulture,
                            $"无法判定：窗口仅 {w2Duration:F3}s，短于清缓存宽限 {drainGrace:F3}s；观察不到发送者是否在宽限后停发。")));
                }
                else
                {
                    unknowns.Add(NmSilencePhaseUnknowns.NoBusActivity);
                    phases.Add(BuildPhase(
                        input, w2Start, w2End, NmSilencePhaseKind.Unknown,
                        NmSilenceBasis.NoBusActivity, stats.Frames, stats.NonNmFrames,
                        nmSenders, dbcSenders, nmSts, IsWindowTruncated(input, w2End),
                        "无法判定：超时后总线无任何帧，无法区分准备休眠/断电/记录停止。"));
                }
            }
            else if (quietEnd <= quietStart)
            {
                // 判据窗被两个宽限夹空：改用「最长安静跨度」判断总线是否曾静止。
                // 连续流量（帧间隔始终 < 清缓存宽限）不得判 PrepareSleep。
                if (maxQuietStretch <= drainGrace)
                {
                    unknowns.Add(NmSilencePhaseUnknowns.AppTxAfterTimeout);
                    phases.Add(BuildPhase(
                        input, w2Start, w2End, NmSilencePhaseKind.Unknown,
                        NmSilenceBasis.AppTxAfterTimeout, stats.Frames, stats.NonNmFrames,
                        nmSenders, dbcSenders, nmSts, IsWindowTruncated(input, w2End),
                        string.Create(CultureInfo.InvariantCulture,
                            $"无法判定：超时后总线持续有帧（最长安静跨度 {maxQuietStretch:F3}s ≤ 清缓存宽限 {drainGrace:F3}s），与预睡眠要求的“总线活动静止”矛盾。")));
                }
                else if (hasLivenessAfterStart)
                {
                    phases.Add(BuildPhase(
                        input, w2Start, w2End, NmSilencePhaseKind.PrepareSleep,
                        NmSilenceBasis.NmSenderStoppedAfterDrain, stats.Frames, stats.NonNmFrames,
                        nmSenders, dbcSenders, nmSts, IsWindowTruncated(input, w2End),
                        string.Create(CultureInfo.InvariantCulture,
                            $"准备休眠（推断）：超时后存在 {maxQuietStretch:F3}s 总线安静段（≥ 清缓存宽限 {drainGrace:F3}s）；窗口内帧 {stats.TotalFrames} 帧。")));
                }
                else
                {
                    unknowns.Add(NmSilencePhaseUnknowns.NoBusActivity);
                    phases.Add(BuildPhase(
                        input, w2Start, w2End, NmSilencePhaseKind.Unknown,
                        NmSilenceBasis.NoBusActivity, stats.Frames, stats.NonNmFrames,
                        nmSenders, dbcSenders, nmSts, IsWindowTruncated(input, w2End),
                        "无法判定：超时后总线无任何帧，无法区分准备休眠/断电/记录停止。"));
                }
            }
            else if (framesInQuiet > 0 || maxQuietStretch <= drainGrace)
            {
                unknowns.Add(NmSilencePhaseUnknowns.AppTxAfterTimeout);
                var detail = framesInQuiet > 0
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"无法判定：超过 NM 超时后（清缓存宽限 {drainGrace:F3}s 之外）仍有 {framesInQuiet} 帧，与预睡眠要求的“总线活动静止”矛盾。")
                    : string.Create(CultureInfo.InvariantCulture,
                        $"无法判定：超时后最长安静跨度 {maxQuietStretch:F3}s ≤ 清缓存宽限 {drainGrace:F3}s，总线未静止（疑似持续周期报文）。");
                phases.Add(BuildPhase(
                    input, w2Start, w2End, NmSilencePhaseKind.Unknown,
                    NmSilenceBasis.AppTxAfterTimeout, stats.Frames, stats.NonNmFrames,
                    nmSenders, dbcSenders, nmSts, IsWindowTruncated(input, w2End),
                    detail));
            }
            else if (hasLivenessAfterStart)
            {
                phases.Add(BuildPhase(
                    input, w2Start, w2End, NmSilencePhaseKind.PrepareSleep,
                    NmSilenceBasis.NmSenderStoppedAfterDrain, stats.Frames, stats.NonNmFrames,
                    nmSenders, dbcSenders, nmSts, IsWindowTruncated(input, w2End),
                    string.Create(CultureInfo.InvariantCulture,
                        $"准备休眠（推断）：超时后存在 {maxQuietStretch:F3}s 总线安静段（≥ 清缓存宽限 {drainGrace:F3}s）；窗口内帧 {stats.TotalFrames} 帧。")));
            }
            else
            {
                unknowns.Add(NmSilencePhaseUnknowns.NoBusActivity);
                phases.Add(BuildPhase(
                    input, w2Start, w2End, NmSilencePhaseKind.Unknown,
                    NmSilenceBasis.NoBusActivity, stats.Frames, stats.NonNmFrames,
                    nmSenders, dbcSenders, nmSts, IsWindowTruncated(input, w2End),
                    "无法判定：超时后总线无任何帧，无法区分准备休眠/断电/记录停止。"));
            }
        }

        // —— w3：睡眠窗口 ——
        var w3Start = Math.Max(s, t2);
        var w3End = e;
        if (w3End > w3Start)
        {
            var stats = WindowStats.Build(gapFrames, nonNmFrames, w3Start, w3End, nmSenders, dbcSenders);
            var framesInWindow = stats.TotalFrames;
            var activityAfterGap = HasFrameAtOrAfter(input.AllFrames, e);
            if (!input.Timing.WaitBusSleepDeclared)
            {
                unknowns.Add(NmSilencePhaseUnknowns.WaitBusSleepDefaulted);
                phases.Add(BuildPhase(
                    input, w3Start, w3End, NmSilencePhaseKind.Unknown,
                    NmSilenceBasis.WaitBusSleepDefaulted, stats.Frames, stats.NonNmFrames,
                    nmSenders, dbcSenders, nmSts, IsWindowTruncated(input, w3End),
                    "无法判定：WaitBusSleep 未显式配置，按默认值处理，不推断睡眠边界。"));
            }
            else if (framesInWindow > 0)
            {
                unknowns.Add(NmSilencePhaseUnknowns.AppTxAfterTimeout);
                phases.Add(BuildPhase(
                    input, w3Start, w3End, NmSilencePhaseKind.Unknown,
                    NmSilenceBasis.AppTxAfterTimeout, stats.Frames, stats.NonNmFrames,
                    nmSenders, dbcSenders, nmSts, IsWindowTruncated(input, w3End),
                    string.Create(CultureInfo.InvariantCulture,
                        $"无法判定：等待窗口内仍有 {framesInWindow} 帧，总线未静止，睡眠不成立。")));
            }
            else if (activityAfterGap && !input.IsTail)
            {
                phases.Add(BuildPhase(
                    input, w3Start, w3End, NmSilencePhaseKind.Sleep,
                    NmSilenceBasis.NmSenderStoppedAfterDrain, stats.Frames, stats.NonNmFrames,
                    nmSenders, dbcSenders, nmSts, IsWindowTruncated(input, w3End),
                    "总线睡眠（推断）：等待窗口内总线无帧，且记录在下一 episode 继续。"));
            }
            else
            {
                unknowns.Add(NmSilencePhaseUnknowns.NoBusActivity);
                phases.Add(BuildPhase(
                    input, w3Start, w3End, NmSilencePhaseKind.Unknown,
                    NmSilenceBasis.NoBusActivity, stats.Frames, stats.NonNmFrames,
                    nmSenders, dbcSenders, nmSts, IsWindowTruncated(input, w3End),
                    "无法判定：窗口内无帧，且之后没有活动证据；无法区分睡眠/断电/记录停止。"));
            }
        }

        return new NmSilencePhaseResult(phases, unknowns);
    }

    private static NmSilencePhase BuildPhase(
        NmSilencePhaseInput input,
        double start,
        double end,
        NmSilencePhaseKind kind,
        NmSilenceBasis basis,
        IReadOnlyList<ReplayFrame> frames,
        IReadOnlyList<ReplayFrame> nonNmFrames,
        IReadOnlySet<string> nmSenders,
        IReadOnlyDictionary<(uint Id, bool IsExtended), string> dbcSenders,
        NmStsEvidence nmSts,
        bool isWindowTruncated,
        string detail)
    {
        var (nmApp, other, unattributed, senderActivity, sendersTruncated) =
            CountSenders(nonNmFrames, nmSenders, dbcSenders);
        var detailWithNmSts = nmSts switch
        {
            // 位值本身是实测的，但整条相位仍是 inferred；措辞必须避免把位值说成状态实测。
            NmStsEvidence.ReadySleepAtLastFrame => detail + "；停发前 NMSts=Ready Sleep（位值，仅作佐证）",
            NmStsEvidence.UnreadableAtLastFrame => detail + "；停发前 NMSts 读不出，未用它加强结论",
            NmStsEvidence.RepeatMessageAtLastFrame => detail + "；停发前 NMSts=Repeat Message State（位值，仅作佐证）",
            _ => detail,
        };
        return new NmSilencePhase(
            start, end, kind, basis,
            NonNmFrameCount: nonNmFrames.Count,
            NmSenderAppFrameCount: nmApp,
            OtherSenderFrameCount: other,
            UnattributedFrameCount: unattributed,
            BusMaxGapSec: MaxGap(frames),
            NmSts: nmSts,
            IsWindowTruncated: isWindowTruncated,
            AfterEpisodeIndex: input.PrecedingEpisode.Index,
            Detail: detailWithNmSts)
        {
            Senders = senderActivity,
            SendersTruncated = sendersTruncated,
        };
    }

    private static (int NmApp, int Other, int Unattributed,
        IReadOnlyList<NmSenderActivity> Senders, bool Truncated) CountSenders(
        IReadOnlyList<ReplayFrame> nonNmFrames,
        IReadOnlySet<string> nmSenders,
        IReadOnlyDictionary<(uint Id, bool IsExtended), string> dbcSenders)
    {
        var nmApp = 0;
        var other = 0;
        var unattributed = 0;
        var bySender = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var frame in nonNmFrames)
        {
            var key = Attribute(frame, dbcSenders);
            if (key is null)
            {
                unattributed++;
                continue;
            }

            if (nmSenders.Contains(key)) nmApp++;
            else other++;
            bySender[key] = bySender.TryGetValue(key, out var count) ? count + 1 : 1;
        }

        var ordered = new List<KeyValuePair<string, int>>(bySender);
        ordered.Sort(static (a, b) =>
        {
            var byCount = b.Value.CompareTo(a.Value);
            return byCount != 0 ? byCount : string.CompareOrdinal(a.Key, b.Key);
        });
        var truncated = ordered.Count > MaxSenders;
        var take = Math.Min(ordered.Count, MaxSenders);
        var senders = new NmSenderActivity[take];
        for (var i = 0; i < take; i++)
        {
            var item = ordered[i];
            senders[i] = new NmSenderActivity(item.Key, nmSenders.Contains(item.Key), item.Value);
        }

        return (nmApp, other, unattributed, senders, truncated);
    }

    private static NmStsEvidence ReadNmStsEvidence(
        NmSilencePhaseInput input,
        IReadOnlyDictionary<(uint Id, bool IsExtended), NmFrame> lastFrames,
        double timeoutSec,
        out bool anyRepeat)
    {
        anyRepeat = false;
        var role = FindBitRole(input.Profile.StandardBitRoles, "NmSts");
        if (role is null) return NmStsEvidence.NotAvailable;

        var sawFrame = false;
        var readable = 0;
        var ready = 0;
        var repeat = 0;
        var unreadable = 0;
        foreach (var pair in lastFrames)
        {
            var frame = pair.Value;
            // 只取 episode 末段仍在发送的报文；早停的报文已由 dropout 归因。
            if (frame.Frame.Timestamp < input.PrecedingEpisode.End - timeoutSec) continue;
            sawFrame = true;
            var data = frame.Frame.Data;
            if (data.Length <= role.Byte || role.Bit is < 0 or > 7)
            {
                unreadable++;
                continue;
            }

            readable++;
            var bit = (data[role.Byte] >> role.Bit) & 1;
            if (bit == 1) ready++;
            else repeat++;
        }

        if (repeat > 0)
        {
            anyRepeat = true;
            return NmStsEvidence.RepeatMessageAtLastFrame;
        }

        if (!sawFrame) return NmStsEvidence.NotAvailable;
        if (readable == 0) return NmStsEvidence.UnreadableAtLastFrame;
        if (unreadable > 0) return NmStsEvidence.UnreadableAtLastFrame;
        return ready > 0 ? NmStsEvidence.ReadySleepAtLastFrame : NmStsEvidence.NotAvailable;
    }

    private static NmBitRole? FindBitRole(IReadOnlyList<NmBitRole>? roles, string name)
    {
        if (roles is null) return null;
        for (var i = 0; i < roles.Count; i++)
        {
            if (string.Equals(roles[i].Name, name, StringComparison.Ordinal)) return roles[i];
        }
        return null;
    }

    private static HashSet<(uint Id, bool IsExtended)> BuildNmKeys(IReadOnlyList<NmFrame> frames)
    {
        var keys = new HashSet<(uint, bool)>();
        foreach (var frame in frames) keys.Add((ParseId(frame.Def.CanIdHex), frame.Def.IsExtended));
        return keys;
    }

    private static Dictionary<(uint Id, bool IsExtended), string> BuildDbcSenderMap(DbcDocument? dbc)
    {
        var map = new Dictionary<(uint, bool), string>();
        if (dbc is null) return map;
        foreach (var message in dbc.Messages)
        {
            var key = (message.Id & 0x1FFFFFFFu, (message.Id & 0x80000000u) != 0);
            if (!map.ContainsKey(key)) map.Add(key, message.Sender);
        }
        return map;
    }

    private static string? Attribute(
        ReplayFrame frame,
        IReadOnlyDictionary<(uint Id, bool IsExtended), string> dbcSenders)
    {
        if (dbcSenders.TryGetValue((frame.Id & 0x1FFFFFFFu, frame.IsExtended), out var sender)
            && !string.IsNullOrWhiteSpace(sender)
            && !string.Equals(sender, "Vector__XXX", StringComparison.Ordinal))
        {
            return sender;
        }

        // J1939 扩展帧：SA 在 ID 低字节。非 J1939 的扩展帧也可能落在这里，
        // 因此 Unknowns 必须带 sender_attribution_uses_j1939_sa 供复核。
        if (frame.IsExtended)
            return string.Create(CultureInfo.InvariantCulture, $"SA:0x{frame.Id & 0xFF:X2}");
        return null;
    }

    private static int CountFrames(IReadOnlyList<ReplayFrame> frames, double start, double end)
    {
        if (end <= start) return 0;
        var count = 0;
        foreach (var frame in frames)
        {
            if (frame.Timestamp >= start && frame.Timestamp < end) count++;
        }
        return count;
    }

    private static bool HasFrameAtOrAfter(IReadOnlyList<ReplayFrame> frames, double t)
    {
        for (var i = 0; i < frames.Count; i++)
        {
            if (frames[i].Timestamp >= t) return true;
        }
        return false;
    }

    private static double? MaxGap(IReadOnlyList<ReplayFrame> frames)
    {
        if (frames.Count < 2) return null;
        var max = 0.0;
        for (var i = 1; i < frames.Count; i++)
        {
            var gap = frames[i].Timestamp - frames[i - 1].Timestamp;
            if (gap > max) max = gap;
        }
        return max;
    }

    /// <summary>
    /// 窗口内最长「安静跨度」，包含窗口首尾到首/末帧的空白。
    /// 用于宽限夹空判据窗时判断总线是否曾静止；只看帧间隔会漏掉尾部静止。
    /// </summary>
    private static double MaxQuietStretch(IReadOnlyList<ReplayFrame> frames, double start, double end)
    {
        var first = double.NaN;
        var last = double.NaN;
        var max = 0.0;
        foreach (var frame in frames)
        {
            if (frame.Timestamp < start || frame.Timestamp >= end) continue;
            if (double.IsNaN(first)) first = frame.Timestamp;
            else
            {
                var gap = frame.Timestamp - last;
                if (gap > max) max = gap;
            }
            last = frame.Timestamp;
        }

        if (double.IsNaN(first)) return Math.Max(end - start, 0);
        var head = first - start;
        if (head > max) max = head;
        var tail = end - last;
        if (tail > max) max = tail;
        return Math.Max(max, 0);
    }

    private static bool IsWindowTruncated(NmSilencePhaseInput input, double end)
        => input.IsTail
           && Math.Abs(end - input.WindowEnd) <= Math.Max(Math.Abs(input.WindowEnd), 1.0) * 1e-9;

    private static uint ParseId(string hex)
        => uint.Parse(hex.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>单个相位窗口的统计快照。</summary>
    private sealed record WindowStats(
        IReadOnlyList<ReplayFrame> Frames,
        IReadOnlyList<ReplayFrame> NonNmFrames,
        int NmSenderAppFrames,
        int OtherFrames,
        int UnattributedFrames,
        int TotalFrames,
        double? BusMaxGapSec)
    {
        public static WindowStats Build(
            IReadOnlyList<ReplayFrame> allFrames,
            IReadOnlyList<ReplayFrame> nonNmFrames,
            double start,
            double end,
            IReadOnlySet<string> nmSenders,
            IReadOnlyDictionary<(uint Id, bool IsExtended), string> dbcSenders)
        {
            var frames = new List<ReplayFrame>();
            foreach (var frame in allFrames)
            {
                if (frame.Timestamp >= start && frame.Timestamp < end) frames.Add(frame);
            }

            var nonNm = new List<ReplayFrame>();
            var nmApp = 0;
            var other = 0;
            var unattributed = 0;
            foreach (var frame in nonNmFrames)
            {
                if (frame.Timestamp < start || frame.Timestamp >= end) continue;
                nonNm.Add(frame);
                var key = Attribute(frame, dbcSenders);
                if (key is null) unattributed++;
                else if (nmSenders.Contains(key)) nmApp++;
                else other++;
            }

            return new WindowStats(frames, nonNm, nmApp, other, unattributed,
                frames.Count, MaxGap(frames));
        }
    }
}
