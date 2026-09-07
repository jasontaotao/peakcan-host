using PeakCan.Host.Mobile.Core.Models;
using PeakCan.Host.Mobile.Core.Platform;
using PeakCan.Host.Mobile.Core.ViewModels;

namespace PeakCan.Host.Mobile.Views;

public partial class TracePage : ContentPage
{
    private readonly TraceSessionViewModel _vm;
    private readonly IUiDispatcher _ui;
    private bool _following = true;
    private double _lastVerticalOffset;

    public TracePage(IUiDispatcher ui, IStreamingSourceFactory sourceFactory, string cachedFilePath)
    {
        InitializeComponent();
        _ui = ui;
        _vm = new TraceSessionViewModel(ui, sourceFactory, src =>
            new PeakCan.Host.Core.Replay.StreamingTracePlayer(src, clock: null));
        BindingContext = _vm;
        SpeedPicker.ItemsSource = new[] { "0.1x", "0.5x", "1x", "2x", "5x", "10x" };
        SpeedPicker.SelectedIndex = 2;
        Frames.Scrolled += OnFramesScrolled;
        _vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(TraceSessionViewModel.VisibleRows) && _following)
                _ui.Post(() => Frames.ScrollTo(_vm.VisibleRows.Count - 1, position: ScrollToPosition.End, animate: false));
        };
        _ = _vm.OpenAsync(cachedFilePath);
    }

    internal void PauseForBackground() => _vm.PauseForBackground();

    private void OnTogglePlay(object? sender, EventArgs e) => _vm.TogglePlayCommand.Execute(null);
    private void OnFilterCompleted(object? sender, EventArgs e) => _vm.SetIdFilter(FilterEntry.Text);
    private void OnSpeedChanged(object? sender, EventArgs e)
    {
        var sel = (string?)SpeedPicker.SelectedItem;
        if (sel is not null && double.TryParse(sel.TrimEnd('x'), out var m)) _vm.SetSpeed(m);
    }
    private void OnSeekCompleted(object? sender, EventArgs e) => _vm.SeekToCommand.Execute(_vm.Progress01);
    private void OnJumpLatest(object? sender, EventArgs e)
    {
        _following = true;
        Frames.ScrollTo(_vm.VisibleRows.Count - 1, position: ScrollToPosition.End, animate: false);
    }
    private void OnFramesScrolled(object? sender, ItemsViewScrolledEventArgs e)
    {
        if (e.VerticalOffset < _lastVerticalOffset - 1)
            _following = false;
        _lastVerticalOffset = e.VerticalOffset;
    }
    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not BindableObject { BindingContext: FrameRow row }) return;
        _ = Navigation.PushAsync(new FrameDetailSheet($"0x{row.IdText} @ {row.TimeText}", row.DataText));
    }
}

