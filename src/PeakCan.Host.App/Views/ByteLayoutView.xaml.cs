using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Views;

public partial class ByteLayoutView : UserControl
{
    private ByteLayoutCell? _hovered;

    public ByteLayoutView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ClosePopup();
        Unloaded += (_, _) => ClosePopup();
    }

    private void OnCellMouseEnter(object sender, MouseEventArgs e) => ShowCell(sender);

    private void OnCellMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ByteLayoutCell cell }
            && ReferenceEquals(_hovered, cell))
        {
            ClosePopup();
        }
    }

    private void OnCellGotFocus(object sender, RoutedEventArgs e) => ShowCell(sender);

    private void OnCellLostFocus(object sender, RoutedEventArgs e) => ClosePopup();

    private void ShowCell(object sender)
    {
        if (sender is not FrameworkElement { DataContext: ByteLayoutCell cell }) return;

        _hovered = cell;
        CellPopupTitle.Text = cell.OwnerSignal ?? $"bit {cell.BitNumber}";
        CellPopupDetail.Text = cell.Tooltip;
        CellPopup.PlacementTarget = (UIElement)sender;
        CellPopup.IsOpen = true;
    }

    private void ClosePopup()
    {
        _hovered = null;
        CellPopup.IsOpen = false;
    }
}
