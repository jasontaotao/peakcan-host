using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PeakCan.Host.App.ViewModels.Xcp;

/// <summary>
/// XCP tab orchestrator（S3-T8，spec D1）：组装 T3 连接 + T7 采集 + T5 卡片 + T6 归因
/// 四面板，视图层（XcpView）按本类单一入口绑定。
/// <para>
/// 接线义务（T7b/T5 评审移交）：
/// <list type="bullet">
/// <item>T6 归因面板经 <see cref="XcpAttributionPanelViewModel.Attach"/> 订阅
/// T5 卡片面板的 <see cref="XcpCardPanelViewModel.GapObserved"/>（DI 路径 ctor 注入已
/// 完成订阅，Attach 幂等直返；测试/手工构造路径由此兜底）。</item>
/// <item>D5 host 两态（未连总线/被过滤）由 T3 连接状态机生产——本类订阅
/// <see cref="XcpConnectionPanelViewModel.ConnectionState"/> 变化转发
/// <see cref="XcpAttributionPanelViewModel.ObserveHostAttribution"/>，视图不自行判状态机。</item>
/// <item>T5 卡片面板的 20 Hz Flush + RefreshStaleness 由视图层 DispatcherTimer 驱动
///（XcpView code-behind），本 VM 不持 Dispatcher（T5 定案）。</item>
/// <item>采集 StopAsync 的 App 关闭路径 await 由组合根 IHostedService 宿主承接
///（AppHostBuilder.XcpAcquisitionShutdownService，T7b L-2），本 VM 只暴露面板。</item>
/// </list>
/// </para>
/// </summary>
public sealed partial class XcpViewModel : ObservableObject
{
    /// <summary>T3 连接面板（A2L 加载 / 通道快照 / 连接状态机）。</summary>
    public XcpConnectionPanelViewModel Connection { get; }

    /// <summary>T5 卡片面板（关注集卡片 + 20 Hz 批量 flush 入口）。</summary>
    public XcpCardPanelViewModel Cards { get; }

    /// <summary>T6 归因面板（D5 合并归因表）。</summary>
    public XcpAttributionPanelViewModel Attribution { get; }

    /// <summary>T7 采集面板（Start/Stop、会话状态、覆盖清单）。</summary>
    public XcpAcquisitionPanelViewModel Acquisition { get; }

    /// <summary>S4-T5 记录面板（MDF 记录控件区，spec D5）。</summary>
    public XcpRecordPanelViewModel Record { get; }

    /// <summary>S5-T4 标定写回面板（spec D5 行内写值 + 参数集导出/下发）。</summary>
    public XcpWritebackViewModel? Writeback { get; }

    /// <summary>S6-T3 回放面板（spec D7 TabControl 第二页；可空——无接线时回放页空态）。</summary>
    public XcpReplayPanelViewModel? Replay { get; }

    /// <summary>S6-T4 MAP 只读可视化面板（spec D7 第三页；可空注入）。</summary>
    public XcpMapPanelViewModel? Map { get; }

    /// <summary>S6-T5 参数集 diff 面板（spec D7 第四页；可空注入）。</summary>
    public XcpDiffPanelViewModel? Diff { get; }

    /// <summary>
    /// 可空注入构造（保既有 VM 测试构造模式：无参可建）。
    /// DI 路径四面板均为 singleton，经本构造原样组装（AppHostBuilderXcpTests 钉住）。
    /// </summary>
    public XcpViewModel(
        XcpConnectionPanelViewModel? connection = null,
        XcpCardPanelViewModel? cards = null,
        XcpAttributionPanelViewModel? attribution = null,
        XcpAcquisitionPanelViewModel? acquisition = null,
        XcpRecordPanelViewModel? record = null,
        XcpWritebackViewModel? writeback = null,
        XcpReplayPanelViewModel? replay = null,
        XcpMapPanelViewModel? map = null,
        XcpDiffPanelViewModel? diff = null)
    {
        Connection = connection ?? new XcpConnectionPanelViewModel();
        Cards = cards ?? new XcpCardPanelViewModel();
        Attribution = attribution ?? new XcpAttributionPanelViewModel();
        // 缺省组装：采集面板接 T3 连接面板 + T5 卡片 sink（D3 管线同一实例）。
        Acquisition = acquisition ?? new XcpAcquisitionPanelViewModel(Connection, Cards.Sink);
        // S4-T5：记录面板（D4 广播装配的记录 sink 由组合根注入；缺省自建测试面）。
        Record = record ?? new XcpRecordPanelViewModel(Acquisition, Cards, Connection);
        // S5-T4：写回面板（可空——无写回接线时卡片写按钮禁用，零行为变化）。
        Writeback = writeback;
        // S6-T3：回放面板（可空注入，与 Writeback 同款保既有 VM 测试构造）。
        Replay = replay;
        // S6-T4：MAP 面板（缺省自建——A2L 加载后的 MAP 清单装配走本类接线）。
        Map = map ?? new XcpMapPanelViewModel();
        // S6-T5：参数集 diff 面板（可空注入，同款保既有 VM 测试构造）。
        Diff = diff;

        // D5：采集 Stop 先停记录（尾部样本落盘）——同一面板实例。
        Acquisition.BeforeStopAsync ??= Record.StopBeforeAcquisitionAsync;

        // T6 接力：归因面板订阅卡片 GapObserved（幂等——DI 路径已在 ctor 订阅）。
        Attribution.Attach(Cards);

        // D5 host 两态直喂：连接状态机 → 归因面板（初始态立即喂一次）。
        Connection.PropertyChanged += OnConnectionPropertyChanged;
        Attribution.ObserveHostAttribution(Connection.AttributionCell);

        // S6-T7 评审 P0-1：A2L 加载 → MAP 清单装配（组合根→VM 的最后一跳）。
        // 初始态已 Loaded/Connected 也装配一次（A2L 先于本 VM 创建的场景）。
        if (Connection.ConnectionState != XcpConnectionState.Disconnected)
            Map?.RefreshMaps();
    }

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 只在连接状态机变化时刷新 host 归因格（AttributionCell 派生自该状态）。
        if (e.PropertyName is null or nameof(XcpConnectionPanelViewModel.ConnectionState))
        {
            Attribution.ObserveHostAttribution(Connection.AttributionCell);
            // P0-1：A2L 加载/切换后重装 MAP 清单（幂等；Disconnected 态清空）。
            Map?.RefreshMaps();
        }
    }
}
