using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using A2lEditor.Core.Layout;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.Services.Xcp;
using PeakCan.Host.Core.Xcp.Calibration;
using PeakCan.Host.Core.Xcp.Receive;

namespace PeakCan.Host.App.ViewModels.Xcp;

/// <summary>卡片限值状态（S1 §5.5 越界变色；判定只读 ValueContract 解析期限值字段）。</summary>
public enum XcpCardLimitState
{
    /// <summary>限值内（含临界值等值——闭区间口径）。</summary>
    Normal,

    /// <summary>越常规上下限（LowerLimit/UpperLimit）。</summary>
    OutOfLimit,

    /// <summary>越扩展上下限（ExtLowerLimit/ExtUpperLimit）。</summary>
    OutOfExtendedLimit,
}

/// <summary>
/// 单张卡片（S1 §5.5 卡片格）：名称/值/单位/格式化显示/限值状态 + 停更标灰。
/// <para>
/// 字段来源钉死（spec §1 断链判据）：值与接收时刻只来自
/// <see cref="XcpDaqSample"/>（S2 T16 解码产物）；单位/格式/限值只读
/// <see cref="ValueContract"/> 解析期字段——本类不做任何 A2L 现算/现解读。
/// </para>
/// </summary>
public sealed class XcpCardViewModel : ObservableObject
{
    /// <summary>A2L FORMAT 是 C printf 风格（"%.2f"/"%6.2"/"%8.3e"）；host 只取精度语义，
    /// 用 .NET 标准数字格式渲染（不引入 printf 依赖）。结构不匹配（无数字部分）时
    /// 回退不变文化 G；未识别的字母后缀按 F 精度渲染（T5 评审 LOW：正则宽松，注释原
    /// 称"无法识别回退 G"与实现不符，已如实更正）。</summary>
    private static readonly Regex FormatPattern =
        new(@"^%?(\d+)?(?:\.(\d+))?([a-zA-Z])?$", RegexOptions.Compiled);

    private DateTimeOffset? _lastUpdate;

    public XcpCardViewModel(string name, string category, ValueContract contract)
    {
        Name = name;
        Category = category;
        Contract = contract;
    }

    /// <summary>对象名（关注集条目身份，去重键）。</summary>
    public string Name { get; }

    /// <summary>对象类别（来自 XcpWatchRow，如 MEASUREMENT / CHARACTERISTIC）。</summary>
    public string Category { get; }

    /// <summary>解析期合同实例（引用身份 = 不重建不重解读；显示字段只读它的解析期字段）。</summary>
    public ValueContract Contract { get; }

    /// <summary>单位（ValueContract.Unit，解析期）。</summary>
    public string? Unit => Contract.Unit;

    /// <summary>格式化后的值文本（ValueContract.Format，解析期）；未收到样本为 "—"。</summary>
    public string DisplayValue => _displayValue;

    private string _displayValue = "—";

    /// <summary>限值状态（越限判定读合同解析期限值字段，禁现算量程）。</summary>
    public XcpCardLimitState LimitState => _limitState;

    private XcpCardLimitState _limitState = XcpCardLimitState.Normal;

    /// <summary>停更标灰（最后一次更新距今 ≥ 3 × 采集周期）。</summary>
    public bool IsStale => _isStale;

    private bool _isStale;

    /// <summary>停更时长人读文本（停更 = "停更 30 ms"；新鲜 = "刚刚更新"；未收到数据 = ""）。</summary>
    public string StaleDuration => _staleDuration;

    private string _staleDuration = string.Empty;

    /// <summary>最后一次样本的主机接收时刻（XcpDaqSample.ReceivedAt；未收到数据为 null）。</summary>
    public DateTimeOffset? LastUpdate => _lastUpdate;

