namespace PeakCan.Host.Core.Analysis.Nm;

public sealed record NmContentInput(
    IReadOnlyList<NmFrame> Frames,
    NmProfile Profile,
    int CycleMs,
    // 可选 episode 列表：提供后事件按时间落到真实 episode 编号；
    // 不提供时保持 0，兼容只关心事件内容的调用方。
    IReadOnlyList<NmEpisode>? Episodes = null,
    // 统一时序（spec §5.2）：null 时回落到 Profile.Timings / M1 默认。
    NmGlobalTiming? Timing = null);

public sealed record NmContentResult(
    IReadOnlyList<NmEvent> Events,
    IReadOnlyList<NmFinding> Findings,
    IReadOnlyList<NmTruncation> Truncations);

/// <summary>
/// 内容级观测：突发、字节/位变化、NID/CBV/PN/用户数据。所有"语义"判断只在
/// profile 明确给出对应输入时才做。
/// </summary>
public static class NmContentAnalyzer
{
    private const int MaxEventsPerMessage = 200;

    public static NmContentResult Analyze(NmContentInput input)
    {
        var events = new List<NmEvent>();
        var findings = new List<NmFinding>();
        var truncations = new List<NmTruncation>();
        // 统一时序（spec §5.2）：漏帧/突发阈值与 episode 切分同源。
        var globalTiming = input.Timing
            ?? NmTimingDefaults.FromProfile(input.Profile.Timings, input.Profile.StandardTimings);

        // 分组键必须含扩展位：标准 0x500 与扩展 0x500 是两条不同报文，
        // 合并后会打散间隔并串用错误的周期/超时阈值。
        foreach (var group in input.Frames.GroupBy(f => (f.Def.CanIdHex, f.Def.IsExtended)))
        {
            var ordered = group.OrderBy(f => f.Frame.Timestamp).ToArray();
            var canId = group.Key.CanIdHex;
            var emitted = 0;
            var attempted = 0;
            var dropped = 0;

            void Emit(NmEvent e)
            {
                attempted++;
                if (emitted >= MaxEventsPerMessage)
                {
                    dropped++;
                    return;
                }
                // 事件时间落在哪个 episode，就记哪个编号；取不到时沿用 0。
                // 扩展位随分组写入：同数值标准/扩展帧的 subject 过滤必须能区分。
                events.Add(e with
                {
                    EpisodeIndex = NmEpisodeBuilder.IndexAt(e.T, input.Episodes),
                    IsExtended = group.Key.IsExtended,
                });
                emitted++;
            }

            // 突发阈值与周期统计同源：报文级周期优先，其次 profile 全局周期。
            var burstCycleMs = ordered[0].Def.CycleMs ?? input.CycleMs;
            DetectBurst(ordered, burstCycleMs, Emit);
            DetectByteAndBitChanges(ordered, canId, Emit);
            DetectMissedFrames(ordered, input.CycleMs, globalTiming, Emit);

            // 只有真正丢弃过事件才记截断；Returned/Total 都是事件数，不是帧数。
            if (dropped > 0)
                truncations.Add(new NmTruncation("events:" + canId, emitted, attempted));

            CheckNid(ordered, input, findings);
            CheckCbv(ordered, input, findings);
            CheckUserData(ordered, input, findings);
        }

        return new NmContentResult(events, findings, truncations);
    }

    private static void DetectBurst(NmFrame[] ordered, int cycleMs, Action<NmEvent> emit)
    {
        var cycleSec = cycleMs / 1000.0;
        for (int i = 1; i < ordered.Length; i++)
        {
            if (ordered[i].Frame.Timestamp - ordered[i - 1].Frame.Timestamp < cycleSec) continue;

            // 静默后的短间隔序列：连续 ≥2 个 < 0.5×cycle 的间隔。
            var burstStart = i;
            int count = 0;
            for (int j = i; j + 1 < ordered.Length; j++)
            {
                var delta = ordered[j + 1].Frame.Timestamp - ordered[j].Frame.Timestamp;
                if (delta >= 0.5 * cycleSec) break;
                count++;
            }
            if (count >= 2)
            {
                emit(new NmEvent(ordered[burstStart].Frame.Timestamp, "burst_observed",
                    ordered[burstStart].Def.Sender, ordered[burstStart].Def.CanIdHex,
                    $"{count + 1} 帧短间隔（相邻 < {0.5 * cycleMs:F0}ms）", 0, "measured"));
            }
        }
    }

