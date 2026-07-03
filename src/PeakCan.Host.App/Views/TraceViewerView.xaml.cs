using System.Windows;
using Microsoft.Win32;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Views;

public partial class TraceViewerView : Window
{
    public TraceViewerView()
    {
        InitializeComponent();
    }

    public TraceViewerView(TraceViewerViewModel vm) : this()
    {
        DataContext = vm;
    }

    private async void OnOpenAscClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "ASC files|*.asc;*.ASC|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        if (DataContext is TraceViewerViewModel vm)
        {
            try { await vm.OpenFileAsync(dlg.FileName); }
            catch (System.Exception ex) { MessageBox.Show(this, ex.Message, "Open failed"); }
        }
    }

    private async void OnLoadDbcClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "DBC files|*.dbc;*.DBC|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        if (DataContext is TraceViewerViewModel vm)
        {
            try { await vm.LoadDbcAsync(dlg.FileName); }
            catch (System.Exception ex) { MessageBox.Show(this, ex.Message, "DBC load failed"); }
        }
    }
}
