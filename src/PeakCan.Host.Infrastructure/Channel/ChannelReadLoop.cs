using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core;
using PeakCan.Host.Core;

namespace PeakCan.Host.Infrastructure.Channel;

/// <summary>
/// 双厂商驱动读循环骨架（P2-1 2026-09-06）：PEAK（ReadLoopFlow 132 行）与
/// ZLG（ReadLoopFlow 104 行）此前各维护一套几乎同构的轮询循环——双路径
/// （classic/FD）排空、失败计数（每迭代一次而非每异常一次）、backoff
/// （1/10/50ms）、give-up 判定（100 次连续失败）、<see cref="ReadLoopError"/>
/// 每订阅者 try/catch 隔离——只有 SDK 读调用、帧解码与 give-up 收口不同。
/// 厂商侧只需实现 3 个 hook（<see cref="DrainClassicFrames"/> /
/// <see cref="DrainFdFrames"/> / <see cref="OnReadLoopGiveUp"/>）。
/// <para>
/// 顺带修复一个潜在 bug：PEAK 侧在 W18 拆分时丢失了
/// <c>LogReadLoopException</c>/<c>LogReadLoopSubscriberThrew</c> 的
/// [LoggerMessage] 属性——无实现 partial 方法的调用点被编译器静默移除，
/// 即 PEAK 读循环异常日志自拆分以来从未真正输出（ZLG 侧正常）。
/// 骨架持有带属性的 LoggerMessage 声明，此类日志恢复。
/// </para>
/// </summary>
public abstract partial class ChannelReadLoop
{
    /// <summary>连续失败上限（bus-dead heuristic）；厂商通道类以 const 别名暴露保持测试兼容。</summary>
    internal const int MaxConsecutiveReadFailures = 100;

    // 连续失败后的 backoff（ms）。任一帧成功即归零。
    private static readonly int[] BackoffMs = { 1, 10, 50 };

    private readonly ILogger _logger;
    // 2026-09-06 review MEDIUM 修复：hook 在排空中途抛异常时，此前已发出的帧
    // 不能丢——旧实现用局部 bool 在 try 作用域内逐帧置位（抛出后仍可见）。
    // 骨架改为迭代级字段：hook 每发出一帧调 MarkFrameEmitted()，骨架在
    // 迭代开始时清零、两条 drain 结束后统一读取。
    private bool _gotAnyFrameThisIteration;

