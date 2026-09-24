using PeakCan.Host.Core.Xcp.Scheduling;

namespace PeakCan.Host.Core.Xcp.Receive;

/// <summary>
/// 计划内换表空窗通知类型（spec §3 Receive 写死条款：Receive 层新增，携带预期时长上界；
/// 重配细分归 host，不改包枚举）。
/// <para>
/// 与 T11 <see cref="RotationGapWindow"/> 的衔接：RotationScheduler 在 stop 应答落地、
/// 首条重写前经 <see cref="IXcpPlanGapNotifier"/> 推送 Scheduling 侧形状；Receive 层
/// 状态机（<see cref="PlanGapWatcher"/>）转成本类型开窗 —— Receive 侧只消费 T11 的
/// 通知形状与 ExpectedMaxDuration 上界，不感知 Scheduling 内部时序。
/// </para>
/// </summary>
/// <param name="OdtCount">换表涉及的 ODT 数（原样透传 T11）。</param>
/// <param name="EntryCount">换表涉及的条目数（原样透传 T11）。</param>
/// <param name="ExpectedMaxDuration">空窗预期时长上界（T11 按命令数 × T1 最坏 + quiesce 预算算出）；超时未恢复即升级断流。</param>
public sealed record PlanGapWindow(ushort OdtCount, int EntryCount, TimeSpan ExpectedMaxDuration);

/// <summary>
/// 计划空窗/断流状态机：OnPlanGapWindow 开窗 → 期内收到 DTO 样本关窗（恢复，不产归因）
/// → 超过 <see cref="PlanGapWindow.ExpectedMaxDuration"/> 未恢复升级断流，
/// 生产 <see cref="A2lEditor.Core.Layout.MissingCause.AcquisitionInterrupted"/>
/// （包枚举槽位首次有生产者，spec 写死条款）。
/// <para>
/// 计时走 <see cref="TimeProvider.CreateTimer"/> —— 测试用 FakeTimeProvider.Advance 驱动。
/// 新窗口取代未结束旧窗口（连续换表）；被取代的旧窗静默关闭（其上界未到，
/// 断流只对未结束的当班窗口成立）。
/// </para>
/// <para>
/// T14-review 前瞻观察落纸：XcpCanTransport DTO DropOldest 丢帧无本地归因，
/// 空窗升级断流只能靠本窗口的时长上界兜底 —— 聚合端不得假设每个空窗必有
/// 逐帧归因事件。
/// </para>
/// <para>
/// 通知契约（T11）：IXcpPlanGapNotifier 实现不得抛异常（轮转中途异常会让表停在
/// stop 态无归因）——内部失败只得放弃该窗/该条归因，与 T14 Attributed 残余语义同源。
/// </para>
/// </summary>
public sealed class PlanGapWatcher : IXcpPlanGapNotifier, IDisposable
{
    private readonly IXcpAcquisitionSink _sink;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private ITimer? _timer;
    private PlanGapWindow? _open;
    private bool _disposed;

    public PlanGapWatcher(IXcpAcquisitionSink sink, TimeProvider? timeProvider = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>当前是否处于计划空窗（聚合端/测试诊断用）。</summary>
    public bool IsOpen
    {
        get { lock (_gate) return _open is not null; }
    }

    /// <summary>T11 计划空窗通知入口：开窗（旧窗被新窗取代）。</summary>
    public void OnPlanGapWindow(RotationGapWindow window)
    {
        try
        {
            Open(window);
        }
        catch
        {
            // 通知契约不得抛异常：仅放弃本次开窗（同 T14 Attributed 残余语义）。
        }
    }

    /// <summary>DTO 样本恢复：期内收到样本即关窗，不产断流归因。</summary>
    public void OnSampleReceived(XcpDaqSample sample)
    {
        try
        {
            CloseWindow();
        }
        catch
        {
            // 恢复路径不得抛（通知契约同源）。
        }
    }

    private void Open(RotationGapWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.ExpectedMaxDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window),
                "Plan gap window must carry a positive expected max duration.");

        PlanGapWindow opened;
        lock (_gate)
        {
            if (_disposed)
                return;
            _timer?.Dispose();
            opened = new PlanGapWindow(window.OdtCount, window.EntryCount, window.ExpectedMaxDuration);
            _open = opened;
            _timer = _timeProvider.CreateTimer(
                OnWindowExpired, null, window.ExpectedMaxDuration, Timeout.InfiniteTimeSpan);
        }

        _sink.OnGap(XcpAcquisitionGap.PlanGapOpened(opened));
    }

    private void CloseWindow()
    {
        lock (_gate)
        {
            if (_open is null)
                return;
            _timer?.Dispose();
            _timer = null;
            _open = null;
        }
    }

    private void OnWindowExpired(object? state)
    {
        PlanGapWindow expired;
        lock (_gate)
        {
            if (_open is null)
                return; // 已恢复或已被新窗取代
            expired = _open;
            _timer?.Dispose();
            _timer = null;
            _open = null;
        }

        try
        {
            _sink.OnGap(XcpAcquisitionGap.AcquisitionInterrupted(
                $"plan gap window expired without recovery after " +
                $"{expired.ExpectedMaxDuration.TotalMilliseconds:F0} ms " +
                $"(odts {expired.OdtCount}, entries {expired.EntryCount})."));
        }
        catch
        {
            // sink 抛异常不得外溢（通知契约）：放弃该条断流归因。
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        CloseWindow();
    }
}