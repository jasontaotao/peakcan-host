using System.Windows;
using System.Windows.Controls;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Views;

/// <summary>
/// Code-behind for the DBC tab view. Visibility changes drive the
/// phase-2 live-refresh timer; everything else lives in the VM.
/// </summary>
public partial class DbcView : UserControl
{
    public DbcView()
    {
        InitializeComponent();
        IsVisibleChanged += OnIsVisibleChanged;
    }

    /// <summary>切到其他标签页时停掉 33ms 实时刷新，切回再恢复。</summary>
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        => (DataContext as DbcViewModel)?.SetLiveVisible(IsVisible);
}
