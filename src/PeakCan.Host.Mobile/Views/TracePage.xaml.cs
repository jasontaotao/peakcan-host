using Microsoft.Extensions.Logging;
using PeakCan.Host.Mobile.Core.Models;
using PeakCan.Host.Mobile.Core.Platform;
using PeakCan.Host.Mobile.Core.ViewModels;

namespace PeakCan.Host.Mobile.Views;

public partial class TracePage : ContentPage
{
    private readonly TraceSessionViewModel _vm;

    public TracePage(IUiDispatcher ui, IStreamingSourceFactory sourceFactory, string cachedFilePath, ILogger? logger = null)
    {
        InitializeComponent();
        _vm = new TraceSessionViewModel(ui, sourceFactory, src =>
            new PeakCan.Host.Core.Replay.StreamingTracePlayer(src, clock: null), logger);
        BindingContext = _vm;
        _vm.PropertyChanged += OnVmPropertyChanged;
        SpeedPicker.ItemsSource = new[] { "0.1x", "0.5x", "1x", "2x", "5x", "10x" };
        SpeedPicker.SelectedIndex = 2;
        _ = InitializeAsync(cachedFilePath);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 拖拽期间不回写 Slider（防止 native SeekBar 重置拖拽手势）
        if (e.PropertyName == nameof(TraceSessionViewModel.Progress01) && !_vm.IsSeekDragging)
            SeekSlider.Value = _vm.Progress01;
    }

    private async Task InitializeAsync(string cachedFilePath)
    {
        await _vm.OpenAsync(cachedFilePath);
        ScrollToLatest();
    }

    internal void PauseForBackground() => _vm.PauseForBackground();

    private void OnTogglePlay(object? sender, EventArgs e)
    {
        _vm.TogglePlayCommand.Execute(null);
        if (_vm.State == PeakCan.Host.Mobile.Core.ViewModels.SessionState.Playing)
            Dispatcher.Dispatch(ScrollToLatest);
    }

    private void ScrollToLatest()
    {
        if (_vm.VisibleRows.Count > 0)
            Frames.ScrollTo(_vm.VisibleRows.Count - 1, position: ScrollToPosition.End, animate: false);
    }

    private void OnFilterCompleted(object? sender, EventArgs e) => _vm.SetIdFilter(FilterEntry.Text);

    private void OnSpeedChanged(object? sender, EventArgs e)
    {
        var sel = (string?)SpeedPicker.SelectedItem;
        if (sel is not null && double.TryParse(sel.TrimEnd('x'), out var m)) _vm.SetSpeed(m);
    }

    private void OnSeekStarted(object? sender, EventArgs e)
    {
        _vm.IsSeekDragging = true;
    }

    private void OnSeekCompleted(object? sender, EventArgs e)
    {
        _vm.SeekToCommand.Execute(SeekSlider.Value);
    }

    private void OnJumpLatest(object? sender, EventArgs e) => ScrollToLatest();

    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not BindableObject { BindingContext: FrameRowSlot row } || row.IsEmpty) return;
        _ = Navigation.PushAsync(new FrameDetailSheet($"0x{row.IdText} @ {row.TimeText}", row.DataText));
    }
}
