namespace PeakCan.Host.Mobile.Views;

public partial class FrameDetailSheet : ContentPage
{
    public string Header { get; }
    public string RawBytes { get; }

    public FrameDetailSheet(string header, string rawBytes)
    {
        InitializeComponent();
        Header = header;
        RawBytes = rawBytes;
        BindingContext = this;
    }
}
