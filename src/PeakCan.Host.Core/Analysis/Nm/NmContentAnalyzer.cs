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
            var produced = 0;

            void Emit(NmEvent e)
            {
                if (produced >= MaxEventsPerMessage) return;
                events.Add(e);
                produced++;
            }

            DetectBurst(ordered, input.CycleMs, Emit);
            DetectByteAndBitChanges(ordered, group.Key, Emit);

            if (produced >= MaxEventsPerMessage)
                truncations.Add(new NmTruncation("events:" + group.Key, produced, ordered.Length));

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

    private static void DetectByteAndBitChanges(NmFrame[] ordered, string canId, Action<NmEvent> emit)
    {
        if (ordered.Length < 2) return;
        var previous = ordered[0].Frame.Data;
        for (int i = 1; i < ordered.Length; i++)
        {
            var current = ordered[i].Frame.Data;
            for (int b = 0; b < Math.Min(previous.Length, current.Length); b++)
            {
                if (previous[b] == current[b]) continue;
                var xor = previous[b] ^ current[b];
                emit(new NmEvent(ordered[i].Frame.Timestamp, "byte_change", null, canId,
                    $"byte{b}: 0x{previous[b]:X2} → 0x{current[b]:X2}", 0, "measured"));
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((xor & (1 << bit)) == 0) continue;
                    // 内插里的三元表达式必须整体加括号，否则 ":" 会被当成格式说明符。
                    var wasSet = (previous[b] & (1 << bit)) != 0;
                    var isSet = (current[b] & (1 << bit)) != 0;
                    emit(new NmEvent(ordered[i].Frame.Timestamp, "bit_change", null, canId,
                        $"byte{b}.bit{bit}: {(wasSet ? 1 : 0)} → {(isSet ? 1 : 0)}",
                        0, "measured"));
                }
            }
            previous = current;
        }
    }

    private static void CheckNid(NmFrame[] ordered, NmContentInput input, List<NmFinding> findings)
    {
        if (input.Profile.Layout?.NidByte is not { } nidByte) return;

        var frames = ordered.Where(f => f.Frame.Data.Length > nidByte).ToArray();
        if (frames.Length == 0) return;

        var expected = input.Profile.Nodes
            .FirstOrDefault(n => n.Name == ordered[0].Def.Sender)?.StationAddress;
        var values = frames.Select(f => f.Frame.Data[nidByte]).Distinct().ToArray();

        if (values.Length > 1)
        {
            findings.Add(new NmFinding("NM_NID_CHANGED", "warn",
                $"同一发送者的 NID 在窗口内变化：{string.Join(",", values.Select(v => $"0x{v:X2}"))}",
                frames.Select(f => f.Frame.Timestamp).Take(5).ToArray(), true,
                "检查发送节点配置/复位"));
        }

        if (expected is { } sa && values.Length == 1 && values[0] != sa)
        {
            findings.Add(new NmFinding("NM_NID_MISMATCH", "warn",
                $"NID 0x{values[0]:X2} ≠ 站地址 0x{sa:X2}",
                new[] { frames[0].Frame.Timestamp }, false,
                "核对 DBC 站地址与发送内容"));
        }

        if (values.Length == 1 && input.Profile.Nodes.Count > 0 &&
            !input.Profile.Nodes.Any(n => n.StationAddress == values[0]))
        {
            findings.Add(new NmFinding("NM_UNKNOWN_NID", "info",
                $"NID 0x{values[0]:X2} 不在 DBC 节点表中",
                new[] { frames[0].Frame.Timestamp }, true,
                "确认矩阵是否遗漏该节点"));
        }
    }

    private static void CheckCbv(NmFrame[] ordered, NmContentInput input, List<NmFinding> findings)
    {
        if (input.Profile.Layout?.CbvByte is not { } cbvByte) return;
        if (input.Profile.Features?.PnEnabled != false) return;

        // AUTOSAR CanNm 的 PNI 位于 CBV bit4；只在用户明确配置 CBV 字节时解释。
        var hit = ordered.FirstOrDefault(f => f.Frame.Data.Length > cbvByte &&
                                              (f.Frame.Data[cbvByte] & 0x10) != 0);
        if (hit is not null)
        {
            findings.Add(new NmFinding("NM_PNI_WHILE_PN_DISABLED", "warn",
                "PN 关闭但 CBV PNI 位置 1", new[] { hit.Frame.Timestamp }, false,
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
