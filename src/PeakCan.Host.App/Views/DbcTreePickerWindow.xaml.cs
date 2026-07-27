using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    /// <summary>v3.62.0 MINOR: index of the last signal node clicked (without
    /// Shift). Used as the anchor for Shift+Click range selection. -1 means
    /// no anchor yet (first click or reset after search filter change).</summary>
    private int _lastSignalIndex = -1;

    public DbcTreePickerWindow(DbcTreePickerViewModel vm) : this()
    {
        InitializeComponent();
        DataContext = vm;
        // v3.62.0 MINOR: keep each node's IsSelected in sync with the
        // ViewModel's SelectedSignals collection so that batch operations
        // (全选/反选/Shift+Click range) refresh the checkbox visuals.
        vm.SelectedSignals.CollectionChanged += (_, _) => SyncAllNodeSelectionState(vm);
        UpdateSelectedCount();
    }

    public DbcTreePickerWindow()
    {
        InitializeComponent();
    }

    private void OnSignalCheckClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { IsChecked: bool isChecked } cb
            || cb.DataContext is not DbcTreeNode node
            || DataContext is not DbcTreePickerViewModel vm)
            return;

        // v3.62.0 MINOR: Shift+Click range selection. When Shift is held
        // and we have a previous anchor, toggle every visible signal
        // between the anchor and the current node (inclusive).
        if (IsShiftHeld() && _lastSignalIndex >= 0)
        {
            var visible = GetVisibleSignalNodes(vm);
            var currentIndex = visible.IndexOf(node);
            if (currentIndex >= 0)
            {
                var start = Math.Min(_lastSignalIndex, currentIndex);
                var end = Math.Max(_lastSignalIndex, currentIndex);
                for (int i = start; i <= end; i++)
                    ApplyCheckState(visible[i], isChecked, vm);
                _lastSignalIndex = currentIndex;
                UpdateSelectedCount();
                return;
            }
            // Fall through to single-click if current node not found
            // (shouldn't happen — defensive).
        }

        // Single-click toggle (original behavior).
        ApplyCheckState(node, isChecked, vm);
        _lastSignalIndex = GetVisibleSignalNodes(vm).IndexOf(node);
        UpdateSelectedCount();
    }

    /// <summary>v3.62.0 MINOR: detect Shift key state reliably. Uses
    /// Keyboard.IsKeyDown for both LeftShift and RightShift (more
    /// reliable than Keyboard.Modifiers in a Click event handler).</summary>
    private static bool IsShiftHeld() =>
        Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);

    /// <summary>v3.62.0 MINOR: apply a check state to a single signal node
    /// (add or remove from SelectedSignals). The CollectionChanged
    /// handler on SelectedSignals will sync the node's IsSelected.</summary>
    private static void ApplyCheckState(DbcTreeNode node, bool isChecked, DbcTreePickerViewModel vm)
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
    }

    /// <summary>v3.62.0 MINOR: flatten the tree into a depth-first ordered
    /// list of visible signal nodes. Used by Shift+Click range selection
    /// and by the "全选可见" / "反选" batch actions. Respects the search
    /// filter (IsVisible=false nodes and their descendants are excluded).</summary>
    private static List<DbcTreeNode> GetVisibleSignalNodes(DbcTreePickerViewModel vm)
    {
        var result = new List<DbcTreeNode>();
        foreach (var root in vm.Roots)
            CollectVisibleSignals(root, result);
        return result;
    }

    private static void CollectVisibleSignals(DbcTreeNode node, List<DbcTreeNode> result)
    {
        if (!node.IsVisible) return;
        if (node.IsSignal) result.Add(node);
        foreach (var child in node.Children)
            CollectVisibleSignals(child, result);
    }

    /// <summary>v3.62.0 MINOR: sync every visible signal node's IsSelected
    /// property with the ViewModel's SelectedSignals collection. Called
    /// from the CollectionChanged handler so batch operations (全选/反选
    /// /Shift+Click range) refresh the checkbox visuals.</summary>
    private static void SyncAllNodeSelectionState(DbcTreePickerViewModel vm)
    {
        foreach (var node in GetVisibleSignalNodes(vm))
            node.IsSelected = vm.SelectedSignals.Contains(node);
    }

    /// <summary>v3.62.0 MINOR: "全选可见" button — select every signal that
    /// passes the current search filter.</summary>
    private void OnSelectAllVisibleClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DbcTreePickerViewModel vm) return;
        foreach (var node in GetVisibleSignalNodes(vm))
        {
            if (!vm.SelectedSignals.Contains(node))
                vm.SelectedSignals.Add(node);
        }
        _lastSignalIndex = -1;  // anchor no longer meaningful after bulk select
        // CollectionChanged handler syncs IsSelected automatically.
        UpdateSelectedCount();
    }

    /// <summary>v3.62.0 MINOR: "反选" button — invert the selection state of
    /// every visible signal (selected → unselected, unselected → selected).</summary>
    private void OnInvertSelectionClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DbcTreePickerViewModel vm) return;
        foreach (var node in GetVisibleSignalNodes(vm))
        {
            if (vm.SelectedSignals.Contains(node))
                vm.SelectedSignals.Remove(node);
            else
                vm.SelectedSignals.Add(node);
        }
        _lastSignalIndex = -1;  // anchor no longer meaningful after invert
        // CollectionChanged handler syncs IsSelected automatically.
        UpdateSelectedCount();
    }

    /// <summary>v3.62.0 MINOR: reset the Shift+Click anchor when the search
    /// filter changes — the visible list has been reordered/truncated so
    /// the old index would point at the wrong node.</summary>
    private void OnSearchBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        _lastSignalIndex = -1;
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