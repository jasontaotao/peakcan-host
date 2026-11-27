using System.Collections.Generic;
using System.Windows;
using PeakCan.HIL.Core;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewModel
{
    /// <summary>
    /// 一次批次追加完成（UI 线程触发）。视图用它做"每批一次"的自动滚动，
    /// 而不是订阅 <see cref="Entries"/> 的每次 <c>CollectionChanged</c>——
    /// 一批 1600 帧就是 1600 次 ScrollIntoView，会把 UI 线程压垮。
    /// <para>
    /// 订阅者不得抛异常：本事件在数据摄入核心的同步路径里触发，异常会沿
    /// <c>AppendBatchAsync</c> 的 Task 冒泡、终止 <c>TraceService</c> 的接收循环。
    /// </para>
    /// </summary>
    public event Action? BatchAppended;

    /// <summary>
    /// v1.2.11: test-only helper to inject a pending entry directly,
    /// bypassing <see cref="AppendBatchAsync"/>'s dispatcher hop. Used by
    /// <c>DbcDecodeBackgroundServiceTests</c> which run on the xunit MTA
    /// threadpool with no WPF Application.
    /// </summary>
    internal void RegisterForTesting(TraceEntryKey key, TraceEntry entry)
        => _pendingDecode.GetOrAdd(key, _ => new System.Collections.Concurrent.ConcurrentQueue<TraceEntry>()).Enqueue(entry);

    /// <summary>
    /// v1.2.11 PATCH review fix: atomic check-and-remove. The worker calls
    /// this after successfully filling <see cref="TraceEntry.Decoded"/> so
    /// the entry stops occupying the pending map. Returning false means
    /// another worker (or a Clear()) already removed it; the caller should
    /// not double-write Decoded in that case.
    /// <para>
    /// 2026-09-05 P1-3：value 为 FIFO 队列，同 key 多帧逐个出列；
    /// 队列空时移除 key 防长跑内存泄漏。
    /// </para>
    /// </summary>
    internal bool TryCompletePending(TraceEntryKey key, out TraceEntry? entry)
    {
        entry = null;
        if (!_pendingDecode.TryGetValue(key, out var queue))
            return false;
        lock (_pendingPurgeGate)
        {
            // 与 FIFO trim purge（同锁）互斥：出列 + 移除原子化，
            // 防止 purge 在间隙内误删后续 live 条目。
            if (!queue.TryDequeue(out var dequeued))
                return false;
            entry = dequeued;
            // 队列空则移除 key。TryRemove 与并发注册之间的竞态兜底：
            // 移除后队列又非空（新条目刚入列）→ 把剩余条目并回当前队列。
            if (queue.IsEmpty
                && _pendingDecode.TryRemove(new KeyValuePair<TraceEntryKey, System.Collections.Concurrent.ConcurrentQueue<TraceEntry>>(key, queue))
                && !queue.IsEmpty)
            {
                var current = _pendingDecode.GetOrAdd(key, _ => queue);
                if (!ReferenceEquals(current, queue))
                {
                    // 并发注册已建新队列：把竞态窗口入列的条目并回，保序。
                    while (queue.TryDequeue(out var late))
                        current.Enqueue(late);
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Append a batch of frames to <see cref="Entries"/>, then trim to
    /// <see cref="MaxRows"/>. Marshals to the WPF UI thread via
    /// <c>Application.Current.Dispatcher</c> then delegates to the sync core
    /// <see cref="AppendBatchCore"/>.
    /// </summary>
    public Task AppendBatchAsync(IReadOnlyList<CanFrame> batch)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return Task.CompletedTask;

        // 2026-11-27 P0：用 Background(4) 而不是默认 Normal(9) 投递。
        // WPF 优先级 Normal(9) > Render(7) > Input(5) > Background(4)：
        // 数据摄入若以 Normal 排队，会持续压过输入与绘制——用户点击进不来、
        // 画面不刷新，表现就是"界面卡住但 CPU 不低"。降到 Background 后，
        // 输入和渲染始终先得到处理；接收本身有 10k 有界通道兜底，晚一批无妨。
        return dispatcher.InvokeAsync(() => AppendBatchCore(batch),
            System.Windows.Threading.DispatcherPriority.Background).Task;
    }

    /// <summary>
    /// 2026-08-31 P1: 同步核心（UI 线程契约，MTA 可直驱测试）。每帧流程：计数
    /// → <see cref="IsPaused"/> 跳过 → 建 <see cref="TraceEntry"/>（含
    /// <c>Data</c> 拷贝 + <see cref="TraceEntry.HighlightColorIndex"/> 高亮求值）
    /// → Add → <c>_pendingDecode</c> 注册 → trim。**非破坏性**：除暂停外全部
    /// 入列（视图层过滤负责隐藏，改过滤可找回已入列帧）。
    /// <para>core 末尾：统计展开时刷 <see cref="RefreshStats"/> + 状态文本更新。</para>
    /// </summary>
    internal void AppendBatchCore(IReadOnlyList<CanFrame> batch)
    {
        // 2026-11-27 P0：先在普通列表里组装整批，最后**一次性**应用到 Entries。
        //
        // 旧实现每帧 Entries.Add + 淘汰时逐条 RemoveAt(0)，500 fps 下每批约 200 次
        // CollectionChanged。DataGrid 每次都在 UI 线程同步做视图维护，而这些通知以
        // DispatcherPriority.Normal 投递，优先级高于 Input(5)/Render(7)，于是输入与
        // 绘制被长期饿死——"界面点不动、画面不刷新，但 CPU 单核繁忙"。
        // 现在整批只发一条 Reset，通知次数与帧率解耦。
        var appended = new List<TraceEntry>(batch.Count);
        var pendingKeys = new List<TraceEntryKey>(batch.Count);

        foreach (var f in batch)
        {
            // Track per-message-ID counts (before any display filtering).
            TotalFrameCount++;
            _messageCounts[f.Id.Raw] = _messageCounts.GetValueOrDefault(f.Id.Raw) + 1;

            // v0.9.2: pause still tracks counts but skips display.
            if (IsPaused)
            {
                // final review I1：暂停只是不显示，SecOC seq 对齐不能停——通道
                // _rxSequence 对所有受保护帧递增（SDK 读线程），joiner 计数只在
                // Join 时推进；若暂停期间不推进，恢复后徽章会取到 N 帧前的
                // verdict（张冠李戴）。join 结果丢弃，仅消耗计数。
                SecOcBadgeResolver?.Invoke(f);
                continue;
            }

            var data = f.Data.ToArray();
            var entry = new TraceEntry
            {
                Timestamp = f.Timestamp,
                Channel = f.Channel,
                Id = f.Id,
                Dlc = f.Dlc,
                // Insert a single space between every 2-char hex byte so
                // "DEADBEEF" reads as "DE AD BE EF".
                DataHex = FormatHexWithSpaces(f.Data.Span),
                // 原始载荷拷贝：payload 过滤与高亮重算都需要。
                Data = data,
                IsError = f.IsError,
                IsSim = f.FrameSource == FrameSource.Environment,
                IsFd = f.IsFd,
                IsRtr = (f.Flags & FrameFlags.Rtr) != 0,
                // M2.4b（spec §5-D6.7）：徽标三态——resolver join 命中 →
                // ✓/✗+reason；无 resolver（离线/未接线）→ "离线不验"。
                SecOcBadge = SecOcBadgeResolver?.Invoke(f) ?? SecOcBadge.Offline,
            };
            // 新帧入列即按当前高亮规则求色（无规则 → -1）。
            entry.HighlightColorIndex = EvaluateHighlight(entry);
            appended.Add(entry);
            pendingKeys.Add(new TraceEntryKey(
                f.Id.Raw,
                f.Timestamp.TotalMicroseconds,
                f.Channel.Handle));
        }

        // 淘汰数：入列后总数超出上限的部分，从**合并序列的头部**移除。
        // 先消耗既有行；不足的部分再裁掉本批新追加的头部（单批就超限的场景，
        // 例如 MaxRows=2 却一次来了 3 帧）。
        var projectedCount = Entries.Count + appended.Count;
        var removeCount = projectedCount > MaxRows ? projectedCount - MaxRows : 0;
        var fromExisting = Math.Min(removeCount, Entries.Count);
        var fromAppended = removeCount - fromExisting;

        // 淘汰既有行的 pending 清理必须在移除前做（需要读到即将离开的行对象）。
        for (var i = 0; i < fromExisting; i++)
            PurgePendingFor(Entries[i]);

        // 本批内被立即裁掉的头部帧：从未进入 Entries，直接丢弃即可。
        // pendingKeys 与 appended 必须同步裁剪，否则后面的注册会错位。
        if (fromAppended > 0)
        {
            appended.RemoveRange(0, fromAppended);
            pendingKeys.RemoveRange(0, fromAppended);
        }

        // 一次性应用：头部移除 + 尾部追加，只发一条 Reset。
        Entries.ApplyBatch(fromExisting, appended);

        // v1.2.11: register the just-appended entries so DbcDecodeBackgroundService
        // can fill Decoded when it looks up the same CanFrame in DBC.
        for (var i = 0; i < appended.Count; i++)
            _pendingDecode.GetOrAdd(pendingKeys[i], _ => new System.Collections.Concurrent.ConcurrentQueue<TraceEntry>())
                .Enqueue(appended[i]);

        // 批次末：统计面板若展开则刷（限流到 2 Hz，见 StatsFlow）；状态文本重算。
        if (StatsExpanded) RefreshStatsThrottled();
        UpdateStatusText();

        // 视图的自动滚动入口：每批一次，而不是每行一次。
        BatchAppended?.Invoke();
    }

    /// <summary>
    /// 从 <see cref="_pendingDecode"/> 里清掉一条即将被 FIFO 淘汰的行。
    /// 与 worker 的 <see cref="TryCompletePending"/>（同 <c>_pendingPurgeGate</c> 锁）
    /// 互斥，且仅当队头仍指向该行时才出列，防止同 key 后续帧被误删。
    /// </summary>
    private void PurgePendingFor(TraceEntry removed)
    {
        var removedKey = new TraceEntryKey(
            removed.Id.Raw,
            removed.Timestamp.TotalMicroseconds,
            removed.Channel.Handle);

        if (_pendingDecode.TryGetValue(removedKey, out var pendingQueue))
        {
            lock (_pendingPurgeGate)
            {
                if (pendingQueue.TryPeek(out var pending) && ReferenceEquals(pending, removed))
                {
                    pendingQueue.TryDequeue(out _);
                    if (pendingQueue.IsEmpty)
                        _pendingDecode.TryRemove(
                            new KeyValuePair<TraceEntryKey, System.Collections.Concurrent.ConcurrentQueue<TraceEntry>>(removedKey, pendingQueue));
                }
            }
        }

        // 标记已淘汰：解码 worker 看到后不再为这一行排队回填。
        removed.MarkEvicted();
    }
}
