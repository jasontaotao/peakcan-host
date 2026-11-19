namespace PeakCan.Host.Core.Analysis.Nm;

public sealed record NmContentInput(
    IReadOnlyList<NmFrame> Frames,
    NmProfile Profile,
    int CycleMs);

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

        foreach (var group in input.Frames.GroupBy(f => f.Def.CanIdHex, StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(f => f.Frame.Timestamp).ToArray();
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
                events.Add(e);
                emitted++;
            }

            DetectBurst(ordered, input.CycleMs, Emit);
            DetectByteAndBitChanges(ordered, group.Key, Emit);

            // 只有真正丢弃过事件才记截断；Returned/Total 都是事件数，不是帧数。
            if (dropped > 0)
                truncations.Add(new NmTruncation("events:" + group.Key, emitted, attempted));

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

        // AUTOSAR CanNm 的 PNI 位于 CBV bit4；只在用户明确配置 CBV 字节时解释。
        // 位角色是硬编码的（M1 的 NmLayout 没有位定义），所以结论按推断输出。
        var hit = ordered.FirstOrDefault(f => f.Frame.Data.Length > cbvByte &&
                                              (f.Frame.Data[cbvByte] & 0x10) != 0);
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
