using System.ComponentModel;
using System.Windows;
using PeakCan.Host.App.Services.HilPanel;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Windows;

/// <summary>
/// P0-3: window host for the HIL testing surface. Carries the existing
/// <see cref="Views.HilView"/>; DataContext is set by the caller (the
/// shared <c>HilViewModel</c> singleton) via the Show factory.
/// </summary>
public partial class HilWindow : Window
{
    private readonly HilPanelStateStore? _stateStore;

    public HilWindow(HilPanelStateStore? stateStore = null)
    {
        InitializeComponent();
        _stateStore = stateStore;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_stateStore is null || DataContext is not HilViewModel vm) return;
        await _stateStore.LoadAsync(default);
        vm.ApplyPanelState(_stateStore.Get());
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_stateStore is null || DataContext is not HilViewModel vm) return;
        _stateStore.Set(vm.CapturePanelState());
    }
}