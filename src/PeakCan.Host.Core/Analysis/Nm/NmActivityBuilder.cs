namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 报文级阶段推断（spec §7.3）：wakeup → active → released / silent。
/// 同一 episode 内各段首尾相接且非零长度；跨 episode 的静默空档单独留白，
/// 由整网行渲染，报文行不得跨越。M1 只做按报文的推断，不拆节点。
/// <para>
/// 前提：<c>episodes</c> 必须覆盖该报文的全部帧（<see cref="NmEpisodeBuilder"/>
/// 的产物满足）；落在 episode 之外的帧不会生成段（公众调用方自行保证切片完整）。
/// </para>
/// </summary>
public static class NmActivityBuilder
{
    /// <param name="stsProbe">
    /// NMSts 位读取器（spec §7.5.1）；null = 未声明或不可用 → 退回时间推断。
    /// </param>
    public static IReadOnlyList<NmSegment> Build(
        IReadOnlyList<NmFrame> messageFrames,
        NmMessageDef def,
        IReadOnlyList<NmEpisode> episodes,
        NmEffectiveTiming timing,
        IReadOnlyList<NmFrame> allNmFrames,
        double windowEnd,
        NmStsProbe? stsProbe = null)
    {
        if (messageFrames.Count == 0) return Array.Empty<NmSegment>();

        var ordered = messageFrames.OrderBy(f => f.Frame.Timestamp).ToArray();

        // 单帧构不成任何非零长度的阶段：不伪造零长段，返回空活动区间。
        if (ordered.Length == 1) return Array.Empty<NmSegment>();

        var cycleSec = Math.Max(timing.CycleMs, 1) / 1000.0;

        // 阶段属于 episode：两段唤醒之间的静默空档不能被画进同一条
        // wakeup/active 段，否则第二段唤醒永远不会出现，且"发送中"的起止
        // 时间会横跨整个睡眠期。按 episode 切片逐段生成。
        if (episodes is null || episodes.Count == 0)
        {
            var segments = new List<NmSegment>(3);
            AppendEpisodeSegments(
                segments, ordered, episode: null,
                cycleSec, timing, def, allNmFrames, windowEnd, phaseLimit: windowEnd,
                stsProbe);
            return segments;
        }

        var all = new List<NmSegment>(episodes.Count * 2);
        var sorted = episodes.OrderBy(e => e.Start).ToArray();
        int cursor = 0;
        for (int i = 0; i < sorted.Length; i++)
        {
            var episode = sorted[i];
            while (cursor < ordered.Length && ordered[cursor].Frame.Timestamp < episode.Start)
                cursor++;
            var begin = cursor;
            while (cursor < ordered.Length && ordered[cursor].Frame.Timestamp <= episode.End)
                cursor++;
            if (cursor == begin) continue;

            // 尾段不得越过下一 episode 的起点：episode 用全局静默阈值切分，
            // 而尾段可能按报文级 timeout 外推；两者不一致时会在时间条上叠段。
            var phaseLimit = i + 1 < sorted.Length ? sorted[i + 1].Start : windowEnd;
            AppendEpisodeSegments(
                all, ordered[begin..cursor], episode,
                cycleSec, timing, def, allNmFrames, windowEnd, phaseLimit, stsProbe);
        }

        return all;
    }