    /// <summary>
    /// 按变化段聚合：每个发生过变化的字节只产一条 <c>byte_change</c>，每个发生过翻转的位
    /// 只产一条 <c>bit_change</c>。避免递增计数器/噪声字节把事件数打满上限。
    /// </summary>
    private static void DetectByteAndBitChanges(NmFrame[] ordered, string canId, Action<NmEvent> emit)
    {
        if (ordered.Length < 2) return;

        var states = new Dictionary<int, ByteChangeState>();
        var previous = ordered[0].Frame.Data;
        for (int i = 1; i < ordered.Length; i++)
        {
            var current = ordered[i].Frame.Data;
            var timestamp = ordered[i].Frame.Timestamp;
            for (int b = 0; b < Math.Min(previous.Length, current.Length); b++)
            {
                if (previous[b] == current[b]) continue;
                var xor = previous[b] ^ current[b];
                if (!states.TryGetValue(b, out var state))
                {
                    state = new ByteChangeState { FirstTs = timestamp, FirstValue = previous[b] };
                    states[b] = state;
                }
                state.LastTs = timestamp;
                state.LastValue = current[b];
                state.Count++;
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((xor & (1 << bit)) == 0) continue;
                    if (state.BitCounts[bit] == 0) state.BitFirstTs[bit] = timestamp;
                    state.BitLastTs[bit] = timestamp;
                    state.BitCounts[bit]++;
                }
            }
            previous = current;
        }

