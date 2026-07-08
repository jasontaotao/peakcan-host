using System.Windows;
using System.Windows.Controls;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Views;

/// <summary>
/// v3.16.0 MINOR: code-behind for the DBC tree picker dialog. Wires
/// the signal checkbox Click to the VM's selection, applies the
/// search filter on text change, and exposes the selected signals
/// via <see cref="SelectedSignals"/> on OK click.
/// </summary>
public partial class DbcTreePickerWindow : Window
{
    public IReadOnlyList<(uint CanId, string SignalName)> SelectedSignals { get; private set; }
        = Array.Empty<(uint, string)>();

    public DbcTreePickerWindow(DbcTreePickerViewModel vm) : this()
    {
        InitializeComponent();
        DataContext = vm;
        UpdateSelectedCount();
    }

    public DbcTreePickerWindow()
    {
        InitializeComponent();
    }

    private void OnSignalCheckClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { IsChecked: bool isChecked } cb
            && cb.DataContext is DbcTreeNode node
            && DataContext is DbcTreePickerViewModel vm)
        {
            if (isChecked)
            {
                if (!vm.SelectedSignals.Contains(node))
                    vm.SelectedSignals.Add(node);
            }
            else
            {
                vm.SelectedSignals.Remove(node);
            }
            UpdateSelectedCount();
        }
    }

    private void UpdateSelectedCount()
    {
        if (DataContext is DbcTreePickerViewModel vm)
            SelectedCountText.Text = $"{vm.SelectedSignals.Count} signal(s) selected";
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is DbcTreePickerViewModel vm)
            SelectedSignals = vm.GetSelectedTuples();
        DialogResult = true;
        Close();
    }
}