    /// <summary>样本落地（Flush 批量驱动；值/时刻只来自样本，格式/单位/限值只来自合同）。</summary>
    internal void Update(XcpDaqSample sample)
    {
        _displayValue = FormatValue(Contract.Format, sample.Value);
        OnPropertyChanged(nameof(DisplayValue));

        _limitState = LimitStateOf(Contract, sample.Value);
        OnPropertyChanged(nameof(LimitState));

        _lastNumericValue = sample.Value; // S5-T4：写值/参数集导出需要数值面（DisplayValue 是格式化文本）
        OnPropertyChanged(nameof(LastNumericValue));

        _lastUpdate = sample.ReceivedAt;
        OnPropertyChanged(nameof(LastUpdate)); // T5 评审 MEDIUM：绑定属性必须随更新通知（原漏报）
    }

    // ===== S5-T4（spec D5）：标定写值入口（组合根经面板 AttachWriteback 接线） =====

    /// <summary>最近一次样本数值（写值/参数集导出的数值面；DisplayValue 是格式化文本）。</summary>
    public double? LastNumericValue => _lastNumericValue;

    private double? _lastNumericValue;

    /// <summary>写回处理器（组合根经面板 AttachWriteback 接线；null = 未接线，写按钮禁用）。</summary>
    public Func<XcpCardViewModel, double, Task<CalibrationWriteOutcome>>? WriteHandler { get; set; }

    /// <summary>是否可写（CHARACTERISTIC 卡片且写回已接线）。</summary>
    public bool CanWrite =>
        string.Equals(Category, "CHARACTERISTIC", StringComparison.OrdinalIgnoreCase) && WriteHandler is not null;

    /// <summary>写入物理值文本（不变文化解析）。</summary>
    public string? WriteValueText
    {
        get => _writeValueText;
        set
        {
            if (_writeValueText == value)
                return;
            _writeValueText = value;
            OnPropertyChanged();
            WriteValueCommand.NotifyCanExecuteChanged();
        }
    }

    private string? _writeValueText;

    /// <summary>最近一次写值结果（人读状态行）。</summary>
    public string? LastWriteStatus
    {
        get => _lastWriteStatus;
        private set
        {
            if (_lastWriteStatus == value)
                return;
            _lastWriteStatus = value;
            OnPropertyChanged();
        }
    }

    private string? _lastWriteStatus;

    private bool CanWriteValue() =>
        CanWrite && double.TryParse(WriteValueText, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out _);

    /// <summary>D5 行内写值命令（写 + 回读 + 状态行）。</summary>
    public RelayCommand WriteValueCommand => _writeValueCommand ??= new RelayCommand(WriteValueAsync, CanWriteValue);

    private RelayCommand? _writeValueCommand;

