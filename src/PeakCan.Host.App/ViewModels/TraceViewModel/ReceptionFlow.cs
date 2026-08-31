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
        => _pendingDecode[key] = entry;

    /// <summary>
    /// v1.2.11 PATCH review fix: atomic check-and-remove. The worker calls
    /// this after successfully filling <see cref="TraceEntry.Decoded"/> so
    /// the entry stops occupying the pending map. Returning false means
    /// another worker (or a Clear()) already removed it; the caller should
    /// not double-write Decoded in that case.
    /// </summary>
    internal bool TryCompletePending(TraceEntryKey key, out TraceEntry? entry)
        => _pendingDecode.TryRemove(key, out entry);

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
            _pendingDecode[pendingKey] = Entries[^1];
        }
        while (Entries.Count > MaxRows) Entries.RemoveAt(0);

        // 批次末：统计面板若展开则刷；状态文本重算。
        if (StatsExpanded) RefreshStats();
        UpdateStatusText();
    }
}
