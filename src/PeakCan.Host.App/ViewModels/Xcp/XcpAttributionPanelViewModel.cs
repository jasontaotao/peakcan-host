using System;
using System.Collections.Generic;
using A2lEditor.Core.Layout;
using CommunityToolkit.Mvvm.ComponentModel;
using PeakCan.Host.Core.Xcp.Receive;

namespace PeakCan.Host.App.ViewModels.Xcp;

/// <summary>
/// D5 合并归因表（spec §2-D5）六大格类别。映射口径（S3-T6 断链判据）：
/// <list type="bullet">
/// <item>UnconnectedBus / FilteredOut —— host 态直喂，生产者：T3 连接状态机
/// <see cref="XcpConnectionPanelViewModel.AttributionCell"/> / T5 关注集筛选；</item>
/// <item>PackageMissingCause —— <c>Gap.Kind == MissingCauseAttributed</c>，按
/// <c>Gap.Cause</c>（包侧五值）分格；</item>
/// <item>AcquisitionInterrupted —— <c>Gap.Kind == AcquisitionInterrupted</c>
/// （Cause 槽位同值但不落包侧格）；</item>
/// <item>FrameAttribution —— <c>Gap.Kind == ReceiveAttribution</c>，按
/// <c>Gap.ReceiveKind</c>（六值）分格；</item>
/// <item>PlanGap —— <c>Gap.Kind == PlanGapOpened</c>，显示
/// <c>Gap.ExpectedMaxDuration</c>。</item>
/// </list>
/// 无法映射的条目组合 fail loud（不做第四类黑盒串接）。
/// </summary>
public enum XcpAttributionCellKind
{
    /// <summary>D5 表"未连总线"格（host 态直喂）。</summary>
    UnconnectedBus,

    /// <summary>D5 表"被过滤"格（host 关注集筛选直喂）。</summary>
    FilteredOut,

    /// <summary>D5 表包侧五值 MissingCause 格（按 Cause 分格）。</summary>
    PackageMissingCause,

    /// <summary>D5 表断流格（PlanGap 超时升级）。</summary>
    AcquisitionInterrupted,

    /// <summary>D5 表逐帧归因格（按 ReceiveKind 分格）。</summary>
    FrameAttribution,

    /// <summary>D5 表计划空窗格（换表中）。</summary>
    PlanGap,
}

/// <summary>
/// 归因单格：聚合计数 + 最近一条明细（人类可读）。计数只增不减——
/// host 态退场（Connected）后历史计数保留，仅"当前归因"查询切换来源。
/// </summary>
public partial class XcpAttributionCellViewModel : ObservableObject
{
    [ObservableProperty]
    private long _count;

    /// <summary>最近一条归因明细（人读文本，取 Gap.Detail 原文）。</summary>
    [ObservableProperty]
    private string? _lastDetail;

    /// <summary>最近一次携带的预期时长上界（仅 PlanGapOpened 携带，spec D5）。</summary>
    [ObservableProperty]
    private TimeSpan? _lastExpectedMaxDuration;

    public XcpAttributionCellKind Kind { get; }

    public string Title { get; }

    /// <summary>包侧五值格的 Cause 键（其余类别 null）。</summary>
    public MissingCause? MissingCause { get; }

    /// <summary>逐帧归因格的 ReceiveKind 键（其余类别 null）。</summary>
    public XcpReceiveAttributionKind? ReceiveKind { get; }

    public XcpAttributionCellViewModel(
        XcpAttributionCellKind kind,
        string title,
        MissingCause? missingCause = null,
        XcpReceiveAttributionKind? receiveKind = null)
    {
        Kind = kind;
        Title = title;
        MissingCause = missingCause;
        ReceiveKind = receiveKind;
    }

    internal void Record(string detail, TimeSpan? expectedMaxDuration = null)
    {
        Count++;
        LastDetail = detail;
        LastExpectedMaxDuration = expectedMaxDuration;
    }
}

