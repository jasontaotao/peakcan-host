using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Core.Analysis.Dbc;
using DbcValueType = PeakCan.HIL.Core.Dbc.ValueType;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class DbcViewModel
{
    [ObservableProperty]
    private DbcMessageViewModel? _selectedMessage;

    [ObservableProperty]
    private ushort? _selectedMuxValue;

    [ObservableProperty]
    private SignalBitLayoutSnapshot? _layoutSnapshot;

    [ObservableProperty]
    private bool _hasMux;

    [ObservableProperty]
    private string _layoutEmptyText = "";

    public ObservableCollection<ushort> MuxValues { get; } = new();

    public ObservableCollection<DbcSignalRow> VisibleSignals { get; } = new();

    public ObservableCollection<ByteLayoutRow> LayoutRows { get; } = new();

    private bool _suppressLayout;

    partial void OnSelectedMessageChanged(DbcMessageViewModel? value)
    {
        if (_suppressLayout) return;
        RebuildMuxAndLayout();
    }

    partial void OnSelectedMuxValueChanged(ushort? value)
    {
        if (_suppressLayout) return;
        UpdateLayout();
    }

    /// <summary>
    /// 重算 mux 候选项与位图。切换消息时默认选最小 mux 值；搜索恢复同一消息时
    /// 传 <paramref name="preferredMux"/> 保留用户当前分支。
    /// <para>
    /// 下拉只列已有分支，正常 UI 选不到“无匹配”值；<c>mux_no_match</c> 是对
    /// 外部直接设置 <see cref="SelectedMuxValue"/> 的防御路径。
    /// </para>
    /// </summary>
    private void RebuildMuxAndLayout(ushort? preferredMux = null)
    {
        var wasSuppressed = _suppressLayout;
        _suppressLayout = true;
        try
        {
            MuxValues.Clear();
            if (SelectedMessage?.RawMessage is { } message)
            {
                var values = message.Signals
                    .Where(s => s.IsMultiplexed && s.MultiplexValue is not null)
                    .Select(s => s.MultiplexValue!.Value)
                    .Distinct()
                    .OrderBy(v => v);
                foreach (var value in values) MuxValues.Add(value);
            }

            HasMux = MuxValues.Count > 0;
            if (!HasMux)
            {
                SelectedMuxValue = null;
            }
            else if (preferredMux is { } preferred && MuxValues.Contains(preferred))
            {
                SelectedMuxValue = preferred;
            }
            else
            {
                SelectedMuxValue = MuxValues[0];
            }
        }
        finally
        {
            _suppressLayout = wasSuppressed;
        }

        UpdateLayout();
    }

    /// <summary>
    /// 重算当前选中消息的静态布局快照，并投影成可绑定的行。
    /// 只由选中消息 / mux 变化触发，无定时器。
    /// </summary>
    private void UpdateLayout()
    {
        LayoutRows.Clear();
        VisibleSignals.Clear();
        LayoutEmptyText = "";

        if (SelectedMessage?.RawMessage is not { } message)
        {
            LayoutSnapshot = null;
            SyncLive();
            return;
        }

        var snapshot = SignalBitLayout.Compute(message, SelectedMuxValue);
        LayoutSnapshot = snapshot;

        if (snapshot.RenderedDlc == 0)
        {
            LayoutEmptyText = "无信号 / 无载荷（DLC=0）";
        }
        else if (snapshot.Segments.Count == 0)
        {
            LayoutEmptyText = "无信号 / 无载荷";
        }
        else if (snapshot.Cells.All(c => c.OwnerSignal is null))
        {
            LayoutEmptyText = $"{snapshot.Segments.Count} 个信号均无可绘制位";
        }

        var diagnostics = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in snapshot.Diagnostics.GroupBy(
                     d => d.SignalName, StringComparer.Ordinal))
        {
            diagnostics[group.Key] = string.Join(
                "；", group.Select(d => d.Text));
        }

        foreach (var segment in snapshot.Segments)
        {
            var signal = message.Signals.FirstOrDefault(s =>
                string.Equals(s.Name, segment.SignalName, StringComparison.Ordinal));
            if (signal is null) continue;

            VisibleSignals.Add(new DbcSignalRow(
                signal,
                segment.PaletteIndex,
                diagnostics.TryGetValue(segment.SignalName, out var text)
                    ? text
                    : ""));
        }

        var paletteByName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var segment in snapshot.Segments)
            paletteByName.TryAdd(segment.SignalName, segment.PaletteIndex);

        foreach (var group in snapshot.Cells
                     .GroupBy(c => c.ByteIndex)
                     .OrderBy(g => g.Key))
        {
            var cells = new List<ByteLayoutCell>(8);
            foreach (var cell in group.OrderByDescending(c => c.BitIndex))
            {
                var palette = cell.OwnerSignal is not null
                    && paletteByName.TryGetValue(cell.OwnerSignal, out var index)
                    ? index
                    : -1;
                cells.Add(new ByteLayoutCell(
                    cell.BitIndex,
                    cell.OwnerSignal,
                    palette,
                    FormatCellTooltip(message, cell)));
            }

            LayoutRows.Add(new ByteLayoutRow(group.Key, cells));
        }

        SyncLive();
    }

    /// <summary>hover / 键盘聚焦卡片的内容：位置、信号摘要、factor/offset、重叠信息。</summary>
    private static string FormatCellTooltip(Message message, BitCell cell)
    {
        var head = $"B{cell.ByteIndex} bit{cell.BitIndex}";
        if (cell.OwnerSignal is null) return $"{head} 未分配";

        head += $" · {cell.OwnerSignal}";
        var signal = message.Signals.FirstOrDefault(s =>
            string.Equals(s.Name, cell.OwnerSignal, StringComparison.Ordinal));
        if (signal is null) return head;

        var detail = string.Create(
            CultureInfo.InvariantCulture,
            $"{signal.StartBit}|{signal.Length}@{(signal.Order == ByteOrder.LittleEndian ? '1' : '0')} · factor {signal.Factor}, offset {signal.Offset} {signal.Unit}");
        if (cell.OverlappedSignals.Count > 0)
        {
            detail += $"\n与 {string.Join("、", cell.OverlappedSignals)} 重叠";
        }

        return $"{head}\n{detail}";
    }
}