    protected ChannelReadLoop(ChannelId id, ILogger logger)
    {
        Id = id;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>通道标识（public：满足 <see cref="ICanChannel.Id"/>，子类勿再声明遮蔽属性）。</summary>
    public ChannelId Id { get; }

    /// <summary>
    /// <see cref="DrainClassicFrames"/>/<see cref="DrainFdFrames"/> 内每发出一帧
    /// 调用一次（驱动"任一帧成功 → 失败计数归零"契约，中途抛异常也不丢）。
    /// </summary>
    protected void MarkFrameEmitted() => _gotAnyFrameThisIteration = true;

    /// <summary>
    /// v3.16.9.4 PATCH 语义：读循环失败上浮到 UI 层。在 SDK 读线程触发
    /// （订阅方自行封送 UI）。与 ILogger 记录并存（additive）。
    /// </summary>
    public event Action<ReadLoopError>? ReadLoopError;

    /// <summary>
    /// 排空 classic 路径当前积压的所有帧：每发出一帧调
    /// <see cref="MarkFrameEmitted"/>，并触发 <c>FrameReceived</c>。
    /// SDK 异常直接抛出（骨架统一 catch：记日志 + SafeEmit + 计一次迭代失败；
    /// 抛出前已发出的帧经 <see cref="MarkFrameEmitted"/> 保留计数语义）。
    /// </summary>
    protected abstract void DrainClassicFrames();

    /// <summary>同 <see cref="DrainClassicFrames"/>，FD 路径。</summary>
    protected abstract void DrainFdFrames();

    /// <summary>
    /// give-up（连续失败达到 <see cref="MaxConsecutiveReadFailures"/>）后的
    /// SDK 侧收口：PEAK = Uninitialize + 连接门 MarkFailed；ZLG = 标记断开 +
    /// 复位 + 释放设备引用。
    /// </summary>
    protected abstract void OnReadLoopGiveUp();

    /// <summary>
    /// 读循环主体。经典/FD 读各自独立 try/catch（v3.16.9.4：classic 路径的
    /// 订阅者异常不得吞掉同迭代的 FD 帧）。give-up 后退出循环（不 busy-spin
    /// 死总线）。
    /// </summary>
    internal async Task ReadLoopAsync(CancellationToken ct)
    {
        var consecutiveFailures = 0;
        while (!ct.IsCancellationRequested)
        {
            _gotAnyFrameThisIteration = false;
            bool iterationFailed = false;
            try
            {
                DrainClassicFrames();
            }
            catch (Exception ex)
            {
                LogReadLoopException(_logger, Id.Handle, "classic", ex);
                SafeEmitReadLoopError(new ReadLoopError(Id.Handle, ReadLoopErrorKind.ClassicReadException, ex));
                iterationFailed = true;
            }
            try
            {
                DrainFdFrames();
            }
            catch (Exception ex)
            {
                LogReadLoopException(_logger, Id.Handle, "FD", ex);
                SafeEmitReadLoopError(new ReadLoopError(Id.Handle, ReadLoopErrorKind.FdReadException, ex));
                iterationFailed = true;
            }

            // 按迭代计数（不按异常次数）：classic+FD 双失败的迭代仍算 1 次
            //（与拆分前语义一致）。任一帧成功（含 drain 中途抛异常前已发出的）
            // 即归零。
            var gotAnyFrame = _gotAnyFrameThisIteration;
            if (iterationFailed && !gotAnyFrame) consecutiveFailures++;
            if (gotAnyFrame) consecutiveFailures = 0;

            if (consecutiveFailures >= MaxConsecutiveReadFailures)
            {
                // 死总线不 busy-spin：单次 fatal 日志 + LoopGivingUp 事件 +
                // SDK 收口，然后退出循环。
                LogReadLoopGivingUp(_logger, Id.Handle, consecutiveFailures);
                SafeEmitReadLoopError(new ReadLoopError(Id.Handle, ReadLoopErrorKind.LoopGivingUp, null));
                OnReadLoopGiveUp();
                return;
            }

            var delay = consecutiveFailures == 0
                ? 1
                : BackoffMs[Math.Min(consecutiveFailures - 1, BackoffMs.Length - 1)];
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// 每订阅者独立 try/catch：一个行为异常的订阅者（如对已释放 Dispatcher
    /// 抛异常的 UI 处理器）不得拖垮 SDK 读循环。与 ChannelRouter 的
    /// sink-OnError 隔离模式一致：循环是高优先级线程，订阅者是尽力而为。
    /// </summary>
    private void SafeEmitReadLoopError(ReadLoopError err)
    {
        var handler = ReadLoopError;
        if (handler is null) return;
        foreach (Action<ReadLoopError> sub in handler.GetInvocationList())
        {
            try { sub(err); }
            catch (Exception ex)
            {
                LogReadLoopSubscriberThrew(_logger, Id.Handle, sub.Method.DeclaringType?.FullName ?? "?", ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Read loop exception: handle 0x{Handle:X4} {Kind}")]
    private static partial void LogReadLoopException(ILogger logger, ushort handle, string kind, Exception error);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Read loop giving up on handle 0x{Handle:X2} after {Failures} consecutive failures — bus appears dead, call Disconnect+Connect to recover")]
    private static partial void LogReadLoopGivingUp(ILogger logger, ushort handle, int failures);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Read loop subscriber threw: handle 0x{Handle:X4} sub={Sub}")]
    private static partial void LogReadLoopSubscriberThrew(ILogger logger, ushort handle, string sub, Exception ex);
}
