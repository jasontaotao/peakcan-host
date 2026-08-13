namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceChartViewModel
{
    public void SetTotalDuration(double seconds) => TotalDuration = seconds;
}