/// <summary>信号表一行：原始 Signal、调色板索引、布局诊断文本与实时值。</summary>
public sealed partial class DbcSignalRow : ObservableObject
{
    public DbcSignalRow(Signal signal, int paletteIndex, string diagnosticText)
    {
        Signal = signal;
        PaletteIndex = paletteIndex;
        DiagnosticText = diagnosticText;
    }

    public Signal Signal { get; }

    public int PaletteIndex { get; }

    public string DiagnosticText { get; }

    public string Name => Signal.Name;

    public string BitLayout => string.Create(
        CultureInfo.InvariantCulture,
        $"{Signal.StartBit}|{Signal.Length}@{(Signal.Order == ByteOrder.LittleEndian ? '1' : '0')}{(Signal.ValueType == DbcValueType.Signed ? '-' : '+')}");

    public string FactorOffset => string.Create(
        CultureInfo.InvariantCulture, $"{Signal.Factor} / {Signal.Offset}");

    public string Unit => Signal.Unit;

    public string Comment => Signal.Comment ?? "";

    /// <summary>当前帧解码后的物理值；无数据 / mux 分支不匹配时显示“—”。</summary>
    [ObservableProperty]
    private string _liveValue = "—";
}

/// <summary>位图的一行（一个字节），Cells 固定按 bit 7 → bit 0 排列。</summary>
public sealed record ByteLayoutRow(int ByteIndex, IReadOnlyList<ByteLayoutCell> Cells)
{
    public string ByteLabel => $"B{ByteIndex}";
}

/// <summary>位图的一格：位号、归属信号、调色板索引与 hover 文本。</summary>
public sealed partial class ByteLayoutCell : ObservableObject
{
    public ByteLayoutCell(int bitNumber, string? ownerSignal, int paletteIndex, string tooltip)
    {
        BitNumber = bitNumber;
        OwnerSignal = ownerSignal;
        PaletteIndex = paletteIndex;
        Tooltip = tooltip;
    }

    public int BitNumber { get; }

    public string? OwnerSignal { get; }

    public int PaletteIndex { get; }

    public string Tooltip { get; }

    /// <summary>
    /// 当前帧里该位的值：true/false = 有数据；null = 无实时数据（未选中、
    /// 该字节超出帧载荷，或该格不属于任何信号）。只有归属信号的格子在实时刷新时赋值。
    /// </summary>
    [ObservableProperty]
    private bool? _isSet;
}