/// <summary>
/// XCP 归因面板 VM（S3-T6，spec D5 合并归因表逐格落地）。
/// <para>
/// 条目接力：T5 <see cref="XcpCardPanelViewModel"/> drain 后经
/// <see cref="XcpCardPanelViewModel.GapObserved"/> 事件旁路进本面板
///（<see cref="Attach"/> 订阅）——本面板 <b>不另开 sink drain</b>
///（重复消费即丢条）。host 两态不经 Gap 通道，由 T3/T5 生产者经
/// <see cref="ObserveHostAttribution"/> 直喂。
/// </para>
/// <para>
/// 卡片格归因查询（<see cref="GetAttributionFor"/>）：对象名 → 当前归因格。
/// <see cref="XcpAcquisitionGap"/> 没有对象标识字段，因此包侧归因不做对象名映射
///（不造字段），统一显示为全局格：host 格激活时优先，否则回落最近一条 gap 格。
/// </para>
/// </summary>
public partial class XcpAttributionPanelViewModel : ObservableObject
{
    private readonly Dictionary<XcpHostAttributionCell, XcpAttributionCellViewModel> _cellsByHost = new();
    private readonly Dictionary<MissingCause, XcpAttributionCellViewModel> _cellsByCause = new();
    private readonly Dictionary<XcpReceiveAttributionKind, XcpAttributionCellViewModel> _cellsByReceiveKind = new();
    private readonly XcpAttributionCellViewModel _interruptedCell;
    private XcpAttributionCellViewModel? _lastGapCell;
    private XcpCardPanelViewModel? _attachedCardPanel;

    /// <summary>可空注入构造（保既有 VM 测试构造模式；注入即完成 T5 事件订阅）。</summary>
    /// <param name="cardPanel">T5 卡片面板；注入后自动订阅 GapObserved 接力（T8 接线同款）。</param>
    public XcpAttributionPanelViewModel(XcpCardPanelViewModel? cardPanel = null)
    {
        // D5 表顺序建格（视图绑定顺序即表顺序）。
        var unconnected = new XcpAttributionCellViewModel(
            XcpAttributionCellKind.UnconnectedBus, "未连总线");
        var filtered = new XcpAttributionCellViewModel(
            XcpAttributionCellKind.FilteredOut, "被过滤");
        _cellsByHost[XcpHostAttributionCell.UnconnectedBus] = unconnected;
        _cellsByHost[XcpHostAttributionCell.FilteredOut] = filtered;

        // 包侧五值（A2lEditor MissingCause 六值中的前五；AcquisitionInterrupted
        // 槽位由 Receive 层升级生产，独立格，见 spec D5）。
        foreach (var cause in new[]
                 {
                     MissingCause.NotAcquired,
                     MissingCause.SegmentMissing,
                     MissingCause.ConversionUnsupported,
                     MissingCause.AccessBlocked,
                     MissingCause.AccessInferred,
                 })
        {
            _cellsByCause[cause] = new XcpAttributionCellViewModel(
                XcpAttributionCellKind.PackageMissingCause, cause.ToString(), missingCause: cause);
        }

        _interruptedCell = new XcpAttributionCellViewModel(
            XcpAttributionCellKind.AcquisitionInterrupted, "AcquisitionInterrupted");

        foreach (var kind in Enum.GetValues<XcpReceiveAttributionKind>())
        {
            _cellsByReceiveKind[kind] = new XcpAttributionCellViewModel(
                XcpAttributionCellKind.FrameAttribution, kind.ToString(), receiveKind: kind);
        }

        PlanGapCell = new XcpAttributionCellViewModel(
            XcpAttributionCellKind.PlanGap, "计划空窗（换表中）");

        Cells = new List<XcpAttributionCellViewModel>
        {
            unconnected,
            filtered,
        }
        .Concat(_cellsByCause.Values)
        .Append(_interruptedCell)
        .Concat(_cellsByReceiveKind.Values)
        .Append(PlanGapCell)
        .ToList();

        if (cardPanel is not null)
            Attach(cardPanel);
    }

    /// <summary>D5 表全部格（视图渲染顺序 = 表顺序）。</summary>
    public IReadOnlyList<XcpAttributionCellViewModel> Cells { get; }

    /// <summary>
    /// 当前生效的 host 格（null = Connected 态，host 格退场，包侧 Receive 归因接管——
    /// T3 <see cref="XcpConnectionPanelViewModel.AttributionCell"/> 同口径）。
    /// </summary>
    [ObservableProperty]
    private XcpHostAttributionCell? _activeHostCell;

    /// <summary>断流格（独立于包侧五值格；Cause 槽位同值但不落那格）。</summary>
    public XcpAttributionCellViewModel AcquisitionInterruptedCell => _interruptedCell;

    /// <summary>计划空窗格（最近一条显示 ExpectedMaxDuration）。</summary>
    public XcpAttributionCellViewModel PlanGapCell { get; }

    /// <summary>host 格查询（T3/T5 直喂目标）。</summary>
    public XcpAttributionCellViewModel CellFor(XcpHostAttributionCell cell) => _cellsByHost[cell];

    /// <summary>包侧五值格查询；AcquisitionInterrupted 槽位无格（走独立格），返回 null。</summary>
    public XcpAttributionCellViewModel? CellFor(MissingCause cause) =>
        _cellsByCause.GetValueOrDefault(cause);

