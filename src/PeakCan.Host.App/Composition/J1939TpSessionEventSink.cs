using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.J1939;
using PeakCan.Host.Core.Services;

namespace PeakCan.Host.App.Composition;

/// <summary>
/// 接住在线 <see cref="J1939TpLayer.SessionEvent"/>（丢包/超时/被取代/驱逐），marshal 到 UI
/// 线程批量投递 <see cref="TraceViewModel.J1939SessionEvents"/>，并落 Warning 摘要日志。
/// <para>
/// <b>洪峰防护（发散审查 HIGH）</b>：<c>SessionEvent</c> 无上游限速（故障节点高频重发 CM →
/// 逐事件 Superseded；看门狗每 100ms 最多 32 个 Timeout）。旧实现逐事件
/// <c>RunOnUiPost</c>（fire-and-forget，派发队列无界）+ 逐事件 O(n) 裁剪，遇故障节点会卡死 UI。
/// 现改为 <see cref="SignalViewModel"/> 同款 drain-tick：读线程只缓冲，~30Hz 滴答单次
/// <c>RunOnUiPost</c> 批量灌入——派发队列从"无界"降为"每滴答 1 次"，日志从逐事件降为每批摘要。
/// </para>
/// <para>
/// 结构镜像 <see cref="J1939TpSinkAdapter"/>（App 层桥接 Core 层，Core 不感知 UI）：订阅/退订随宿主
/// （<see cref="IHostedService"/>，经 <c>AddHostedService</c> 注册）。只订阅 DI singleton（在线层）；
/// <see cref="Services.J1939.J1939ReassemblyService"/> 自建的 OfflineMode 临时层不在订阅范围。
/// </para>
/// <para>
/// <b>读线程契约</b>：<see cref="OnSessionEvent"/> 只做锁内缓冲，绝不抛回 <c>ProcessFrame</c>；
/// 滴答投递（UI 线程）内的 VM 追加异常由 try/catch 吞掉并留日志（测试无 Application 时 inline）。
/// </para>
/// </summary>
internal sealed partial class J1939TpSessionEventSink : IHostedService, IDisposable
{
    /// <summary>滴答周期：~30Hz，与 <see cref="SignalViewModel"/> 的 DrainInterval 同量级。</summary>
    private static readonly TimeSpan DrainInterval = TimeSpan.FromMilliseconds(33);

    private readonly J1939TpLayer _layer;
    private readonly TraceViewModel _vm;
    private readonly ILogger<J1939TpSessionEventSink> _logger;
    private readonly ITimerFactory _timerFactory;
    private readonly object _pendingLock = new();
    private readonly List<J1939SessionEvent> _pending = new();
    private ICyclicTimer? _drainTimer;

    /// <summary>
    /// Construct the sink. <paramref name="logger"/> optional 镜像
    /// <see cref="J1939TpSinkAdapter"/> 的 null-logger 容忍模式；<paramref name="timerFactory"/>
    /// 缺省 <see cref="CyclicTimerFactory"/>（ICyclicTimer 生产工厂——<see cref="PeriodicTimerFactory"/>
    /// 只产 IPeriodicTimer，见其类注释）。测试注入 <c>FakeTimerFactory</c> 确定性驱动滴答。
    /// </summary>
    public J1939TpSessionEventSink(
        J1939TpLayer layer,
        TraceViewModel vm,
        ILogger<J1939TpSessionEventSink>? logger = null,
        ITimerFactory? timerFactory = null)
    {
        _layer = layer ?? throw new ArgumentNullException(nameof(layer));
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _logger = logger ?? NullLogger<J1939TpSessionEventSink>.Instance;
        _timerFactory = timerFactory ?? new CyclicTimerFactory();
    }

    /// <summary>
    /// 宿主启动：订阅会话事件 + 启动滴答定时器。字段型事件 <c>+=</c> 不去重，
    /// 先 <c>-=</c> 再 <c>+=</c> 保证重入/重启幂等；定时器同理——重入（无 StopAsync 的二次
    /// StartAsync）时先停旧表，防双表并发滴答重复投递/泄漏（review MEDIUM）。
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _layer.SessionEvent -= OnSessionEvent;
        _layer.SessionEvent += OnSessionEvent;
        _drainTimer?.Dispose();
        _drainTimer = _timerFactory.CreateCyclicTimer(OnDrainTick, null, DrainInterval);
        return Task.CompletedTask;
    }

    /// <summary>宿主停止：退订 + 停定时器。可重复调用。</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _layer.SessionEvent -= OnSessionEvent;
        _drainTimer?.Dispose();
        _drainTimer = null;
        return Task.CompletedTask;
    }

    /// <summary>DI 容器 dispose 兜底（宿主异常路径未走 StopAsync 时也退订/停表）。</summary>
    public void Dispose()
    {
        _layer.SessionEvent -= OnSessionEvent;
        _drainTimer?.Dispose();
        _drainTimer = null;
    }

    /// <summary>
    /// SDK 读线程/看门狗定时器线程回调：只缓冲进 pending 列表，不触碰 UI、不落日志
    /// （聚合在滴答摘要）——读线程非阻塞契约 + 洪峰节流。
    /// </summary>
    private void OnSessionEvent(J1939SessionEvent evt)
    {
        lock (_pendingLock) _pending.Add(evt);
    }

    /// <summary>滴答回调：marshal 到 UI 线程执行 <see cref="DrainPending"/>（测试无 Application → inline）。</summary>
    private void OnDrainTick(object? state) => ((Action)DrainPending).RunOnUiPost();

    /// <summary>
    /// 锁内换出整批 → UI 线程批量追加（每批只裁一次）+ 每批一条 Warning 摘要。
    /// 洪峰下派发队列 ≤1 次/滴答、日志 ≤30 条/秒。
    /// </summary>
    private void DrainPending()
    {
        J1939SessionEvent[] batch;
        lock (_pendingLock)
        {
            if (_pending.Count == 0)
                return;
            batch = _pending.ToArray();
            _pending.Clear();
        }

        try
        {
            _vm.AddJ1939SessionEvents(batch);
        }
        catch (Exception ex)
        {
            // 吞掉并留日志——绝不把异常抛回 SDK 读线程。
            LogForwardFailed(_logger, ex);
        }
        LogDrainSummary(_logger, batch.Length, batch[0].Kind.ToString(), batch[0].Sa, batch[0].Da);
    }

    // Logging：EventId 9303/9304 与 J1939TpSinkAdapter 的 9301/9302 同段号段（J1939 App 面）。
    /// <summary>EventId 9303: 每滴答一条会话事件摘要（洪峰节流后 ≤30 条/秒）。</summary>
    [LoggerMessage(EventId = 9303, Level = LogLevel.Warning, Message = "J1939 session events: {Count} (first {Kind} SA=0x{Sa:X2} DA=0x{Da:X2})")]
    private static partial void LogDrainSummary(ILogger logger, int count, string kind, byte sa, byte da);

    /// <summary>EventId 9304: 会话事件批量投递 Trace 面板失败（不冒泡回读线程）。</summary>
    [LoggerMessage(EventId = 9304, Level = LogLevel.Error, Message = "Failed to forward J1939 session event batch to trace")]
    private static partial void LogForwardFailed(ILogger logger, Exception ex);
}
