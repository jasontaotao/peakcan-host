using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.J1939;

namespace PeakCan.Host.App.Composition;

/// <summary>
/// 接住在线 <see cref="J1939TpLayer.SessionEvent"/>（丢包/超时/被取代/驱逐，SDK 读线程
/// 同步引发），marshal 到 UI 线程投递 <see cref="TraceViewModel.J1939SessionEvents"/>，
/// 并落 Warning 日志（Release 无 UI 时也留痕）。
/// <para>
/// 结构镜像 <see cref="J1939TpSinkAdapter"/>（App 层桥接 Core 层，Core 不感知 UI/基础设施）：
/// 订阅生命周期随宿主——实现 <see cref="IHostedService"/>，经
/// <c>AddHostedService&lt;J1939TpSessionEventSink&gt;()</c> 注册，宿主启动时订阅、
/// 停止时退订。只订阅 DI singleton（在线层）；<see cref="Services.J1939.J1939ReassemblyService"/>
/// 内部自建的 OfflineMode 临时层不在订阅范围。
/// </para>
/// <para>
/// <b>读线程契约</b>（<see cref="OnSessionEvent"/>）：绝不抛回 <c>ProcessFrame</c>——
/// <see cref="TraceViewModel.AddJ1939SessionEvent"/> 在测试/无 Application 场景经
/// <c>RunOnUiPost</c> inline 执行，UI 集合追加异常须在此吞掉并留日志；生产走 fire-and-forget，
/// 异常按 <c>RunOnUiPost</c> 契约浮现在 UI 线程。
/// </para>
/// </summary>
internal sealed partial class J1939TpSessionEventSink : IHostedService, IDisposable
{
    private readonly J1939TpLayer _layer;
    private readonly TraceViewModel _vm;
    private readonly ILogger<J1939TpSessionEventSink> _logger;

    /// <summary>
    /// Construct the sink. <paramref name="logger"/> optional 镜像
    /// <see cref="J1939TpSinkAdapter"/> 的 null-logger 容忍模式（测试构造）；生产 DI 恒注入。
    /// </summary>
    public J1939TpSessionEventSink(
        J1939TpLayer layer,
        TraceViewModel vm,
        ILogger<J1939TpSessionEventSink>? logger = null)
    {
        _layer = layer ?? throw new ArgumentNullException(nameof(layer));
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _logger = logger ?? NullLogger<J1939TpSessionEventSink>.Instance;
    }

    /// <summary>
    /// 宿主启动：订阅会话事件。字段型事件 <c>+=</c> 不去重，重复 StartAsync 会重复订阅
    /// （每个事件双行/双日志），故先 <c>-=</c> 再 <c>+=</c> 保证幂等——宿主正常只调一次，
    /// 此写法让重入/重启安全。
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _layer.SessionEvent -= OnSessionEvent;
        _layer.SessionEvent += OnSessionEvent;
        return Task.CompletedTask;
    }

    /// <summary>宿主停止：退订。可重复调用（-= 对未订阅 handler 是 no-op）。</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _layer.SessionEvent -= OnSessionEvent;
        return Task.CompletedTask;
    }

    /// <summary>DI 容器 dispose 兜底（宿主异常路径未走 StopAsync 时也退订，防悬挂）。</summary>
    public void Dispose() => _layer.SessionEvent -= OnSessionEvent;

    private void OnSessionEvent(J1939SessionEvent evt)
    {
        try
        {
            // 读线程 → UI：fire-and-forget（热路径）。测试无 Application → inline 同步完成。
            ((Action)(() => _vm.AddJ1939SessionEvent(evt))).RunOnUiPost();
        }
        catch (Exception ex)
        {
            // 吞掉并留日志——绝不把异常抛回 SDK 读线程。
            LogForwardFailed(_logger, ex);
        }
        LogSessionEvent(_logger, evt.Kind.ToString(), evt.Sa, evt.Da, evt.Pgn, evt.Mode.ToString(), evt.Detail);
    }

    // Logging：EventId 9303/9304 与 J1939TpSinkAdapter 的 9301/9302 同段号段（J1939 App 面）。
    // ILogger 参数非空（NullLogger 兜底），避免源生成体 CS8602（LoggingFlow 先例）。
    /// <summary>EventId 9303: 一次 J1939 会话异常事件（含 SA/DA/PGN/模式/详情）。</summary>
    [LoggerMessage(EventId = 9303, Level = LogLevel.Warning, Message = "J1939 session event {Kind} SA=0x{Sa:X2} DA=0x{Da:X2} PGN=0x{Pgn:X4} mode={Mode} {Detail}")]
    private static partial void LogSessionEvent(ILogger logger, string kind, byte sa, byte da, uint pgn, string mode, string detail);

    /// <summary>EventId 9304: 会话事件投递 Trace 面板失败（不冒泡回读线程）。</summary>
    [LoggerMessage(EventId = 9304, Level = LogLevel.Error, Message = "Failed to forward J1939 session event to trace")]
    private static partial void LogForwardFailed(ILogger logger, Exception ex);
}
