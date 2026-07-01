using System.Windows;
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
/// </summary>
public partial class AppShell : Window
{
    public AppShell()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Unsubscribe immediately — this is a one-shot initialization.
        SourceInitialized -= OnSourceInitialized;
        if (DataContext is AppShellViewModel shell)
        {
            shell.ShowTraceCommand.Execute(null);
        }
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();
}