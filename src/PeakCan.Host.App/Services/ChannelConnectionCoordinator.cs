using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Channel;
using PeakCan.Host.Infrastructure.Statistics;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Services;

/// <summary>一次多通道连接运行的结果：成功路数 + 最后一个失败槽的 UI 文本。</summary>
/// <param name="ConnectedCount">State == "已连接" 的槽位数。</param>
/// <param name="LastFailureText">最后一个失败槽的诊断文本（连接失败/连接异常）；
/// 全部成功或无有效槽时为 null。</param>
public sealed record ConnectRunResult(int ConnectedCount, string? LastFailureText);

/// <summary>
/// P2-1 真拆类（2026-09-06）：多通道连接生命周期从 <c>AppShellViewModel</c> 的
/// ChannelFlow partial 提升为独立类。本类拥有：连接行集合（
/// <see cref="Connections"/>，即旧 <c>ChannelConnections</c> 同一身份）、
/// best-effort 多通道 connect/disconnect 循环、路由注册/注销、读循环错误
/// 订阅、SendService 通道接线、总线负载波特率喂给、以及每槽状态字符串。
/// <para>
/// <b>与 VM 的边界：</b>工具栏文本（StatusMessage/ConnectionState 聚合）、
/// 探测/枚举、SelectedChannel 持久化仍属 VM——本类不触碰任何 UI 文本属性，
/// 只通过 <see cref="ConnectionsChanged"/> 通知 VM 刷新（对应旧
/// per-slot <c>StateChanged</c> → <c>NotifyConnectionStateChanged</c> 路径）。
/// <see cref="ConnectAllAsync"/> 返回 <see cref="ConnectRunResult"/>，
/// VM 据此映射聚合文本（保持与拆分前完全一致的最终文本行为）。
/// </para>
/// <para>
/// <b>线程模型：</b>与拆分前一致——所有调用点在 UI 线程；行
/// <see cref="ChannelConnection.State"/> 变更经 <see cref="ConnectionsChanged"/>
/// 同步冒泡。方法内部逐槽 <c>ConfigureAwait(true)</c>。
/// </para>
/// </summary>
internal sealed partial class ChannelConnectionCoordinator
{
    private readonly IChannelFactory _channelFactory;
    private readonly ChannelRouter _router;
    private readonly SendService _sendService;
    private readonly BusStatisticsCollector? _busStats;
    private readonly Action<ReadLoopError>? _readLoopErrorSink;
    // 非 generic ILogger：VM 直接传入自有 logger（保持拆分前同一日志类别
    // AppShellViewModel——review HIGH 修复：否则 NullLogger 静默吞掉全部
    // connect/disconnect 诊断日志）。LoggerMessage 源生成器本就接受 ILogger。
    private readonly ILogger _logger;
    // M2.4b（spec §5-D6.7）：SecOC 旁路 verdict 表。断开时清空，防悬空标注
    // （旧 handle 的 verdict 不得落到重连后的新帧上）。null = 测试构造点无 SecOC。
    private readonly PeakCan.Host.Infrastructure.Channel.SecOc.SecOcVerdictTable? _secOcVerdicts;

    public ChannelConnectionCoordinator(
        IChannelFactory channelFactory,
        ChannelRouter router,
        SendService sendService,
        BusStatisticsCollector? busStats = null,
        Action<ReadLoopError>? readLoopErrorSink = null,
        ILogger? logger = null,
        PeakCan.Host.Infrastructure.Channel.SecOc.SecOcVerdictTable? secOcVerdicts = null)
    {
        _channelFactory = channelFactory ?? throw new ArgumentNullException(nameof(channelFactory));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _sendService = sendService ?? throw new ArgumentNullException(nameof(sendService));
        _busStats = busStats;
        _readLoopErrorSink = readLoopErrorSink;
        _logger = logger ?? NullLogger<ChannelConnectionCoordinator>.Instance;
        _secOcVerdicts = secOcVerdicts;
    }

