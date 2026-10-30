using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Views;

/// <summary>
/// Code-behind for the Trace tab view. The control hosts a virtualized
/// <c>DataGrid</c> bound to <see cref="ViewModels.TraceViewModel.Entries"/>.
/// <para>
/// <b>v0.9.2 auto-scroll:</b> when the user is at the bottom of the grid,
/// new rows automatically scroll into view. If the user scrolls up to
/// inspect history, auto-scroll pauses until they return to the bottom.
/// </para>
/// </summary>
public partial class TraceView : UserControl
{
    private bool _autoScroll = true;
    private bool _collectionSubscribed;

    public TraceView()
    {
        InitializeComponent();
        TraceGrid.Loaded += (_, _) =>
        {
            // Loaded 每次切回 tab 都会触发；_collectionSubscribed 防止重复订阅
            // （重复订阅会让每帧触发多次 ScrollIntoView）。
            if (!_collectionSubscribed &&
                TraceGrid.ItemsSource is INotifyCollectionChanged cc)
            {
                cc.CollectionChanged += OnCollectionChanged;
                _collectionSubscribed = true;
            }
        };
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && _autoScroll)
        {
            TraceGrid.ScrollIntoView(TraceGrid.Items[^1]);
        }
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Auto-scroll is on when the user is within 1 row-height of the bottom.
        // Use the event args instead of casting sender (which may be
        // the DataGrid, not the ScrollViewer).
        _autoScroll = e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 20;
    }

    /// <summary>
    /// P0-2：DataGrid 右键默认不改变选中行——先把指针下的行设为 SelectedItem，
    /// 右键菜单命令才能通过 PlacementTarget.SelectedItem 拿到正确的 TraceEntry。
    /// 刻意把 Extended 多选收敛为单选：当前菜单命令都是单行语义。
    /// </summary>
    private void OnGridPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(TraceGrid, source) is DataGridRow row)
        {
            TraceGrid.SelectedItem = row.Item;
            row.Focus();
        }
        else
        {
            // 空白区域/表头右键：清空选中，防止菜单命令误作用于上一行。
            TraceGrid.SelectedItem = null;
        }
    }

    /// <summary>
    /// P0-2：只在右键命中真实帧行时弹出菜单。空白区/表头/滚动条右键已被
    /// <see cref="OnGridPreviewMouseRightButtonDown"/> 清空选中，这里直接取消
    /// 菜单，避免"点了菜单没反应"的静默失败。
    /// </summary>
    private void OnGridContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (TraceGrid.SelectedItem is not TraceEntry)
            e.Handled = true;
    }

    /// <summary>
    /// 复制文本的纯函数（ID + 数据 hex），供单测直接断言。
    /// DLC=0 时 DataHex 为空，结果不带尾随空格。
    /// </summary>
    internal static string BuildIdAndData(TraceEntry entry)
        => $"{entry.Id} {entry.DataHex}".TrimEnd();

    /// <summary>
    /// P0-2：复制选中行的 ID + 数据 hex。剪贴板可能被其他进程短暂占用，
    /// 重试 3 次仍失败则弹窗告知——不静默吞掉异常。
    /// </summary>
    private void OnCopyIdAndData(object sender, RoutedEventArgs e)
    {
        if (TraceGrid.SelectedItem is not TraceEntry entry) return;
        var text = BuildIdAndData(entry);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (ExternalException ex)
            {
                if (attempt >= 2)
                {
                    var owner = Window.GetWindow(this);
                    if (owner is not null)
                        MessageBox.Show(owner, $"复制失败：{ex.Message}",
                            "复制 ID / 数据", MessageBoxButton.OK, MessageBoxImage.Warning);
                    else
                        MessageBox.Show($"复制失败：{ex.Message}",
                            "复制 ID / 数据", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                Thread.Sleep(50);
            }
        }
    }
}
