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
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();
}