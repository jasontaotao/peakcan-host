using System.Collections.ObjectModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.HIL.Core;
using PeakCan.Host.App.Services;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// 2026-08-31 P1/P2：视图层过滤状态。核心是 <see cref="EntriesView"/>
/// （<see cref="ListCollectionView"/>，非破坏性）——不匹配帧仍留在
/// <see cref="Entries"/>，改过滤可找回。过滤字段任一变更 → <see cref="TryRebuildSpec"/>
/// 组装新 <see cref="TraceFilterSpec"/> 并置换 <c>EntriesView.Filter</c>。
/// </summary>
public sealed partial class TraceViewModel
{
    /// <summary>
    /// Trace 行的视图层过滤视图（非破坏性）。DataGrid 绑定此对象而非
    /// <see cref="Entries"/>；过滤谓词为 <see cref="TraceFilterSpec.Matches"/>。
    /// ctor 与 <see cref="Entries"/> 同线程创建（VM 单例、UI 线程解析；测试单线程 MTA 直构）。
    /// </summary>
    public ListCollectionView EntriesView { get; }

    /// <summary>DBC 服务（经 <see cref="BindDbc"/> 属性注入，规避 DI 循环；未绑=null 降级）。</summary>
    private DbcService? _dbcService;

    // —— 过滤字段（UI 文本 / 开关，参与 spec 构建）——

    /// <summary>ID allow-list 文本（无前缀十进制 / 0x=hex，同 Replay/Viewer）。</summary>
    [ObservableProperty]
    private string _idListText = "";

    /// <summary>PGN 列表文本（hex，0x 可选）。</summary>
    [ObservableProperty]
    private string _pgnText = "";

    /// <summary>SA 单字节文本（hex）。空=不过滤。</summary>
    [ObservableProperty]
    private string _saText = "";

    /// <summary>DA 单字节文本（hex）。空=不过滤。</summary>
    [ObservableProperty]
    private string _daText = "";

    /// <summary>DBC 消息名（可编辑 ComboBox，提交时解析）。</summary>
    [ObservableProperty]
    private string _dbcMessageName = "";

    /// <summary>整体取反（黑名单语义）。</summary>
    [ObservableProperty]
    private bool _excludeMatch;

    /// <summary>payload offset 文本（十进制）。</summary>
    [ObservableProperty]
    private string _payloadOffsetText = "";

    /// <summary>payload mask 文本（hex）。</summary>
    [ObservableProperty]
    private string _payloadMaskHex = "";

    /// <summary>payload value 文本（hex）。</summary>
    [ObservableProperty]
    private string _payloadValueHex = "";

    /// <summary>过滤字段错误文本（红字直显首个错误）；null=无错。</summary>
    [ObservableProperty]
    private string? _filterErrorText;

    /// <summary>DBC 消息名下拉投影（来自 <see cref="_dbcService"/>）。</summary>
    public ObservableCollection<string> DbcMessageNames { get; } = new();

    /// <summary>当前生效的过滤规范（解析成功的最新 spec；非法时沿用上一有效）。</summary>
    private TraceFilterSpec _activeSpec = TraceFilterSpec.Empty;

    partial void OnIdListTextChanged(string value) => TryRebuildSpec();
    partial void OnPgnTextChanged(string value) => TryRebuildSpec();
    partial void OnSaTextChanged(string value) => TryRebuildSpec();
    partial void OnDaTextChanged(string value) => TryRebuildSpec();
    partial void OnDbcMessageNameChanged(string value) => TryRebuildSpec();
    partial void OnExcludeMatchChanged(bool value) => TryRebuildSpec();
    partial void OnShowErrorsOnlyChanged(bool value) => TryRebuildSpec();
    partial void OnChannelFilterChanged(ChannelId? value) => TryRebuildSpec();
    partial void OnPayloadOffsetTextChanged(string value) => TryRebuildSpec();
    partial void OnPayloadMaskHexChanged(string value) => TryRebuildSpec();
    partial void OnPayloadValueHexChanged(string value) => TryRebuildSpec();

    /// <summary>
    /// 解析各字段 → 组装新 <see cref="TraceFilterSpec"/> 并置换 <c>EntriesView.Filter</c>。
    /// 任一字段非法 → <see cref="FilterErrorText"/> 红字 + 沿用上一有效 spec（不置换 view，
    /// 防"敲错 PGN 静默放宽成全显"）。合法 → 置换 + Refresh + 清错 + 状态文本。
    /// </summary>
    private void TryRebuildSpec()
    {
        var (spec, error) = TraceFilterParser.TryParse(
            IdListText, PgnText, SaText, DaText, DbcMessageName,
            _dbcService?.Current, PayloadOffsetText, PayloadMaskHex, PayloadValueHex);
        if (error is not null)
        {
            FilterErrorText = error;
            return; // 沿用 _activeSpec，不动 EntriesView。
        }

        // 文本字段由 parser 解析；Channel/ErrorsOnly/Exclude 是独立 VM 属性（spec §9
        // "并入 spec"），在此 merge 进最终 spec。
        _activeSpec = (spec ?? TraceFilterSpec.Empty) with
        {
            Channel = ChannelFilter,
            ErrorsOnly = ShowErrorsOnly,
            Exclude = ExcludeMatch,
        };
        FilterErrorText = null;
        // ListCollectionView.Filter 是 Predicate<object>，需包一层（Entries 元素都是 TraceEntry）。
        EntriesView.Filter = _activeSpec.IsEmpty
            ? null
            : o => o is TraceEntry e && _activeSpec.Matches(e);
        EntriesView.Refresh();
        UpdateStatusText();
    }

    /// <summary>清空全部过滤字段 + 重置为全显。</summary>
    [RelayCommand]
    private void ClearFilters()
    {
        IdListText = "";
        PgnText = "";
        SaText = "";
        DaText = "";
        DbcMessageName = "";
        ExcludeMatch = false;
        PayloadOffsetText = "";
        PayloadMaskHex = "";
        PayloadValueHex = "";
        FilterErrorText = null;
        _activeSpec = TraceFilterSpec.Empty;
        EntriesView.Filter = null;
        EntriesView.Refresh();
        UpdateStatusText();
    }

    // —— 高亮 / 统计 / 状态（后续任务填充，先编译占位）——

    /// <summary>统计面板展开状态（底栏 Expander 双向绑定）。</summary>
    [ObservableProperty]
    private bool _statsExpanded;

    /// <summary>
    /// 对新入列行求高亮色索引（0..5，-1=无）。规则求值见 T6（
    /// <c>HighlightRuleRowViewModel</c>）；本期占位恒返回 -1，T6 接上真实逻辑。
    /// </summary>
    private int EvaluateHighlight(TraceEntry entry) => -1;

    /// <summary>统计面板刷新（T11 填充：Top20 重建 <see cref="StatsRows"/>）。</summary>
    private void RefreshStats()
    {
    }

    /// <summary>状态文本：`显示 X / 共 Y（上限 Z）｜总收 N`（spec §5.8）。</summary>
    [ObservableProperty]
    private string _statusText = "";

    private void UpdateStatusText()
        => StatusText = $"显示 {EntriesView.Count} / 共 {Entries.Count}（上限 {MaxRows}）｜总收 {TotalFrameCount}";
}