        foreach (var pair in states.OrderBy(p => p.Key))
        {
            var b = pair.Key;
            var state = pair.Value;
            emit(new NmEvent(state.FirstTs, "byte_change", null, canId,
                $"byte{b}: 0x{state.FirstValue:X2} → 0x{state.LastValue:X2}，变化 {state.Count} 次（{state.FirstTs:F3}s → {state.LastTs:F3}s）",
                0, "measured"));

            for (int bit = 0; bit < 8; bit++)
            {
                if (state.BitCounts[bit] == 0) continue;
                emit(new NmEvent(state.BitFirstTs[bit], "bit_change", null, canId,
                    $"byte{b}.bit{bit}: 翻转 {state.BitCounts[bit]} 次（{state.BitFirstTs[bit]:F3}s → {state.BitLastTs[bit]:F3}s）",
                    0, "measured"));
            }
        }
    }

    /// <summary>
    /// spec §7.5.6：1.5×生效周期 &lt; 间隔 ≤ 生效超时 → 漏发一次的证据。
    /// 事件计入该报文的 200 条预算；共享 ID 不能否定漏帧，不参与抑制。
    /// </summary>
    private static void DetectMissedFrames(
        NmFrame[] ordered, int globalCycleMs, NmGlobalTiming timing, Action<NmEvent> emit)
    {
        if (ordered.Length < 2) return;

        var def = ordered[0].Def;
        var cycleMs = def.CycleMs ?? globalCycleMs;
        if (cycleMs <= 0) return;
        var timeoutMs = NmTiming.Resolve(def, timing).TimeoutMs;
        var cycleSec = cycleMs / 1000.0;
        var timeoutSec = timeoutMs / 1000.0;

        for (int i = 1; i < ordered.Length; i++)
        {
            var gap = ordered[i].Frame.Timestamp - ordered[i - 1].Frame.Timestamp;
            if (gap <= 1.5 * cycleSec || gap > timeoutSec) continue;

            // 长间隔可能漏多帧：按间隔/周期估算，避免一律写成"1 帧"的事实错误；
            // 判据针对一次漏发时仍保留 spec 要求的"可能漏发 1 帧"措辞。
            var missed = Math.Max(1, (int)Math.Round(gap / cycleSec) - 1);
            var phrase = missed == 1 ? "可能漏发 1 帧" : $"可能漏发约 {missed} 帧";
            emit(new NmEvent(
                ordered[i - 1].Frame.Timestamp, "missed_frame", def.Sender, def.CanIdHex,
                $"{gap:F2}s（期望 {cycleMs}ms，{phrase}）", 0, "inferred"));
        }
    }

    /// <summary>单个字节在整段报文内的变化汇总。</summary>
    private sealed class ByteChangeState
    {
        public double FirstTs;
        public double LastTs;
        public byte FirstValue;
        public byte LastValue;
        public int Count;

        // 位级明细：每个位记录首次/末次翻转时间与翻转次数。
        public readonly double[] BitFirstTs = new double[8];
        public readonly double[] BitLastTs = new double[8];
        public readonly int[] BitCounts = new int[8];
    }

    private static void CheckNid(NmFrame[] ordered, NmContentInput input, List<NmFinding> findings)
    {
        if (input.Profile.Layout?.NidByte is not { } nidByte) return;

        var frames = ordered.Where(f => f.Frame.Data.Length > nidByte).ToArray();
        if (frames.Length == 0) return;

        var expected = input.Profile.Nodes
            .FirstOrDefault(n => n.Name == ordered[0].Def.Sender)?.StationAddress;
        var values = frames.Select(f => f.Frame.Data[nidByte]).Distinct().ToArray();
        var stationAddresses = input.Profile.Nodes
            .Where(n => n.StationAddress is { })
            .Select(n => n.StationAddress!.Value)
            .ToHashSet();

        if (values.Length > 1)
        {
            // 同一 CAN ID 下出现多个 NID → 视为共享 ID（多节点共用同一报文 ID）。
            // 此时 NID 本就应当各不相同，不能把"NID 变化"当成异常，故不报 NM_NID_CHANGED；
            // 逐个 NID 检查是否在节点表中，每个未知 NID 只报一条，Message 带上 NID 值。
            if (stationAddresses.Count == 0) return;
            foreach (var value in values.OrderBy(v => v))
            {
                if (stationAddresses.Contains(value)) continue;
                var firstAt = frames.First(f => f.Frame.Data[nidByte] == value).Frame.Timestamp;
                findings.Add(new NmFinding("NM_UNKNOWN_NID", "info",
                    $"共享 ID {ordered[0].Def.CanIdHex} 下的 NID 0x{value:X2} 不在 DBC 节点表中",
                    new[] { firstAt }, IsInference: true,
                    "确认该报文是否共享 ID，以及节点表是否遗漏该节点"));
            }
            return;
        }

        var nid = values[0];

        // 有期望站地址且不相等 → MISMATCH；命中后不再重复报 UNKNOWN（同一取值只给一个结论）。
        if (expected is { } stationAddress && nid != stationAddress)
        {
            findings.Add(new NmFinding("NM_NID_MISMATCH", "warn",
                $"NID 0x{nid:X2} ≠ 站地址 0x{stationAddress:X2}",
                new[] { frames[0].Frame.Timestamp }, false,
                "核对 DBC 站地址与发送内容"));
            return;
        }

        if (stationAddresses.Count > 0 && !stationAddresses.Contains(nid))
        {
            findings.Add(new NmFinding("NM_UNKNOWN_NID", "info",
                $"NID 0x{nid:X2} 不在 DBC 节点表中",
                new[] { frames[0].Frame.Timestamp }, true,
                "确认矩阵是否遗漏该节点"));
        }
    }

    private static void CheckCbv(NmFrame[] ordered, NmContentInput input, List<NmFinding> findings)
    {
        if (input.Profile.Layout?.CbvByte is not { } cbvByte) return;
        if (input.Profile.Features?.PnEnabled != false) return;

        // AUTOSAR CanNm 的 PNI 位于 CBV bit6（0x40），bit4（0x10）是 Active Wakeup，不得混淆。
        // 只在用户明确配置 CBV 字节时解释；位角色是硬编码的（M1 的 NmLayout 没有位定义），
        // 所以结论按推断输出。
        var hit = ordered.FirstOrDefault(f => f.Frame.Data.Length > cbvByte &&
                                              (f.Frame.Data[cbvByte] & 0x40) != 0);
        if (hit is not null)
        {
            findings.Add(new NmFinding("NM_PNI_WHILE_PN_DISABLED", "warn",
                "PN 关闭但 CBV PNI 位置 1", new[] { hit.Frame.Timestamp }, IsInference: true,
                "核对 CanNmPnEnabled 与实际发送内容"));
        }
    }

    private static void CheckUserData(NmFrame[] ordered, NmContentInput input, List<NmFinding> findings)
    {
        var layout = input.Profile.Layout;
        if (layout?.UserDataOffset is not { } offset || layout.UserDataLength != 0) return;

        var hit = ordered.FirstOrDefault(f => f.Frame.Data.Skip(offset).Any(b => b != 0));
        if (hit is not null)
        {
            findings.Add(new NmFinding("NM_USER_DATA_UNUSED_NONZERO", "warn",
                $"用户数据长度为 0，但 byte{offset} 之后存在非零字节",
                new[] { hit.Frame.Timestamp }, false,
                "核对用户数据长度配置"));
        }
    }
}
