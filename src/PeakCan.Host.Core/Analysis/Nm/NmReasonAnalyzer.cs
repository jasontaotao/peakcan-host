using System.Globalization;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 把字典声明的字段逐帧解码成“值区间”。纯函数，无 IO。
/// 第一版只做整值（Raw/Enum）；Bitmask 由调用方降级前先记录。
/// </summary>
public static class NmReasonAnalyzer
{
    // 与现有 MaxEventsPerMessage 同口径：每字段最多 200 段 / 200 条变化。
    private const int MaxItemsPerField = 200;

    public static NmReasonAnalysis Analyze(
        NmReasonDictionary dictionary,
        DbcDocument dbc,
        IReadOnlyList<NmFrame> frames,
        IReadOnlyList<NmEpisode> episodes,
        NmWindow window)
    {
        var timelines = new List<NmReasonTimeline>();
        var truncations = new List<NmTruncation>();
        // 跳过集合用于抑制 NmSignalSummary 的同名 signal_change；ID 大小写不应影响命中。
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in dictionary.Fields)
        {
            var unknowns = new List<string>();
            var (timeline, resolved) = AnalyzeField(
                field, dbc, frames, episodes, window, unknowns, truncations);
            timelines.Add(timeline);
            // 只有信号真正解析成功才抑制同名 signal_change：字典写错名字时
            // 不能再把原先可见的信号变化事件一起吞掉。
            if (resolved)
            {
                covered.Add(NmSignalSummary.SkipKey(
                    field.CanIdHex, field.IsExtended, field.Signal));
            }
        }