    /// <summary>
    /// 连接行集合（原 AppShellViewModel.ChannelConnections 的同一对象身份——
    /// VM 以委托属性暴露，DataGrid/测试的既有引用不受影响）。
    /// </summary>
    public ObservableCollection<ChannelConnection> Connections { get; } = new();

    /// <summary>
    /// 任一连接行 <see cref="ChannelConnection.State"/> 变更时触发（VM 据此
    /// 刷新 IsConnected/CanExecute + 发布已连接快照）。行 Add/Clear 本身不触发
    ///（与拆分前一致：CollectionChanged 处理器只挂/退 StateChanged，不通知）。
    /// </summary>
    public event Action? ConnectionsChanged;

    /// <summary>连接完成后的 SendService 通道接线出口（VM 公开属性委托至此）。</summary>
    public SendService SendService => _sendService;

    /// <summary>
    /// Best-effort 多通道 connect：逐槽独立连接，失败标红继续，绝不阻塞其余槽。
    /// 行为与拆分前 ChannelFlow.ConnectCoreAsync 逐字一致（含失败槽 DisposeAsync
    /// 兜底、异常槽 Disconnect+Unregister+Dispose 三连、SetBitrate 接线），
    /// 仅把最终聚合文本改为经 <see cref="ConnectRunResult"/> 返回。
    /// </summary>
    public async Task<ConnectRunResult> ConnectAllAsync(IReadOnlyList<ConnectionConfig> configs)
    {
        ArgumentNullException.ThrowIfNull(configs);
        string? lastFailureText = null;

        foreach (var cfg in configs)
        {
            if (cfg.Channel is null) continue; // null 组跳过
            var handle = cfg.Channel.Handle;
            var rate = cfg.BaudRate;
            var channel = _channelFactory.Create(new ChannelId(handle));
            try
            {
                var result = await channel.ConnectAsync(rate, fd: cfg.IsFd).ConfigureAwait(true);
                if (result.IsSuccess)
                {
                    _router.RegisterChannel(channel);
                    // v3.16.9.4 PATCH: subscribe to read-loop errors so bus-off /
                    // driver unload / hardware faults surface on the UI status
                    // bar. Event fires on the SDK read thread; the sink (VM
                    // handler) must marshal to the UI thread itself.
                    if (_readLoopErrorSink is not null)
                    {
                        channel.ReadLoopError += _readLoopErrorSink;
                    }
                    AddRow(new ChannelConnection(channel, cfg.Channel.Name, rate, cfg.IsFd));
                    // 2026-09-06 设计层 MEDIUM：把该路所选预设的标称波特率喂给
                    // 统计收集器——总线负载 % 的位预算分母。多通道混合波特率时
                    // 最后一路成功连接的速率生效（聚合口径本身是近似，收集器
                    // 文档已注明）。收集器为 null（测试构造点）时 no-op。
                    _busStats?.SetBitrate(BaudRateMap.NominalBps(rate));
                    LogConnectOk(_logger, handle);
                }
                else
                {
                    // 尽力式：该组标红跳过，不阻塞其余组。
                    var err = result.Error!;
                    AddRow(new ChannelConnection(channel, cfg.Channel.Name, rate, cfg.IsFd)
                        { State = $"连接失败: {err.Code}" });
                    lastFailureText = $"通道 {cfg.Channel.Name} 连接失败: {err.Code} {err.Message}";
                    LogConnectFailed(_logger, handle, err.Code, err.Message);
                    // PeakCanChannel ctor allocates a CancellationTokenSource
                    // (used by the read loop). On a failed Connect the channel
                    // never acquires the hardware, so the safe teardown is to
                    // dispose it now rather than wait for GC.
                    await channel.DisposeAsync().ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                // 尽力式：该组标红，继续其余组。
                AddRow(new ChannelConnection(channel, cfg.Channel.Name, rate, cfg.IsFd)
                    { State = $"连接异常: {ex.GetType().Name}" });
                lastFailureText = $"通道 {cfg.Channel.Name} 连接异常: {ex.GetType().Name}";
                LogConnectThrew(_logger, handle, ex);
                // RegisterChannel 抛异常时硬件可能已连接但未注册——先
                // 断开硬件连接再 Unregister + Dispose，避免 handle 泄漏
                // （review M2 fix：DisposeAsync 不保证断开硬件连接）。
                try { await channel.DisconnectAsync().ConfigureAwait(true); }
                catch (Exception discEx) { LogDisconnectThrew(_logger, handle, discEx); }
                try { _router.UnregisterChannel(channel); }
                catch (Exception unregEx) { LogUnregisterFailed(_logger, handle, unregEx); }
                await channel.DisposeAsync().ConfigureAwait(true);
            }
        }

        // Publish the connected set to SendService (default target = first
        // connected channel).
        var connected = Connections.Where(c => c.State == "已连接").ToList();
        _sendService.SetChannels(connected.ToDictionary(c => c.Channel.Id, c => c.Channel));
        _sendService.ActiveChannel = connected.FirstOrDefault()?.Channel;

        return new ConnectRunResult(connected.Count, lastFailureText);
    }

    /// <summary>
    /// Disconnect every connected channel, unregister each from the router,
    /// unsubscribe read-loop errors, then clear the collection. Per-channel
    /// failures are swallowed (best-effort) so one dead channel does not
    /// leave the rest connected. 与拆分前 ChannelFlow.DisconnectAsync 循环逐字一致。
    /// </summary>
    public async Task DisconnectAllAsync()
    {
        var snapshot = Connections.ToList();
        foreach (var conn in snapshot)
        {
            try
            {
                await conn.Channel.DisconnectAsync().ConfigureAwait(true);
                LogDisconnectOk(_logger, conn.Channel.Id.Handle);
            }
            catch (Exception ex)
            {
                // DisconnectAsync swallows hardware failures per its own
                // contract; surface the exception as a per-channel state so
                // the operator sees which channel failed to disconnect.
                conn.State = $"断开异常: {ex.GetType().Name}";
                LogDisconnectThrew(_logger, conn.Channel.Id.Handle, ex);
            }
            try { _router.UnregisterChannel(conn.Channel); }
            catch (Exception unregEx) { LogUnregisterFailed(_logger, conn.Channel.Id.Handle, unregEx); }
            // v3.16.9.4 PATCH: unsubscribe read-loop errors before dropping
            // the reference — match the source-gen delegate equality so the
            // old channel's event does not pin the VM.
            if (_readLoopErrorSink is not null)
            {
                conn.Channel.ReadLoopError -= _readLoopErrorSink;
            }
            conn.State = "已断开";
        }
        foreach (var conn in snapshot)
        {
            conn.StateChanged -= OnRowStateChanged;
        }
        Connections.Clear();
        _sendService.SetChannels(null);
        _sendService.ActiveChannel = null;
        // M2.4b（spec §5-D6.7）：全部通道断开 → 清空 SecOC 旁路 verdict 表，
        // 防旧 verdict 悬空标注到重连后的新帧。
        _secOcVerdicts?.Clear();
    }

    private void AddRow(ChannelConnection row)
    {
        row.StateChanged += OnRowStateChanged;
        Connections.Add(row);
    }

    private void OnRowStateChanged() => ConnectionsChanged?.Invoke();

    [LoggerMessage(Level = LogLevel.Information, Message = "Connect OK on handle 0x{Handle:X2}")]
    private static partial void LogConnectOk(ILogger logger, ushort handle);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connect failed on handle 0x{Handle:X2}: {Code} {Message}")]
    private static partial void LogConnectFailed(ILogger logger, ushort handle, ErrorCode code, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Connect threw on handle 0x{Handle:X2}")]
    private static partial void LogConnectThrew(ILogger logger, ushort handle, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connect catch-arm UnregisterChannel threw on handle 0x{Handle:X2}")]
    private static partial void LogUnregisterFailed(ILogger logger, ushort handle, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Disconnect OK on handle 0x{Handle:X2}")]
    private static partial void LogDisconnectOk(ILogger logger, ushort handle);

    [LoggerMessage(Level = LogLevel.Error, Message = "Disconnect threw on handle 0x{Handle:X2}")]
    private static partial void LogDisconnectThrew(ILogger logger, ushort handle, Exception ex);
}