    /// <summary>逐帧归因格查询。</summary>
    public XcpAttributionCellViewModel? CellFor(XcpReceiveAttributionKind kind) =>
        _cellsByReceiveKind.GetValueOrDefault(kind);

    /// <summary>
    /// host 两态直喂（生产者：T3 连接状态机 / T5 关注集筛选）。
    /// <paramref name="cell"/> 为 null 表示 Connected 态 host 格退场——计数保留。
    /// </summary>
    public void ObserveHostAttribution(XcpHostAttributionCell? cell, string? detail = null)
    {
        ActiveHostCell = cell;
        if (cell is not { } c)
            return;

        _cellsByHost[c].Record(detail ?? DefaultHostDetail(c));
    }

    /// <summary>
    /// 一条空窗/断流归因进面板（T5 <c>GapObserved</c> 事件旁路的消费口；也可单测直喂）。
    /// 按 D5 表映射到唯一格，无法映射的条目组合抛
    /// <see cref="InvalidOperationException"/>（无第四类黑盒串接）。
    /// </summary>
    public void ObserveGap(XcpAcquisitionGap gap)
    {
        ArgumentNullException.ThrowIfNull(gap);

        var cell = gap.Kind switch
        {
            XcpAcquisitionGapKind.MissingCauseAttributed => CellForPackageCause(gap),
            XcpAcquisitionGapKind.AcquisitionInterrupted => _interruptedCell,
            XcpAcquisitionGapKind.ReceiveAttribution => CellForReceiveKind(gap),
            XcpAcquisitionGapKind.PlanGapOpened => PlanGapCell,
            _ => throw new InvalidOperationException(
                $"未知的 XcpAcquisitionGapKind: {gap.Kind}（D5 表无对应格，拒绝黑盒串接）"),
        };

        cell.Record(gap.Detail, gap.ExpectedMaxDuration);
        _lastGapCell = cell;
    }

    /// <summary>
    /// 卡片格归因查询：对象名 → 当前归因格（供卡片消费的形状）。
    /// Gap 无对象标识字段，不造对象名映射——统一全局格：host 格激活时优先，
    /// 否则回落最近一条 gap 格（尚无任何条目则 null）。
    /// </summary>
    public XcpAttributionCellViewModel? GetAttributionFor(string objectName)
    {
        ArgumentNullException.ThrowIfNull(objectName);

        return ActiveHostCell is { } host
            ? _cellsByHost[host]
            : _lastGapCell;
    }

    /// <summary>
    /// 订阅 T5 卡片面板的 GapObserved 接力（不另开 sink drain）。
    /// 同一面板重复调用幂等；换面板抛异常——多生产者接力 = 重复消费即丢条。
    /// </summary>
    public void Attach(XcpCardPanelViewModel cardPanel)
    {
        ArgumentNullException.ThrowIfNull(cardPanel);
        if (ReferenceEquals(_attachedCardPanel, cardPanel))
            return;

        if (_attachedCardPanel is not null)
            throw new InvalidOperationException(
                "归因面板已订阅另一 XcpCardPanelViewModel 的 GapObserved——切换生产者会造成接力断链/重复消费，fail loud。");

        cardPanel.GapObserved += ObserveGap;
        _attachedCardPanel = cardPanel;
    }

    private XcpAttributionCellViewModel CellForPackageCause(XcpAcquisitionGap gap) =>
        gap.Cause is { } cause && _cellsByCause.TryGetValue(cause, out var cell)
            ? cell
            : throw new InvalidOperationException(
                $"MissingCauseAttributed 条目的 Cause 无效: {gap.Cause?.ToString() ?? "<null>"}（D5 表包侧五值之外不收）");

    private XcpAttributionCellViewModel CellForReceiveKind(XcpAcquisitionGap gap) =>
        gap.ReceiveKind is { } kind && _cellsByReceiveKind.TryGetValue(kind, out var cell)
            ? cell
            : throw new InvalidOperationException(
                $"ReceiveAttribution 条目的 ReceiveKind 无效: {gap.ReceiveKind?.ToString() ?? "<null>"}（逐帧归因必须携带帧级 Kind，M-3）");

    private static string DefaultHostDetail(XcpHostAttributionCell cell) => cell switch
    {
        XcpHostAttributionCell.UnconnectedBus => "ConnectionState != Connected（T3 连接状态机）",
        XcpHostAttributionCell.FilteredOut => "对象不在关注集显示范围（T5 关注集筛选）",
        _ => cell.ToString(),
    };
}
