using System.Globalization;
using Microsoft.Extensions.Hosting;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.App.Services;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class DbcViewModel
{
    // Flow: live byte-layout lighting (phase 2).
    //
    // Data path: DbcDecodeBackgroundService publishes the latest raw payload of the
    // selected message into LiveFrameSlot (single slot, copy-on-publish). This VM
    // re-decodes on a 33 ms System.Threading.Timer tick, honoring the current mux
    // branch, and applies per-cell bit state + per-signal values on the UI thread
    // via RunOnUiPost. No selection → the timer is stopped: the bus idle path does
    // no per-frame work.
    //
    // Cross-flow callers (plain calls via partial-class visibility):
    //   - UpdateLayout (LayoutFlow) -> SyncLive
    //   - SyncLive -> RefreshLiveLayout / _liveSlot.SetTarget
    //   - OnLiveTick -> RefreshLiveLayout
    //   - Dispose (host shutdown) -> _liveTimer.Dispose

    private const int LiveRefreshIntervalMs = 33;   // ~30 Hz, same cadence as SignalViewModel

    private readonly LiveFrameSlot? _liveSlot;
    private readonly System.Threading.Timer? _liveTimer;
    private readonly object _liveGate = new();
    private long _lastLiveSequence = -1;
    private bool _liveDisposed;

    /// <summary>测试接缝：同步跑一次实时刷新（xunit 没有 WPF 调度器，不能等 Timer）。</summary>
    internal void RefreshLiveLayoutForTest() => RefreshLiveLayout(force: true);

    /// <summary>测试接缝：停掉自动 tick，避免测试线程与线程池回调并发改行集合。</summary>
    internal void StopLiveTimerForTest() => _liveTimer?.Change(Timeout.Infinite, Timeout.Infinite);

    /// <summary>组合根测试用：确认 DI 把同一个单槽注入了 VM。</summary>
    internal LiveFrameSlot? LiveSlot => _liveSlot;

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    Task IHostedService.StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
        _liveDisposed = true;
        _liveTimer?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 选中消息变化后同步实时通道：选中有帧消息 → 记目标 ID、立刷一次、启动 33ms tick；
    /// 无选中 → 停 tick、清目标。切换消息不得沿用上一条消息的载荷。
    /// </summary>
    private void SyncLive()
    {
        if (_liveSlot is null || _liveDisposed) return;

        if (SelectedMessage?.RawMessage is not { } message)
        {
            _liveSlot.SetTarget(null);
            _liveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            lock (_liveGate) { _lastLiveSequence = -1; }
            return;
        }

        _liveSlot.SetTarget(message.Id);
        lock (_liveGate) { _lastLiveSequence = -1; }
        RefreshLiveLayout(force: true);
        _liveTimer?.Change(LiveRefreshIntervalMs, LiveRefreshIntervalMs);
    }

    private void OnLiveTick() => ((Action)(() => RefreshLiveLayout(force: false))).RunOnUiPost();

    /// <summary>
    /// 把单槽里的最新载荷投影到当前布局：格子位状态 + 信号表当前值。
    /// <paramref name="force"/> = true 时忽略序号（选中 / mux 切换后必须立刻重画）。
    /// 在 UI 线程执行（选中路径天然在 UI 线程，tick 路径经 RunOnUiPost）。
    /// </summary>
    private void RefreshLiveLayout(bool force)
    {
        // 测试线程可能同时从线程池 tick；锁保证「最后应用的一定是最新载荷」。
        lock (_liveGate)
        {
            if (SelectedMessage?.RawMessage is not { } message) return;
            if (_liveSlot is null || !_liveSlot.TryRead(message.Id, out var frame)) return;
            if (!force && frame.Sequence == _lastLiveSequence) return;
            _lastLiveSequence = frame.Sequence;

            // 只信任声明 DLC 内的字节：总线帧可能比 BO_ 声明更长，多出的字节不能
            // 让“全部超出 DLC”的信号突然有值（与位图/诊断口径一致）。
            var renderedDlc = LayoutSnapshot?.RenderedDlc ?? frame.Data.Length;
            var viewLength = Math.Min(frame.Data.Length, renderedDlc);
            var payload = frame.Data.Span[..viewLength];
            var frameMux = DecodeFrameMux(message, payload);

            foreach (var row in LayoutRows)
            {
                foreach (var cell in row.Cells)
                {
                    if (cell.OwnerSignal is null)
                    {
                        cell.IsSet = null;
                        continue;
                    }

                    cell.IsSet = row.ByteIndex < payload.Length
                        ? ((payload[row.ByteIndex] >> cell.BitNumber) & 1) == 1
                        : null;
                }
            }

            foreach (var row in VisibleSignals)
                row.LiveValue = FormatLiveValue(row.Signal, payload, frameMux);
        }
    }

    /// <summary>帧里实际的 mux 值；消息无复用器 / 当前载荷解不出时为 null。</summary>
    private static ushort? DecodeFrameMux(Message message, ReadOnlySpan<byte> payload)
    {
        if (message.MultiplexorSignalIndex is not { } index
            || index < 0 || index >= message.Signals.Count)
        {
            return null;
        }

        var muxSignal = message.Signals[index];
        return CanDecode(muxSignal, payload)
            ? (ushort)SignalDecoder.DecodeRaw(payload, muxSignal)
            : null;
    }

    /// <summary>
    /// 单信号当前值。载荷不够覆盖信号全部位时返回“—”（不能像 Decoder 那样把缺字节
    /// 当 0 解码——那会把“没收到数据”显示成 0）；muxed 信号且帧 mux 与选中分支不符
    /// 时也返回“—”。
    /// </summary>
    private static string FormatLiveValue(Signal signal, ReadOnlySpan<byte> payload, ushort? frameMux)
    {
        if (!CanDecode(signal, payload)) return "—";

        if (signal.IsMultiplexed && signal.MultiplexValue is { } expected
            && (frameMux is null || frameMux.Value != expected))
        {
            return "—";
        }

        var physical = SignalDecoder.Decode(payload, signal);
        return physical.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>信号的全部位是否落在当前帧载荷内（Decoder 对缺失字节静默补 0，这里必须先挡）。</summary>
    private static bool CanDecode(Signal signal, ReadOnlySpan<byte> payload)
    {
        if (signal.Length == 0 || signal.Length > 64) return false;
        var lastBit = signal.StartBit + signal.Length - 1;
        return lastBit >> 3 < payload.Length;
    }
}