    private async void WriteValueAsync()
    {
        if (WriteHandler is null
            || !double.TryParse(WriteValueText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return;
        }

        var outcome = await WriteHandler(this, value);
        LastWriteStatus = $"{outcome.Status}：{outcome.Detail}";
    }

    /// <summary>停更刷新（Flush 每拍重算"距今多久"，计时经 VM 注入的 TimeProvider）。</summary>
    internal void RefreshStaleness(DateTimeOffset now, TimeSpan staleThreshold)
    {
        if (_lastUpdate is not { } last)
        {
            // 未收到过数据：不算"停更"（采集未覆盖的归因走 D5 表，不冒充停更语义）。
            _isStale = false;
            _staleDuration = string.Empty;
        }
        else
        {
            var elapsed = now - last;
            _isStale = elapsed >= staleThreshold;
            _staleDuration = _isStale ? $"停更 {FormatDuration(elapsed)}" : "刚刚更新";
        }

        OnPropertyChanged(nameof(IsStale));
        OnPropertyChanged(nameof(StaleDuration));
    }

    /// <summary>越限判定：扩展限优先；null 侧不参与比较；等值不算越限（闭区间）。</summary>
    private static XcpCardLimitState LimitStateOf(ValueContract contract, double value)
    {
        if ((contract.ExtLowerLimit is { } extLower && value < extLower)
            || (contract.ExtUpperLimit is { } extUpper && value > extUpper))
            return XcpCardLimitState.OutOfExtendedLimit;

        if ((contract.LowerLimit is { } lower && value < lower)
            || (contract.UpperLimit is { } upper && value > upper))
            return XcpCardLimitState.OutOfLimit;

        return XcpCardLimitState.Normal;
    }

    private static string FormatValue(string? format, double value)
    {
        if (string.IsNullOrWhiteSpace(format))
            return value.ToString("G", CultureInfo.InvariantCulture);

        var match = FormatPattern.Match(format.Trim());
        if (!match.Success)
            return value.ToString("G", CultureInfo.InvariantCulture);

        var precision = match.Groups[2].Success
            ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)
            : (int?)null;
        var letter = match.Groups[3].Success
            ? char.ToLowerInvariant(match.Groups[3].Value[0])
            : (char?)null;

        return letter switch
        {
            'e' => value.ToString($"E{precision ?? 6}", CultureInfo.InvariantCulture),
            'g' => value.ToString($"G{precision ?? 15}", CultureInfo.InvariantCulture),
            // A2L 惯例 "%6.2"（无类型字母）与 "%..f" 同义；整型字母按 0 位小数渲染。
            'd' or 'i' or 'u' => value.ToString("F0", CultureInfo.InvariantCulture),
            _ => value.ToString(precision is null ? "F" : $"F{precision}", CultureInfo.InvariantCulture),
        };
    }

    private static string FormatDuration(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.FromSeconds(1))
            return $"{elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)} ms";
        if (elapsed < TimeSpan.FromMinutes(1))
            return $"{elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} s";
        return $"{elapsed.TotalMinutes.ToString("F1", CultureInfo.InvariantCulture)} min";
    }
}

/// <summary>
/// XCP 卡片面板 VM（S3-T5，spec D3 + S1 §5.5）：关注集卡片格 + 20 Hz 批量 flush。
/// <para>
/// 本 VM 不持有 Dispatcher：20 Hz 节拍由视图层 DispatcherTimer 驱动 <see cref="Flush"/>，
/// 停更计时经注入 <see cref="TimeProvider"/>（默认 <see cref="TimeProvider.System"/>），可测。
/// 卡片值只来自 <see cref="XcpDaqSample"/>；单位/格式/限值只来自
/// <see cref="ValueContract"/> 解析期字段（spec §1 禁现算量程）。
/// </para>
/// </summary>
public partial class XcpCardPanelViewModel : ObservableObject
{
    /// <summary>停更判定的采集周期默认时长（ms，对齐从机 100 Hz 节拍；周期时长参数化）。</summary>
    public const int DefaultStalePeriodMilliseconds = 10;

    /// <summary>停更阈值 = 连续无更新 <see cref="StaleCycles"/> 个采集周期（S1 §5.5）。</summary>
    public const int StaleCycles = 3;

    private readonly XcpCardPanelSink _sink;
    private readonly TimeProvider _time;
    private readonly TimeSpan _stalePeriod;
    private readonly Dictionary<string, XcpCardViewModel> _cardsByName = new(StringComparer.Ordinal);

    /// <summary>
    /// 可空注入构造（保既有 VM 测试构造模式）。
    /// </summary>
    /// <param name="sink">D3 管线 sink；缺省自建（T8 接线时注入共享实例）。</param>
    /// <param name="timeProvider">停更计时来源；缺省系统时钟。</param>
    /// <param name="stalePeriod">采集周期时长；缺省 10 ms。</param>
    public XcpCardPanelViewModel(
        XcpCardPanelSink? sink = null,
        TimeProvider? timeProvider = null,
        TimeSpan? stalePeriod = null)
    {
        _sink = sink ?? new XcpCardPanelSink();
        _time = timeProvider ?? TimeProvider.System;
        var effective = stalePeriod ?? TimeSpan.FromMilliseconds(DefaultStalePeriodMilliseconds);
        if (effective <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(stalePeriod), effective, "采集周期必须为正——0/负值会让全部卡片永久停更（T5 评审 LOW）。");
        _stalePeriod = effective;
    }

