namespace PeakCan.Host.Mobile.Views;

public partial class FrameDetailSheet : ContentPage
{
    public string Header { get; }
    public string RawBytes { get; }
    public IReadOnlyList<PeakCan.Host.Mobile.Core.Services.SignalDisplay> Signals { get; }

    public FrameDetailSheet(string header, string rawBytes,
        IReadOnlyList<PeakCan.Host.Mobile.Core.Services.SignalDisplay> signals)
    {
        InitializeComponent();
        Header = header;
        RawBytes = rawBytes;
        Signals = signals;
        BindingContext = this;
    }
}