    /// <summary>
    /// 生成一个 episode 内的阶段段：wakeup → active → released/silent。
    /// <paramref name="episode"/> 为 null 时视为没有 episode 边界（兼容旧调用）。
    /// </summary>
    private static void AppendEpisodeSegments(
        List<NmSegment> segments,
        NmFrame[] ordered,
        NmEpisode? episode,
        double cycleSec,
        NmEffectiveTiming timing,
        NmMessageDef def,
        IReadOnlyList<NmFrame> allNmFrames,
        double windowEnd,
        double phaseLimit,
        NmStsProbe? stsProbe)
    {
        if (ordered.Length == 0) return;

        var first = ordered[0].Frame.Timestamp;
        var last = ordered[^1].Frame.Timestamp;

        // 有可用的 NMSts 位时按状态切段（spec §7.5.1），否则退回时间推断。
        // 判据是「本 episode 内至少有一帧读得出」：全都读不出等于没有状态位，
        // 此时硬画 repeat/normal 就是凭空造结论。
        var bodyEnd = last;
        if (stsProbe is not null && TryAppendNmStsSegments(
                segments, ordered, stsProbe, timing, windowEnd, out var stsEnd))
        {
            bodyEnd = stsEnd;
        }
        else
        {
            var wakeupEnd = ResolveWakeupEnd(ordered, first, last, cycleSec, timing);
            if (wakeupEnd > first) segments.Add(new NmSegment(first, wakeupEnd, "wakeup"));

            // spec §7.5.2 真值表第 1 行对 M1 路径同样适用：末帧恰好是窗口终点时，
            // 这段被窗口切断，时长只是下限。不置位会把「还在发、没测完」呈现成完整时长。
            if (last > wakeupEnd)
            {
                segments.Add(new NmSegment(
                    wakeupEnd, last, "active",
                    IsWindowTruncated: IsAtWindowBoundary(last, windowEnd)));
            }
            else if (segments.Count > 0)
            {
                // 只有 wakeup 段（末帧落在观察窗内）：它同样可能被窗口切断。
                var wakeup = segments[^1];
                if (string.Equals(wakeup.Kind, "wakeup", StringComparison.Ordinal))
                {
                    segments[^1] = wakeup with
                    {
                        IsWindowTruncated = IsAtWindowBoundary(wakeup.End, windowEnd),
                    };
                }
            }
        }

        // 释放/全静默段：先按报文归属的 episode 结束；若本报文就是该 episode 最后
        // 停止的报文，episode 按 spec §7.3 要到"全部静默 ≥ timeout"才结束，
        // 因此观察窗延续到 min(窗口结束, 末帧 + 超时)。窗口在阈值前截断时结束时刻
        // 不可判明（BoundaryEnd），不伪造尾段。
        var phaseEnd = last;
        if (episode is { } ep)
        {
            if (ep.End > last)
            {
                phaseEnd = ep.End;
            }
            else if (!ep.BoundaryEnd)
            {
                phaseEnd = Math.Min(windowEnd, last + timing.TimeoutMs / 1000.0);
            }
        }

        // 无论尾段来自 episode.End 还是 last+timeout，都不允许压到下一 episode/窗口之外。
        var tailEndedByBoundary = phaseEnd >= phaseLimit - 1e-9;
        phaseEnd = Math.Min(phaseEnd, phaseLimit);
        if (phaseEnd > bodyEnd)
        {
            var othersActive = OthersActiveBetween(bodyEnd, phaseEnd, def, allNmFrames);
            // spec §7.5.2 真值表：
            //   第 1 行 —— 终点恰好等于窗口终点 → 被窗口切断（时长只是下限）。
            //   第 2 行 —— 终点由 episode 边界（下一 episode 起点）界定 → 边界已知。
            // 两者互斥：终点被夹到 phaseLimit 才算「由边界界定」。
            segments.Add(new NmSegment(
                bodyEnd, phaseEnd, othersActive ? "released" : "silent",
                IsWindowTruncated: IsAtWindowBoundary(phaseEnd, windowEnd),
                StateBoundaryKnown: tailEndedByBoundary));
        }
    }

    /// <summary>
    /// 按 NMSts 逐帧切段（spec §7.5.1 / §7.5.1.1）。
    /// <para>
    /// 起点 = 首帧或状态变化帧；终点 = 下一次变化帧；末段到本 episode 内末帧。
    /// NMSts=0 → <c>repeat</c>，NMSts=1 → <c>normal</c>。
    /// <c>repeat</c> 段内再切 <c>immediate</c> / <c>normal_transmit</c> 子段，
    /// 否则快发信息会被抹掉（§7.5.1.1：快发段是 NM_03/NM_04 的直接证据）。
    /// </para>
    /// </summary>
    /// <returns>true = 至少画出一段；false = 本 episode 内没有任何可读状态位，调用方应退回推断。</returns>
    private static bool TryAppendNmStsSegments(
        List<NmSegment> segments,
        NmFrame[] ordered,
        NmStsProbe probe,
        NmEffectiveTiming timing,
        double windowEnd,
        out double bodyEnd)
    {
        bodyEnd = ordered[^1].Frame.Timestamp;

        // 逐帧读出状态；读不出的帧记 null，后面单独成段。
        var states = new bool?[ordered.Length];
        var anyReadable = false;
        for (var i = 0; i < ordered.Length; i++)
        {
            states[i] = probe.Read(ordered[i]);
            if (states[i] is not null) anyReadable = true;
        }
        if (!anyReadable) return false;

        var produced = false;
        var runStart = 0;
        for (var i = 1; i <= ordered.Length; i++)
        {
            // 末尾或状态发生变化：收束当前 run。
            var changed = i == ordered.Length || states[i] != states[runStart];
            if (!changed) continue;

            var from = ordered[runStart].Frame.Timestamp;
            var to = i < ordered.Length
                ? ordered[i].Frame.Timestamp
                : ordered[^1].Frame.Timestamp;

            // 末段终点恰好等于窗口终点 → 该段被窗口切断（spec §7.5.2 真值表第 1 行）。
            // 中间段终点由「状态变化帧」界定，不是截断。
            var truncated = i == ordered.Length && IsAtWindowBoundary(to, windowEnd);

            // 零长段不画（同一时刻的重复帧会切出 from == to）。
            if (to > from) AppendRun(
                segments, ordered, runStart, i, from, to, states[runStart], timing, truncated);
            if (to > from) produced = true;

            runStart = i;
        }

        return produced;
    }

