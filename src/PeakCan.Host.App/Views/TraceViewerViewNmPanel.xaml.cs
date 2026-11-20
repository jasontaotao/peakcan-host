using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Views;

public partial class TraceViewerViewNmPanel : UserControl
{
    // 活动条相对宿主左右各留 2px（行模板 Margin="2,3,2,3"），准星换算需扣掉。
    private const double NmPlotInset = 2.0;

    // 当前悬停段：MouseEnter 先于上一段的 MouseLeave 时，避免旧 Leave 关掉新卡片。
    private NmTimelineSegmentVm? _nmHoveredSegment;
    private TraceViewerViewModel? _nmBoundVm;

    public TraceViewerViewNmPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (_nmBoundVm is not null)
            _nmBoundVm.NmTimelineRows.CollectionChanged -= OnNmTimelineRowsChanged;
        _nmBoundVm = DataContext as TraceViewerViewModel;
        if (_nmBoundVm is not null)
            _nmBoundVm.NmTimelineRows.CollectionChanged += OnNmTimelineRowsChanged;
        NmSegmentPopup.DataContext = DataContext;
    }

    /// <summary>
    /// 重分析/清空报告会重建 NmTimelineRows；被悬停的段被移出可视树时 WPF
    /// 不保证补发 MouseLeave，Popup 是独立窗口，会停在屏幕上变成悬空卡片。
    /// </summary>
    private void OnNmTimelineRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        CloseSegmentPopup();
        if (DataContext is TraceViewerViewModel vm) vm.ClearNmHoverTime();
    }

    /// <summary>滚动后 Popup 不跟随目标，准星也会离开指针；统一收起等下次悬停。</summary>
    private void OnNmTimelineScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        CloseSegmentPopup();
        if (DataContext is TraceViewerViewModel vm) vm.ClearNmHoverTime();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        NmSegmentPopup.IsOpen = false;
        _nmHoveredSegment = null;
        if (DataContext is TraceViewerViewModel vm) vm.ClearNmHoverTime();
    }

    /// <summary>时间条准星：像素 x → 设计坐标 → 窗口时间读数。</summary>
    private void OnNmTimelineMouseMove(object sender, MouseEventArgs e)
    {
        if (DataContext is not TraceViewerViewModel vm) return;
        var width = NmTimelineTrackHost.ActualWidth;
        if (width <= 0) return;

        var x = Math.Clamp(e.GetPosition(NmTimelineTrackHost).X, 0, width);
        Canvas.SetLeft(NmCrosshairBand, Math.Clamp(x - 11, 0, Math.Max(width - 22, 0)));
        Canvas.SetLeft(NmCrosshairLine, x);
        Canvas.SetLeft(NmHoverChip, Math.Clamp(
            x - NmHoverChip.Width / 2, 0, Math.Max(width - NmHoverChip.Width, 0)));
        // 活动条只占 [2, W-2]：用同一区间反算设计坐标，两端才不会偏移约 50ms。
        var plotWidth = Math.Max(width - NmPlotInset * 2, 1);
        vm.SetNmHoverDesignX(Math.Clamp(
            (x - NmPlotInset) / plotWidth * TraceViewerViewModel.NmTimelineWidth,
            0, TraceViewerViewModel.NmTimelineWidth));
    }

    private void OnNmTimelineMouseLeave(object sender, MouseEventArgs e)
    {
        if (DataContext is TraceViewerViewModel vm) vm.ClearNmHoverTime();
        CloseSegmentPopup();
    }

    /// <summary>活动段 hover：高亮由 XAML Trigger 负责，这里只更新单例信息卡。</summary>
    private void OnNmSegmentMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NmTimelineSegmentVm segment }) return;
        if (DataContext is not TraceViewerViewModel vm) return;

        _nmHoveredSegment = segment;
        vm.SetNmSegmentHover(segment.Kind, FindRowVm((DependencyObject)sender)?.Label ?? "",
            segment.Start, segment.End);
        NmSegmentPopup.DataContext = vm;
        NmSegmentPopup.PlacementTarget = (UIElement)sender;
        NmSegmentPopup.IsOpen = true;
    }

    private void OnNmSegmentMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NmTimelineSegmentVm segment }) return;
        // 已移到另一段时，旧段的 Leave 不得关闭新卡片。
        if (!ReferenceEquals(_nmHoveredSegment, segment)) return;
        CloseSegmentPopup();
    }

    private void CloseSegmentPopup()
    {
        _nmHoveredSegment = null;
        if (NmSegmentPopup is not null) NmSegmentPopup.IsOpen = false;
        if (DataContext is TraceViewerViewModel vm) vm.ClearNmSegmentHover();
    }

    /// <summary>段所在行的行标签（报文 ID/名称/发送者，或「整网状态」）。</summary>
    private static NmTimelineRowVm? FindRowVm(DependencyObject start)
    {
        var current = VisualTreeHelper.GetParent(start);
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: NmTimelineRowVm row }) return row;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    /// <summary>
    /// spec §9.1 / AC6：事件行单击即 seek。只处理真正落在 DataGridRow 上的点击，
    /// 避免点表头/空白处把上一行再 seek 一次。
    /// </summary>
    private void OnEventRowClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not TraceViewerViewModel vm || sender is not DataGrid grid) return;
        if (e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(grid, source) is not DataGridRow row) return;
        if (row.Item is NmEventRowVm nmRow)
            vm.SeekToNmEventCommand.Execute(nmRow);
    }

    /// <summary>保留双击 seek（原有行为）。</summary>
    private void OnEventRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is TraceViewerViewModel vm && sender is DataGrid grid
            && grid.SelectedItem is NmEventRowVm row)
        {
            vm.SeekToNmEventCommand.Execute(row);
        }
    }
}