        return new NmReasonAnalysis(timelines, truncations, covered);
    }

    private static (NmReasonTimeline Timeline, bool Resolved) AnalyzeField(
        NmReasonFieldDef field,
        DbcDocument dbc,
        IReadOnlyList<NmFrame> frames,
        IReadOnlyList<NmEpisode> episodes,
        NmWindow window,
        List<string> unknowns,
        List<NmTruncation> truncations)
    {
        var effective = field.Kind == NmReasonKind.Bitmask ? NmReasonKind.Raw : field.Kind;
        if (field.Kind == NmReasonKind.Bitmask)
            unknowns.Add($"{field.Signal}：第一版不支持位掩码，已按原始整值显示");

        var message = FindMessage(dbc, field.CanIdHex, field.IsExtended);
        var signal = message?.Signals.FirstOrDefault(
            s => string.Equals(s.Name, field.Signal, StringComparison.Ordinal));
        if (signal is null)
        {
            unknowns.Add($"{field.Signal}：DBC 中未找到该信号（{field.CanIdHex}）");
            return (Empty(field, effective, unknowns), false);
        }

        // 值表在字段级解析一次：同名信号跨报文歧义与越界条目都在这里明确降级。
        var dbcTable = ResolveDbcTable(dbc, field.Signal, signal, unknowns);
        ValidateEntryRange(field, signal, unknowns);

        var fieldFrames = frames
            .Where(f => string.Equals(f.Def.CanIdHex, field.CanIdHex, StringComparison.OrdinalIgnoreCase)
                        && f.Def.IsExtended == field.IsExtended
                        && f.Frame.Timestamp >= window.Start
                        && f.Frame.Timestamp <= window.End)
            .OrderBy(f => f.Frame.Timestamp)
            .ToArray();

        var segments = new List<NmReasonSegment>();
        var transitions = new List<NmReasonTransition>();
        var unreadable = new List<double>();
        var labelsCache = new Dictionary<ulong, IReadOnlyList<NmReasonLabel>>();

        // 超过上限不再分配对象，但仍计数：Truncation.Total 必须反映实际生成数。
        var segmentAttempted = 0;
        var transitionAttempted = 0;

        SegmentBuilder? current = null;

        void Close(double end, NmReasonSegmentEnd reason, double? next)
        {
            if (current is null) return;
            segmentAttempted++;
            if (segments.Count < MaxItemsPerField)
            {
                segments.Add(new NmReasonSegment(
                    current.Start, end, current.Raw, LabelsOf(current.Raw, labelsCache),
                    current.StartUnknown, reason == NmReasonSegmentEnd.WindowEnd,
                    reason, next, current.FirstBytes));
            }
            current = null;
        }

        var lastEpisode = -1;
        var pendingUnreadable = false;
        // 帧已按时间排序：episode 指针单调前进，避免每帧 O(episodes) 全表扫描。
        var episodePosition = 0;

        foreach (var frame in fieldFrames)
        {
            var data = frame.Frame.Data;
            var t = frame.Frame.Timestamp;
            if (!Fits(frame, signal))
            {
                if (current is not null) pendingUnreadable = true;
                unreadable.Add(t);
                continue;
            }

            var raw = SignalDecoder.DecodeRaw(data, signal);
            var episode = EpisodeIndexAt(episodes, ref episodePosition, t);

            if (current is not null && raw != current.Raw)
            {
                transitionAttempted++;
                if (transitions.Count < MaxItemsPerField)
                {
                    transitions.Add(new NmReasonTransition(
                        t, field.Signal, field.CanIdHex, field.IsExtended,
                        current.Raw, raw, LabelsOf(current.Raw, labelsCache),
                        LabelsOf(raw, labelsCache)));
                }
            }

            if (current is not null)
            {
                // 优先级：中断 > episode 切换 > 同 episode 值变化。
                // 跨 episode 且值变化时，段尾仍是 EpisodeEnd（这不是同一 episode 内的切换），
                // 值变化由上面的 transition 单独记录。
                if (episode != lastEpisode)
                    Close(current.LastConfirmedT,
                        pendingUnreadable ? NmReasonSegmentEnd.Unreadable : NmReasonSegmentEnd.EpisodeEnd, t);
                else if (pendingUnreadable)
                    Close(current.LastConfirmedT, NmReasonSegmentEnd.Unreadable, t);
                else if (raw != current.Raw)
                    Close(current.LastConfirmedT, NmReasonSegmentEnd.Changed, t);
            }

            if (current is null)
            {
                // 只有字段首段是“窗口前状态未知”；后续段起点是观测到的
                // 变化/恢复帧（其起点为何仍是下界由前一段的 EndReason 表达）。
                current = new SegmentBuilder(
                    t, raw, startUnknown: segments.Count == 0, data.ToArray());
            }

            current.LastConfirmedT = t;
            lastEpisode = episode;
            pendingUnreadable = false;
        }

        if (current is not null)
            Close(current.LastConfirmedT, NmReasonSegmentEnd.WindowEnd, null);

        if (segmentAttempted > segments.Count)
            truncations.Add(new NmTruncation(
                $"reason:segments:{field.CanIdHex}:{field.Signal}",
                segments.Count, segmentAttempted));

        if (transitionAttempted > transitions.Count)
            truncations.Add(new NmTruncation(
                $"reason:transitions:{field.CanIdHex}:{field.Signal}",
                transitions.Count, transitionAttempted));

        return (new NmReasonTimeline(
            field.Signal, field.CanIdHex, field.IsExtended,
            field.Kind, effective, segments, transitions, unreadable, unknowns), true);

        IReadOnlyList<NmReasonLabel> LabelsOf(
            ulong raw, Dictionary<ulong, IReadOnlyList<NmReasonLabel>> cache)
        {
            if (cache.TryGetValue(raw, out var cached)) return cached;
            var labels = BuildLabels(field, dbcTable, raw);
            cache[raw] = labels;
            return labels;
        }
    }

    private static IReadOnlyList<NmReasonLabel> BuildLabels(
        NmReasonFieldDef field, ValueTable? dbcTable, ulong raw)
    {
        if (raw > long.MaxValue) return Array.Empty<NmReasonLabel>();
        var rawLong = (long)raw;
        var labels = new List<NmReasonLabel>(2);

        if (dbcTable is not null
            && dbcTable.Entries.TryGetValue(rawLong, out var dbcText))
        {
            labels.Add(new NmReasonLabel(dbcText, "dbc"));
        }

        if (field.Entries is { } entries
            && entries.TryGetValue(rawLong, out var dictText)
            && labels.All(l => !string.Equals(l.Text, dictText, StringComparison.Ordinal)))
        {
            labels.Add(new NmReasonLabel(dictText, "dictionary"));
        }

        return labels;
    }

    /// <summary>
    /// 解析该字段可用的 DBC 值表。hil-core 把内联 <c>VAL_</c> 的表名设为信号名
    /// （ValueTableFlow.cs:159），解析末尾再并入 <c>ValueTables</c>，因此同名内联表
    /// 会覆盖同名的命名 <c>VAL_TABLE_</c>（ParseDocumentFlow）。以下两种情况都无法
    /// 从 <see cref="DbcDocument"/> 还原当前字段该用的表内容，必须明确降级：
    /// 自家内联表被其它报文同名信号覆盖；引用的命名表被同名内联表覆盖。
    /// </summary>
    private static ValueTable? ResolveDbcTable(
        DbcDocument dbc, string fieldSignal, Signal signal, List<string> unknowns)
    {
        if (signal.ValueTableName is not { Length: > 0 } tableName) return null;

        var inlineOwners = dbc.MessagesById.Values.Count(m =>
            m.Signals.Any(s =>
                string.Equals(s.Name, tableName, StringComparison.Ordinal)
                && string.Equals(s.ValueTableName, tableName, StringComparison.Ordinal)));
        // 自家内联表只有一处声明时可信；引用命名表时，任何同名内联表都是覆盖源。
        var ambiguous = string.Equals(tableName, fieldSignal, StringComparison.Ordinal)
            ? inlineOwners > 1
            : inlineOwners > 0;
        if (ambiguous)
        {
            unknowns.Add(
                $"{fieldSignal}：值表「{tableName}」与其它报文的内联值表同名，无法按报文区分，DBC 解释已忽略");
            return null;
        }

        return dbc.ValueTables.TryGetValue(tableName, out var table) ? table : null;
    }

    /// <summary>
    /// 明确忽略越界/负数字典条目：原始值是无符号位模式，越界键永远不可能命中。
    /// 记一条 Unknown，避免“配了却没生效”静默消失。
    /// </summary>
    private static void ValidateEntryRange(
        NmReasonFieldDef field, Signal signal, List<string> unknowns)
    {
        if (field.Entries is not { Count: > 0 } entries) return;
        var max = signal.Length >= 64 ? ulong.MaxValue : (1UL << signal.Length) - 1UL;
        var invalid = entries.Keys
            .Where(k => k < 0 || (ulong)k > max)
            .OrderBy(k => k)
            .ToArray();
        if (invalid.Length > 0)
        {
            unknowns.Add(
                $"{field.Signal}：字典条目键 {string.Join(",", invalid)} 超出信号位宽 {signal.Length}，已忽略");
        }
    }

    private static Message? FindMessage(DbcDocument dbc, string canIdHex, bool isExtended)
    {
        if (!canIdHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return null;
        if (!uint.TryParse(canIdHex.AsSpan(2), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var id)) return null;
        if (!dbc.MessagesById.TryGetValue(id, out var message)) return null;
        if (((message.Id & 0x80000000u) != 0) != isExtended) return null;
        return message;
    }

    /// <summary>
    /// 载荷是否覆盖信号的全部位。hil-core 的两种字节序解码器都按
    /// <c>[StartBit, StartBit + Length)</c> 取位，缺字节时按 0 补齐（见
    /// SignalDecoder.ReadLittleEndian/ReadBigEndian），因此只要跨度越界就必须
    /// 判不可读，不能让“部分 0 位”变成一个看起来有效的值。
    /// <para>
    /// 有效载荷长度必须分帧型：BLF 经典帧固定切 8 字节（CanMessageFlow 的
    /// <c>frameData.Slice(8, 8)</c>），真实长度是 <see cref="ReplayFrame.Dlc"/>；
    /// 只看 <c>Data.Length</c> 会把填充 0 当真实值。FD 帧的 <c>Data.Length</c>
    /// 已是真实帧长（CanFdMessageFlow 按 frame_length 切），不能用 DLC code 截。
    /// 同时守住 hil-core <see cref="SignalDecoder"/> 的 64 位上限，超长信号
    /// 降级为不可读而不是抛出中断整份分析。
    /// </para>
    /// </summary>
    private static bool Fits(NmFrame frame, Signal signal)
    {
        if (signal.Length is 0 or > 64) return false;
        var data = frame.Frame.Data;
        var bytes = (frame.Frame.Flags & FrameFlags.Fd) != 0
            ? data.Length
            : Math.Min(frame.Frame.Dlc, data.Length);
        return signal.StartBit + signal.Length <= bytes * 8;
    }

    /// <summary>
    /// 单调 episode 指针：帧按时间升序，位置只前进不回退。
    /// 语义与 <see cref="NmEpisodeBuilder.IndexAt"/> 一致：时间落在 episode 内取该
    /// episode；落在空档取前一个 episode；早于首个 episode 时取第一个（0）。
    /// </summary>
    private static int EpisodeIndexAt(
        IReadOnlyList<NmEpisode> episodes, ref int position, double t)
    {
        if (episodes.Count == 0) return 0;
        while (position + 1 < episodes.Count && episodes[position + 1].Start <= t)
            position++;
        return episodes[position].Index;
    }

    private static NmReasonTimeline Empty(
        NmReasonFieldDef field, NmReasonKind effective, List<string> unknowns)
        => new(field.Signal, field.CanIdHex, field.IsExtended,
            field.Kind, effective, Array.Empty<NmReasonSegment>(),
            Array.Empty<NmReasonTransition>(), Array.Empty<double>(), unknowns);

    private sealed class SegmentBuilder(
        double start, ulong raw, bool startUnknown, byte[] firstBytes)
    {
        public double Start { get; } = start;
        public ulong Raw { get; } = raw;
        public bool StartUnknown { get; } = startUnknown;
        public byte[] FirstBytes { get; } = firstBytes;
        public double LastConfirmedT { get; set; } = start;
    }
}