    /// <summary>
    /// 把一段同状态区间落成段。NMSts=1 → normal；NMSts=0 → repeat，
    /// 并在其内部按快发检测再切子段；null（该段全部读不出）→ 标 inferred 的 unknown。
    /// </summary>
    private static void AppendRun(
        List<NmSegment> segments,
        NmFrame[] ordered,
        int startIndex,
        int endIndex,
        double from,
        double to,
        bool? state,
        NmEffectiveTiming timing,
        bool isWindowTruncated)
    {
        if (state is null)
        {
            // 这一小段逐帧不可读（spec §7.5.1.2）：只降级这一小段，不牵连整段。
            segments.Add(new NmSegment(
                from, to, "unknown", Evidence: "inferred",
                IsWindowTruncated: isWindowTruncated));
            return;
        }

        // spec §7.5.2 真值表第 3/4 行：NMSts 段的 StateBoundaryKnown 只取决于
        // **终点是不是状态变化帧**——这正是字段本身的定义（「段内是否观测到状态跃迁」）。
        //   endIndex < ordered.Length → 终点是变化帧 → true
        //   末段跑到末帧 → 窗口内只有单一状态，状态边界不可判明 → false
        // 与截断无关：截断由 IsWindowTruncated 表达，两者正交（§7.5.2 强调过）。
        var boundaryKnown = endIndex < ordered.Length;

        if (state is true)
        {
            // spec §7.5.1.3：normal 段只代表「观测到的常规操作发送段」，
            // 终点是本 episode 内末帧，不是完整 NOS 生命周期。
            segments.Add(new NmSegment(
                from, to, "normal",
                Evidence: "measured",
                IsWindowTruncated: isWindowTruncated,
                StateBoundaryKnown: boundaryKnown));
            return;
        }

        // NMSts=0：Repeat Message State。内部按快发再切（spec §7.5.1.1）。
        //
        // 命名裁决：§7.5.1.1 把无快发的那部分叫 normal_transmit，而 §7.5.5 验收表
        // 对同一区间写的是 repeat。两者同义、同区间，取 §7.5.5 的 repeat——
        // 它是硬验收门，且 normal_transmit 与 repeat 并存会让时间条出现两个同义 kind。
        // 快发段仍单独切出来（NM_03/NM_04 的直接证据不能丢）。
        var timestamps = new double[endIndex - startIndex];
        for (var k = startIndex; k < endIndex; k++) timestamps[k - startIndex] = ordered[k].Frame.Timestamp;

        var burst = NmBurstDetector.DetectInEpisode(
            timestamps, from, to, timing,
            anchorAtStart: true,
            preferImmediateCycle: timing.ImmediateCycleDeclared);

        // 快发段可见的条件：检出候选且**非零长**。
        // 不能要求 `burst.End < to`——那样「RMS 由快发构成、后面没有稳态帧」时
        // 整个 immediate 会被抹掉，而那正是最需要它作证的情形（§7.5.1.1）。
        if (burst.IsCandidate && burst.End > from)
        {
            segments.Add(new NmSegment(
                from, burst.End, "immediate", Evidence: "measured",
                // 快发段被窗口切断时，实测帧数只是下限（spec §7.5.3.1 IsIndeterminate）。
                IsWindowTruncated: burst.IsIndeterminate && isWindowTruncated));
            if (to > burst.End)
            {
                segments.Add(new NmSegment(
                    burst.End, to, "repeat", Evidence: "measured",
                    IsWindowTruncated: isWindowTruncated,
                    StateBoundaryKnown: boundaryKnown));
            }
        }
        else
        {
            // 无快发：整段就是 repeat。
            // 「NMSts=repeat 但无快发」是合法的 NM_02 远程唤醒形态，不是违规（§7.5.1）。
            segments.Add(new NmSegment(
                from, to, "repeat", Evidence: "measured",
                IsWindowTruncated: isWindowTruncated,
                StateBoundaryKnown: boundaryKnown));
        }
    }

    /// <summary>
    /// 段终点是否恰好落在窗口/报告边界上（spec §7.5.2 的 <c>IsWindowTruncated</c> 判据）。
    /// <para>
    /// 用相对容差而非精确相等：时间戳是浮点累加值，末帧与窗口终点常有 1e-12 级差异。
    /// 容差按窗口长度缩放，避免长窗口下把「差 1μs」误判成截断。
    /// </para>
    /// </summary>
    private static bool IsAtWindowBoundary(double end, double windowEnd)
        => Math.Abs(end - windowEnd) <= Math.Max(Math.Abs(windowEnd), 1.0) * 1e-9;

