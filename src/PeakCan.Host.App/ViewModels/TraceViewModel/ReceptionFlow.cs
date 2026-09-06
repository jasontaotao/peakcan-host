using System.Collections.Generic;
using System.Windows;
using PeakCan.HIL.Core;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewModel
{
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
        return dispatcher.InvokeAsync(() => AppendBatchCore(batch)).Task;
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
        foreach (var f in batch)
        {
            // Track per-message-ID counts (before any display filtering).
            TotalFrameCount++;
            _messageCounts[f.Id.Raw] = _messageCounts.GetValueOrDefault(f.Id.Raw) + 1;

            // v0.9.2: pause still tracks counts but skips display.
            if (IsPaused) continue;

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
            };
            // 新帧入列即按当前高亮规则求色（无规则 → -1）。
            entry.HighlightColorIndex = EvaluateHighlight(entry);
            Entries.Add(entry);
            // v1.2.11: register the just-appended entry so DbcDecodeBackgroundService
            // can fill Decoded when it looks up the same CanFrame in DBC.
            var pendingKey = new TraceEntryKey(
                f.Id.Raw,
                f.Timestamp.TotalMicroseconds,
                f.Channel.Handle);
            _pendingDecode.GetOrAdd(pendingKey, _ => new System.Collections.Concurrent.ConcurrentQueue<TraceEntry>()).Enqueue(Entries[^1]);
        }
        while (Entries.Count > MaxRows)
        {
            var removed = Entries[0];
            var removedKey = new TraceEntryKey(
                removed.Id.Raw,
                removed.Timestamp.TotalMicroseconds,
                removed.Channel.Handle);
            // FIFO 出列时同步清 pending map，避免长跑内存泄漏。
            // 与 worker 的 TryCompletePending（同 _pendingPurgeGate 锁）互斥，
            // 且仅当队头仍指向被移除行时才出列，防止同 key 后续帧被误删。
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
            Entries.RemoveAt(0);
        }

        // 批次末：统计面板若展开则刷；状态文本重算。
        if (StatsExpanded) RefreshStats();
        UpdateStatusText();
    }
}
