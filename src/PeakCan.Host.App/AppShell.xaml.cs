using System.Windows;

namespace PeakCan.Host.App;

/// <summary>
/// Top-level shell window. Hosts the menu (File: Open DBC, Exit), a
/// channel-probe / connect toolbar, the status bar, and an empty
/// <c>ContentControl</c> that Tasks 13-17 will populate with per-tab
/// view-models (Trace / Send / DBC / Signal / Stats).
/// </summary>
public partial class AppShell : Window
{
    public AppShell()
    {
        InitializeComponent();
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();
}
