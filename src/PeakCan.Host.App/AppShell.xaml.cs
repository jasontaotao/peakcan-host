using System.ComponentModel;
using System.Windows;
using PeakCan.Host.App.Services.Ui;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App;

/// <summary>
/// Top-level shell window. Hosts the menu (File: Open DBC, Exit / View:
/// Trace / DBC / Send), a channel-probe / connect toolbar, the status
/// bar, and a <c>ContentControl</c> bound to
/// <see cref="AppShellViewModel.CurrentView"/>.
/// <para>
/// On <see cref="OnSourceInitialized"/> we kick off
/// <see cref="AppShellViewModel.ShowTraceCommand"/> so the trace grid
/// is the default landing surface. We use <c>SourceInitialized</c>
/// (not the ctor) because the WPF dispatcher loop isn't pumping in the
/// ctor — but the STA requirement is satisfied by then.
/// </para>
/// <para>
/// P0-5: <see cref="WindowStateStore"/> (injected at startup) drives the
/// main window's geometry persistence — restore on
/// <c>SourceInitialized</c>, save on <c>Closing</c>. Secondary windows are
/// persisted inside <see cref="WindowHostService"/> instead.
/// </para>
/// </summary>
public partial class AppShell : Window
{
    /// <summary>P0-5: injected at startup by AppHostBuilder — drives the
    /// main shell's window-geometry persistence.</summary>
    public WindowStateStore? WindowStateStore { get; set; }

    /// <summary>P2-6: injected at startup by AppHostBuilder — persists the
    /// AppShell layout (right-panel width + main/right tab selection) to
    /// <c>%APPDATA%/PeakCan.Host/layout.json</c>.</summary>
    public LayoutStateStore? LayoutStateStore { get; set; }

    public AppShell()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Unsubscribe immediately — this is a one-shot initialization.
        SourceInitialized -= OnSourceInitialized;
        // P0-5: restore persisted geometry before the default tab renders.
        if (WindowStateStore is not null)
        {
            WindowHostService.ApplyStoredState(this, WindowKey.AppShell, WindowStateStore);
        }
        // P2-6: restore persisted layout (right-panel width + tab selection)
        // before the default tab renders. No-op without a LayoutStateStore or
        // a real AppShellViewModel DataContext.
        RestoreLayout();
        if (DataContext is AppShellViewModel shell)
        {
            shell.ShowTraceCommand.Execute(null);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (WindowStateStore is not null)
        {
            WindowHostService.SaveState(this, WindowKey.AppShell, WindowStateStore);
        }
        // P2-6: persist the current layout before the window closes.
        SaveLayout();
    }

    /// <summary>P2-6: apply the persisted layout (right-panel width + tab
    /// selection) onto the shell. Silent no-op when the store is null, the
    /// DataContext is not a real <see cref="AppShellViewModel"/>, or no
    /// state has been saved yet.</summary>
    private void RestoreLayout()
    {
        if (LayoutStateStore is null || DataContext is not AppShellViewModel shell) return;
        var s = LayoutStateStore.Get();
        if (s is null) return;
        if (s.RightPanelWidth > 0) RightPanelColumn.Width = new GridLength(s.RightPanelWidth);
        shell.SelectedMainTabIndex = s.SelectedMainTabIndex;
        shell.SelectedRightTabIndex = s.SelectedRightTabIndex;
    }

    /// <summary>P2-6: capture the current layout and persist it. Silent
    /// no-op when the store is null or the DataContext is not a real
    /// <see cref="AppShellViewModel"/>.</summary>
    private void SaveLayout()
    {
        if (LayoutStateStore is null || DataContext is not AppShellViewModel shell) return;
        LayoutStateStore.Set(new LayoutStateDto(
            RightPanelColumn.Width.Value, shell.SelectedMainTabIndex, shell.SelectedRightTabIndex));
    }

    /// <summary>P2-6 测试挂钩：从测试代码注入布局（真实操作由用户拖
    /// splitter / 切 tab 完成，本方法让 STA 测试直接驱动保存路径）。
    /// Visible to PeakCan.Host.App.Tests via InternalsVisibleTo.</summary>
    internal void TestSetLayout(double rightPanelWidth, int mainTab, int rightTab)
    {
        RightPanelColumn.Width = new GridLength(rightPanelWidth);
        if (DataContext is AppShellViewModel shell)
        {
            shell.SelectedMainTabIndex = mainTab;
            shell.SelectedRightTabIndex = rightTab;
        }
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();
}