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

    /// <summary>DBC 服务（P1-2 2026-09-06：改 <see cref="TraceViewModel(DbcService?)"/> ctor 注入；null 降级）。</summary>
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

    /// <summary>
    /// 行右键"排除这个 ID"的绝对排除集合。独立于 <see cref="ExcludeMatch"/>
    /// 的整体取反——排除命中在取反前判 false，其他过滤条件不受影响。
    /// </summary>
    private readonly HashSet<uint> _excludedIds = new();

    /// <summary>排除 ID 的显示文本（与界面 ID 列同格式），供 chip 与状态提示使用。</summary>
    private readonly Dictionary<uint, string> _excludedIdLabels = new();

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
            // chip 仍反映用户已输入的原始 token，便于就地删除非法项。
            RebuildFilterChips();
            return; // 沿用 _activeSpec，不动 EntriesView。
        }

        // 文本字段由 parser 解析；Channel/ErrorsOnly/Exclude 是独立 VM 属性（spec §9
        // "并入 spec"），在此 merge 进最终 spec。
        _activeSpec = (spec ?? TraceFilterSpec.Empty) with
        {
            Channel = ChannelFilter,
            ErrorsOnly = ShowErrorsOnly,
            Exclude = ExcludeMatch,
            // 复制一份：spec 语义不可变，集合可能被后续右键继续追加。
            ExcludedIds = _excludedIds.Count == 0 ? null : _excludedIds.ToHashSet(),
        };
        FilterErrorText = null;
        ApplyActiveSpec();
    }

    /// <summary>
    /// 把当前 <see cref="_activeSpec"/> 应用到视图并刷新状态文本。
    /// 正常路径由 <see cref="TryRebuildSpec"/> 调用；过滤文本非法时，
    /// 行右键的排除操作也用它直接应用（parser 早退不会重建 spec）。
    /// </summary>
    private void ApplyActiveSpec()
    {
        // ListCollectionView.Filter 是 Predicate<object>，需包一层（Entries 元素都是 TraceEntry）。
        EntriesView.Filter = _activeSpec.IsEmpty
            ? null
            : o => o is TraceEntry e && _activeSpec.Matches(e);
        EntriesView.Refresh();
        UpdateStatusText();
        RebuildFilterChips();
    }

    /// <summary>
    /// 过滤文本处于非法态时，<see cref="TryRebuildSpec"/> 会早退并沿用旧 spec
    /// （旧排除集仍在），导致"排除/清除排除"看起来无效。这里只更新 spec 的
    /// <see cref="TraceFilterSpec.ExcludedIds"/> 维度并直接应用，不触碰文本字段。
    /// </summary>
    private void ApplyExclusionChange()
    {
        if (FilterErrorText is null)
        {
            TryRebuildSpec();
            return;
        }

        _activeSpec = _activeSpec with
        {
            ExcludedIds = _excludedIds.Count == 0 ? null : _excludedIds.ToHashSet(),
        };
        ApplyActiveSpec();
    }

    /// <summary>清空全部过滤字段 + 重置为全显（含并入 spec 的通道/仅错误，review 2026-08-31）。</summary>
    [RelayCommand]
    private void ClearFilters()
    {
        IdListText = "";
        PgnText = "";
        SaText = "";
        DaText = "";
        DbcMessageName = "";
        ExcludeMatch = false;
        // 并入 spec 的两个独立 VM 属性也必须重置——否则下次编辑任一文本字段时
        // TryRebuildSpec 重新 merge 旧值，"已清除"的通道/仅错误条件静默复活。
        ShowErrorsOnly = false;
        ChannelFilter = null;
        PayloadOffsetText = "";
        PayloadMaskHex = "";
        PayloadValueHex = "";
        _excludedIds.Clear();
        _excludedIdLabels.Clear();
        FilterErrorText = null;
        _activeSpec = TraceFilterSpec.Empty;
        ApplyActiveSpec();
    }

    // —— 高亮 / 统计 / 状态（后续任务填充，先编译占位）——

    /// <summary>统计面板展开状态（底栏 Expander 双向绑定）。</summary>
    [ObservableProperty]
    private bool _statsExpanded;

    // —— MaxRows 可调输入（spec §5.8）——

    /// <summary>MaxRows 文本（TwoWay 绑定工具栏输入框），初始同步当前 <see cref="MaxRows"/>。</summary>
    [ObservableProperty]
    private string _maxRowsText = "5000";

    /// <summary>MaxRows 非法时红字；null=无错。</summary>
    [ObservableProperty]
    private string? _maxRowsErrorText;

    /// <summary>
    /// <see cref="MaxRowsText"/> 变更：解析成功且 ∈ [100, 50000] → 应用 <see cref="MaxRows"/>；
    /// 非法 → <see cref="MaxRowsErrorText"/> 红字 + <see cref="MaxRows"/> 不变 + 文本回退旧值。
    /// trim 在批次末按新值生效（调低后下一批次截断）。
    /// </summary>
    partial void OnMaxRowsTextChanged(string value)
    {
        if (int.TryParse(value, out var parsed) && parsed is >= 100 and <= 50000)
        {
            MaxRows = parsed;
            MaxRowsErrorText = null;
            UpdateStatusText();
        }
        else
        {
            MaxRowsErrorText = $"MaxRows 需在 [100, 50000] 之间";
            // 直接写 backing 字段回退旧值，避免重入 OnMaxRowsTextChanged 清掉刚设的错误；
            // 再手动 OnPropertyChanged 通知 UI 刷新文本框。
            _maxRowsText = MaxRows.ToString();
            OnPropertyChanged(nameof(MaxRowsText));
        }
    }

    /// <summary>状态文本：`显示 X / 共 Y（上限 Z）｜总收 N`；有排除时追加 `｜已排除 M 个 ID`（spec §5.8）。</summary>
    [ObservableProperty]
    private string _statusText = "";

    private void UpdateStatusText()
    {
        // 排除集合是隐藏状态：在状态栏显式给出数量，避免"行莫名消失"不可解释。
        var excludedSuffix = _excludedIds.Count == 0
            ? ""
            : $"｜已排除 {_excludedIds.Count} 个 ID";
        StatusText =
            $"显示 {EntriesView.Count} / 共 {Entries.Count}（上限 {MaxRows}）｜总收 {TotalFrameCount}{excludedSuffix}";
    }
}
