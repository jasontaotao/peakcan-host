namespace PeakCan.Host.Mobile.Views;

public partial class FrameDetailSheet : ContentPage
{
    public string Header { get; }
    public string RawBytes { get; }
    public IReadOnlyList<PeakCan.Host.Mobile.Core.Services.SignalDisplay> Signals { get; }
    public bool ShowSetAnchorButton { get; }

    private readonly Action<double>? _onSetAnchor;
    private readonly double? _frameTimestamp;

    /// <summary>
    /// <paramref name="onSetAnchor"/> 与 <paramref name="frameTimestamp"/> 均非空时显示
    /// "设为锚点"按钮，点击后回调并返回上一页。
    /// </summary>
    public FrameDetailSheet(string header, string rawBytes,
        IReadOnlyList<PeakCan.Host.Mobile.Core.Services.SignalDisplay> signals,
        Action<double>? onSetAnchor = null, double? frameTimestamp = null)
    {
        InitializeComponent();
        Header = header;
        RawBytes = rawBytes;
        Signals = signals;
        _onSetAnchor = onSetAnchor;
        _frameTimestamp = frameTimestamp;
        ShowSetAnchorButton = onSetAnchor is not null && frameTimestamp is not null;
        BindingContext = this;
    }

    private async void OnSetAnchorClicked(object? sender, EventArgs e)
    {
        if (_onSetAnchor is null || _frameTimestamp is not { } timestamp) return;
        _onSetAnchor(timestamp);
        _ = await Navigation.PopAsync();
    }
}