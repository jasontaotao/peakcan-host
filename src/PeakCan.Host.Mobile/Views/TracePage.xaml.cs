using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using System.Globalization;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Mobile.Core.Models;
using PeakCan.Host.Mobile.Core.Platform;
using PeakCan.Host.Mobile.Core.Services;
using SkiaSharp;
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
        DbcCatalogHolder? dbcHolder = null,
        ITraceCacheSinkFactory? cacheSinkFactory = null)
    {
        InitializeComponent();
        _dbcProvider = dbcProvider ?? throw new ArgumentNullException(nameof(dbcProvider));
        _dbcHolder = dbcHolder ?? new DbcCatalogHolder();
        _vm = new TraceSessionViewModel(ui, sourceFactory,
            src => new PeakCan.Host.Core.Replay.StreamingTracePlayer(src, clock: null), logger, cacheSinkFactory);
        BindingContext = _vm;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.Chart.RenderChanged += OnChartRenderChanged;
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

    private void OnShowTableClicked(object? sender, EventArgs e) => ShowTableTab();

    private void OnShowChartClicked(object? sender, EventArgs e) => ShowChartTab();

    private void ShowTableTab()
    {
        FramesGrid.IsVisible = true;
        ChartGrid.IsVisible = false;
        StatusRow.IsVisible = true;
        FilterRow.IsVisible = true;
    }

    private void ShowChartTab()
    {
        FramesGrid.IsVisible = false;
        ChartGrid.IsVisible = true;
        StatusRow.IsVisible = false;
        FilterRow.IsVisible = false;
        RenderChart();
    }

    private void OnChartRenderChanged(object? sender, EventArgs e) => RenderChart();

    private void RenderChart()
    {
        var chart = _vm.Chart;
        SelectedSignalsLabel.Text = chart.SelectedSignals.Count == 0
            ? string.Empty
            : string.Join("  |  ", chart.SelectedSignals.Select(s => s.DisplayName));
        ChartEmptyLabel.IsVisible = chart.SelectedSignals.Count == 0;

        var series = new List<ISeries>();
        foreach (var selection in chart.SelectedSignals)
        {
            if (!chart.RenderPoints.TryGetValue(selection.Key, out var points)) continue;
            series.Add(new LineSeries<ObservablePoint>
            {
                Name = selection.DisplayName,
                Values = points.Select(p => new ObservablePoint(p.Timestamp, p.Value)).ToArray(),
                GeometrySize = 0,
                Fill = null,
                LineSmoothness = 0
            });
        }

        SignalChart.Series = series;
        SignalChart.XAxes = [new Axis
        {
            Name = "时间 (s)",
            Labeler = value => value.ToString("F2", CultureInfo.InvariantCulture)
        }];
        SignalChart.YAxes = [new Axis()];

        if (chart.Cursor is { } cursor)
        {
            SignalChart.Sections = [new RectangularSection
            {
                Xi = cursor.Timestamp,
                Xj = cursor.Timestamp,
                Fill = new SolidColorPaint(SKColors.Orange.WithAlpha(48))
            }];
        }
        else
        {
            SignalChart.Sections = [];
        }
    }

    private void OnSelectSignalClicked(object? sender, EventArgs e)
    {
        _ = Navigation.PushAsync(new SignalSelectionPage(_vm.Chart));
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


