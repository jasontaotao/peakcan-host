using Microsoft.Extensions.Logging;
using PeakCan.Host.Mobile.Core.Models;
using PeakCan.Host.Mobile.Core.Platform;
using PeakCan.Host.Mobile.Core.Services;
using PeakCan.Host.Mobile.Core.ViewModels;

namespace PeakCan.Host.Mobile.Views;

public partial class TracePage : ContentPage
{
    private readonly TraceSessionViewModel _vm;
    private readonly IDbcCatalogProvider _dbcProvider;
    private readonly DbcCatalogHolder _dbcHolder;

    public TracePage(
        IUiDispatcher ui,
        IStreamingSourceFactory sourceFactory,
        string cachedFilePath,
        string sourceName,
        long fileSizeBytes,
        ILogger? logger = null,
        IDbcCatalogProvider? dbcProvider = null,
        DbcCatalogHolder? dbcHolder = null)
    {
        InitializeComponent();
        _dbcProvider = dbcProvider ?? throw new ArgumentNullException(nameof(dbcProvider));
        _dbcHolder = dbcHolder ?? new DbcCatalogHolder();
        _vm = new TraceSessionViewModel(ui, sourceFactory,
            src => new PeakCan.Host.Core.Replay.StreamingTracePlayer(src, clock: null), logger);
        BindingContext = _vm;
        _vm.PropertyChanged += OnVmPropertyChanged;
        SpeedPicker.ItemsSource = new[] { "0.1x", "0.5x", "1x", "2x", "5x", "10x" };
        SpeedPicker.SelectedIndex = 2;
        _vm.SetDbc(_dbcHolder.Current);
        _ = InitializeAsync(cachedFilePath, sourceName, fileSizeBytes);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 拖拽期间不回写 Slider（防止 native SeekBar 重置拖拽手势）
        if (e.PropertyName == nameof(TraceSessionViewModel.Progress01) && !_vm.IsSeekDragging)
            SeekSlider.Value = _vm.Progress01;
    }

    private async Task InitializeAsync(string cachedFilePath, string sourceName, long fileSizeBytes)
    {
        await _vm.OpenAsync(cachedFilePath, sourceName, fileSizeBytes);
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

    private async void OnLoadDbcClicked(object? sender, EventArgs e)
    {
        try
        {
            var result = await _dbcProvider.PickAndLoadAsync();
            if (result.Error is not null)
            {
                await DisplayAlertAsync("DBC 加载失败", result.Error, "确定");
                return;
            }
            if (result.Catalog is null) return;

            _dbcHolder.Set(result.Catalog);
            _vm.SetDbc(result.Catalog);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("DBC 加载失败", ex.Message, "确定");
        }
    }

    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not BindableObject { BindingContext: FrameRowSlot row }
            || row.IsEmpty || row.Source is null)
            return;

        var decoded = _vm.Dbc?.Decode(
            row.Source.Id,
            row.Source.IsExtended,
            row.Source.Data,
            row.Source.Dlc)?.Signals ?? [];

        _ = Navigation.PushAsync(new FrameDetailSheet(
            $"0x{row.IdText} @ {row.TimeText}",
            row.DataText,
            decoded));
    }
}