    /// <summary>D3 管线 sink（视图层接线用同一实例灌样本；sink 归 App 层自持）。</summary>
    public XcpCardPanelSink Sink => _sink;

    /// <summary>卡片格（关注集条目，AddWatch/RemoveWatch 动态增删）。</summary>
    public ObservableCollection<XcpCardViewModel> Cards { get; } = new();

    /// <summary>sink DropOldest 丢条计数（丢弃必须可见，不静默；随 Flush 同步）。</summary>
    [ObservableProperty]
    private long _droppedCount;

    /// <summary>已 drain 的空窗/断流归因条数（OnGap 同队列旁路；不静默）。</summary>
    [ObservableProperty]
    private long _gapCount;

    /// <summary>最近一条归因明细（人读文本；归因面板 T6 的明细入口）。</summary>
    [ObservableProperty]
    private string? _lastGapDetail;

    /// <summary>
    /// drain 到的空窗/断流归因旁路出口（D3：OnGap 同队列旁路进归因通道——
    /// 归因面板 T6 订阅此事件接力，不重复 drain 同一队列）。
    /// </summary>
    public event Action<XcpAcquisitionGap>? GapObserved;

    /// <summary>关注集加入一个对象（按对象名去重；合同必须来自解析期 ContractSet）。</summary>
    // S5-T4：写回处理器（AttachWriteback 接线；新增卡片自动带上）。
    private Func<XcpCardViewModel, double, Task<CalibrationWriteOutcome>>? _writeHandler;

    /// <summary>S5-T4（spec D5）：接线/解除卡片写回处理器（null = 解除）。</summary>
    public void AttachWriteback(Func<XcpCardViewModel, double, Task<CalibrationWriteOutcome>>? handler)
    {
        _writeHandler = handler;
        foreach (var card in Cards)
            card.WriteHandler = handler;
    }

    public void AddWatch(string name, string category, ValueContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (_cardsByName.ContainsKey(name))
            return;

        var card = new XcpCardViewModel(name, category, contract)
        {
            WriteHandler = _writeHandler, // S5-T4：新增卡片带上已接线的写回处理器
        };
        _cardsByName.Add(name, card);
        Cards.Add(card);
    }

    /// <summary>关注集移除一个对象；之后同对象样本不再渲染（采集集不受影响）。</summary>
    public bool RemoveWatch(string name)
    {
        if (!_cardsByName.Remove(name, out var card))
            return false;

        Cards.Remove(card);
        return true;
    }

    /// <summary>
    /// 20 Hz 批量 flush（视图层 DispatcherTimer 驱动）：批量 drain → 更新卡片 →
    /// 全卡重算停更（耗时经注入 TimeProvider，非本机随手取时）。
    /// </summary>
    public void Flush()
    {
        foreach (var entry in _sink.Drain())
        {
            switch (entry)
            {
                case { Kind: XcpCardPanelEntryKind.Value, Sample: { } sample }:
                    // 显示集与采集集分离（spec §1）：非关注对象样本只路过，不渲染。
                    if (_cardsByName.TryGetValue(sample.Entry.ObjectName, out var card))
                        card.Update(sample);
                    break;

                case { Kind: XcpCardPanelEntryKind.Gap, Gap: { } gap }:
                    GapCount++;
                    LastGapDetail = gap.Detail;
                    GapObserved?.Invoke(gap);
                    break;
            }
        }

        DroppedCount = _sink.DroppedCount;
        RefreshStaleness();
    }

    private void RefreshStaleness()
    {
        var now = _time.GetUtcNow();
        var threshold = TimeSpan.FromTicks(_stalePeriod.Ticks * StaleCycles);
        foreach (var card in _cardsByName.Values)
            card.RefreshStaleness(now, threshold);
    }
}
