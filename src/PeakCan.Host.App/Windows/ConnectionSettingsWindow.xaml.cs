using System.Windows;

namespace PeakCan.Host.App.Windows;

/// <summary>
/// P1-2: modal connection-settings dialog. Fields (device / channel /
/// bitrate / FD) are driven by the selected <see cref="ViewModels.DeviceDescriptor"/>
/// via <see cref="ViewModels.ConnectionSettingsViewModel"/>; "应用并连接"
/// writes the selection back to the shell and triggers Connect.
/// </summary>
public partial class ConnectionSettingsWindow : Window
{
    public ConnectionSettingsWindow()
    {
        InitializeComponent();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