    /// <summary>
    /// 唤醒段的终点（spec §7.3 / §7.5.3）。
    /// <para>
    /// 快发检测走唯一实现 <see cref="NmBurstDetector"/>，不在这里另写阈值
    /// （§7.5.3.1：三处阈值冲突的历史教训）。
    /// </para>
    /// <para>
    /// <b>两条路径语义不同，不能合并</b>（spec §7.5.3 / §7.5.5）：
    /// </para>
    /// <list type="bullet">
    /// <item><b>未声明 T_REPEAT_MESSAGE</b>（M1 路径）：唤醒段 = 观测到的快发段，
    /// 无快发时用周期当观察窗。这是 M1 的老口径，§7.5.5 的回归门钉的就是它。</item>
    /// <item><b>已声明</b>（企标路径）：唤醒段 = <b>整个 Repeat Message State 的推断时长</b>，
    /// 按有无快发在 1700 / 1500 之间选。快发在这里只用来<b>选窗</b>，
    /// 不用来定终点——§7.5 的错误表正是在纠正「有快发时只认突发段，
    /// 把剩下 2.14s 的 RMS 画成发送中」。</item>
    /// </list>
    /// </summary>
    private static double ResolveWakeupEnd(
        NmFrame[] ordered, double first, double last, double cycleSec, NmEffectiveTiming timing)
    {
        var timestamps = new double[ordered.Length];
        for (var i = 0; i < ordered.Length; i++) timestamps[i] = ordered[i].Frame.Timestamp;

        // preferImmediateCycle：未显式声明 ImmediateCycleMs 时用 M1 的 0.5×cycle 阈值
        // （spec §7.5.3.1 回退表）。不能靠值是否为 0 推断：它有默认值 20。
        var preferImmediate = timing.ImmediateCycleDeclared;

        if (timing.RepeatWindowSource != NmRepeatWindowSource.StandardDual)
        {
            // —— M1 路径（None / M1Single 都是 M1 语义）——
            // 唤醒终点 = 从首帧起算的快发段末帧；无快发时用周期 / M1 的单一值当观察窗。
            // M1Single 也走这里：M1 的 RepeatMessageMs 只代表观察窗，不代表 RMS 总时长，
            // 给它套企标的「选窗」语义会让既有项目的唤醒段突然变长（§7.5.5）。
            var m1Burst = NmBurstDetector.DetectInEpisode(
                timestamps, first, last, timing,
                anchorAtStart: true, preferImmediateCycle: preferImmediate);
            if (m1Burst.IsCandidate && m1Burst.End > first) return m1Burst.End;

            var fallbackMs = timing.RepeatWindowSource == NmRepeatWindowSource.M1Single
                ? timing.RepeatMessageWithBurstMs
                : timing.CycleMs;
            return Math.Min(first + Math.Max(fallbackMs, 1) / 1000.0, last);
        }

        // —— 企标路径：快发只选窗，窗口是 RMS 的期望总时长 ——
        // 这里用 anchorAtStart:false：§7.5.3 的判据是「episode 内**有**快发段」，
        // 不要求它从首帧起算。用 true 会把「首帧长间隔 + 中途快发」误判成无快发，
        // 从而错选 1500。
        var anyBurst = NmBurstDetector.DetectInEpisode(
            timestamps, first, last, timing,
            anchorAtStart: false, preferImmediateCycle: preferImmediate);

        // spec §7.5.3.1：「回退时不得选 1700」——cycle_ratio_fallback 检出的快发
        // 证明不了「符合 N_ImmediateNM_TIMES」，按 1500 兜底。
        // 只看 IsCandidate 不看 Detector 会让未声明 immediate_cycle_ms 的 profile 误选 1700。
        var hasCompliantShapeBurst = anyBurst.IsCandidate
            && string.Equals(anyBurst.Detector, "immediate", StringComparison.Ordinal);

        var windowMs = hasCompliantShapeBurst
            ? timing.RepeatMessageWithBurstMs
            : timing.RepeatMessageWithoutBurstMs;
        return Math.Min(first + windowMs / 1000.0, last);
    }

    private static bool OthersActiveBetween(
        double from, double to, NmMessageDef def, IReadOnlyList<NmFrame> allNmFrames)
        => allNmFrames.Any(f =>
            f.Frame.Timestamp > from && f.Frame.Timestamp <= to
            && !(string.Equals(f.Def.CanIdHex, def.CanIdHex, StringComparison.Ordinal)
                 && f.Def.IsExtended == def.IsExtended));
}
