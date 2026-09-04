using PeakCan.Host.App.ViewModels.HIL;

namespace PeakCan.Host.App.Views.HIL;

public partial class TraceToEnvironmentWindow : System.Windows.Window
{
    public TraceToEnvironmentWindow()
    {
        InitializeComponent();
        if (DataContext is TraceToEnvironmentViewModel)
            Title = "录制转为环境";
    }
}
