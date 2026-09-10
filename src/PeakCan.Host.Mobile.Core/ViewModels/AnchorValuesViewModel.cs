using CommunityToolkit.Mvvm.ComponentModel;
using PeakCan.Host.Mobile.Core.Models;
using PeakCan.Host.Mobile.Core.Platform;
using PeakCan.Host.Mobile.Core.Services;

namespace PeakCan.Host.Mobile.Core.ViewModels;

/// <summary>锚点值面板一行：一个解码信号；无 DBC 时一帧一行（SignalName 空、ValueText 为 hex data）。</summary>
public sealed record AnchorValueRow(string MessageName, string SignalName, string ValueText, string Unit);

/// <summary>
/// 锚点时刻的全信号取值面板数据。打开面板时实时查询缓存（zero-order hold 语义，
/// 每 CAN ID 取 t 前最后一帧）+ DBC 解码；加载完成前 Rows 为空。一次性加载，
/// 非高频路径，查询结果整体替换后发布变更。
/// </summary>
public sealed partial class AnchorValuesViewModel : ObservableObject
{
    private readonly ITraceCacheStore _cache;
    private readonly long _traceId;
    private readonly DbcCatalog? _dbc;
    private readonly IUiDispatcher _ui;
    private IReadOnlyList<AnchorValueRow> _rows = [];

    public AnchorValuesViewModel(ITraceCacheStore cache, long traceId, DbcCatalog? dbc, IUiDispatcher ui)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _traceId = traceId;
        _dbc = dbc;
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
    }

    /// <summary>解码结果行；加载完成前为空。</summary>
    public IReadOnlyList<AnchorValueRow> Rows
    {
        get => _rows;
        private set
        {
            if (ReferenceEquals(_rows, value)) return;
            _rows = value;
            OnPropertyChanged();
        }
    }

    /// <summary>加载完成且无行（锚点落在未缓存区域）。</summary>
    [ObservableProperty]
    private bool _isEmpty;

    /// <summary>
    /// 查询每 CAN ID 在 timestamp 之前的最后一帧并解码，按 MessageName 排序后整体替换。
    /// 结果属性变更经 UI dispatcher 发布（对齐 TraceChartViewModel 的发布路径）。
    /// </summary>
    public async Task LoadAsync(double timestamp, CancellationToken ct = default)
    {
        var frames = await _cache.GetLatestFramesBeforeAsync(_traceId, timestamp, ct).ConfigureAwait(false);
        var rows = new List<AnchorValueRow>();
        foreach (var frame in frames)
        {
            var decoded = _dbc?.Decode(frame.CanId, frame.IsExtended, frame.Data, frame.Dlc);
            if (decoded is { Signals.Count: > 0 })
            {
                foreach (var signal in decoded.Signals)
                    rows.Add(new AnchorValueRow(decoded.MessageName, signal.Name, signal.Value, signal.Unit));
            }
            else
            {
                // 无 DBC / 未定义 ID：raw fallback——hex 格式化必须复用 FrameRow，
                // 保证面板与表格逐字节一致（FrameRow 注释强调的同一缓存/一致性理由）。
                var row = FrameRow.FromCached(frame);
                rows.Add(new AnchorValueRow(row.IdText, string.Empty, row.DataText, string.Empty));
            }
        }

        // 稳定排序（List.Sort 基于 introsort 不稳定，同消息多信号会被任意交换）；
        // OrderBy 保证同 MessageName 行保持 DBC 解码顺序。
        rows = [.. rows.OrderBy(static r => r.MessageName, StringComparer.Ordinal)];
        _ui.Post(() =>
        {
            Rows = rows;
            IsEmpty = rows.Count == 0;
        });
    }
}