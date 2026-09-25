using System.Collections.Specialized;
using System.Windows;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels.Xcp;

namespace PeakCan.Host.App.Views.Xcp;

/// <summary>
/// S3-T9: code-behind for the XCP object picker dialog. Thin shell only —
/// all selection/dedup logic lives in <see cref="XcpObjectPickerViewModel"/>.
/// Wires the leaf checkbox Click to the VM selection (DbcTreePickerWindow
/// precedent: binding-based IsChecked is unreliable on the WPF DataTemplate),
/// mirrors the watched-object count label, and exposes
/// <see cref="ConfirmedRows"/> to the caller on OK (T11 wires the entry point).
/// </summary>
public partial class XcpObjectPickerWindow : Window
{
    /// <summary>Selected (name, category) rows handed back on OK. Empty on Cancel.</summary>
    public IReadOnlyList<XcpWatchRow> ConfirmedRows { get; private set; }
        = Array.Empty<XcpWatchRow>();

    public XcpObjectPickerWindow(XcpObjectPickerViewModel vm) : this()
    {
        InitializeComponent();
        DataContext = vm;
        vm.Roots.CollectionChanged += (_, _) => UpdateSelectedCount(vm);
        foreach (var group in vm.Roots)
            group.Children.CollectionChanged += (_, _) => UpdateSelectedCount(vm);
        UpdateSelectedCount(vm);
    }

    public XcpObjectPickerWindow()
    {
        InitializeComponent();
    }

    private void OnNodeCheckClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox cb
            || cb.DataContext is not XcpPickerNode node
            || DataContext is not XcpObjectPickerViewModel vm
            || !node.IsLeaf)
            return;

        node.IsSelected = !node.IsSelected;
        UpdateSelectedCount(vm);
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not XcpObjectPickerViewModel vm)
            return;

        ConfirmedRows = vm.Confirm();
        DialogResult = true;
        Close();
    }

    private void UpdateSelectedCount(XcpObjectPickerViewModel vm)
    {
        var count = vm.Roots.Sum(g => g.Children.Count(n => n.IsSelected));
        SelectedCountText.Text = $"{count} object(s) selected";
    }
}
