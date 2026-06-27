using System.Windows.Controls;

namespace PeakCan.Host.App.Views;

/// <summary>
/// v1.2.11 PATCH Item 6 UI: thin UserControl shell around
/// <see cref="ViewModels.RecordViewModel"/>. All behavior lives in the VM;
/// the code-behind only calls <c>InitializeComponent</c>.
/// </summary>
public partial class RecordView : UserControl
{
    public RecordView() => InitializeComponent();
}