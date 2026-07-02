using System.Windows;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Windows;

/// <summary>
/// v2.1.0 MINOR: non-modal window for composing and sending a
/// sequence of CAN frames. Hosts a <see cref="MultiFrameSendViewModel"/>;
/// DataGrid + toolbar + mode/run row are bound declaratively in
/// the .xaml. All behavior lives in the VM; this class is just
/// code-behind glue + window close.
/// </summary>
public partial class MultiFrameSendWindow : Window
{
    public MultiFrameSendWindow(MultiFrameSendViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>
    /// v2.1.0 MINOR: close button handler. Closes the window; the VM
    /// is owned by the DI container and disposed on host shutdown.
    /// </summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}