using LiveChartsCore;
using LiveChartsCore.Drawing;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Measure;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Maui;
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
    private DbcCatalog? _appliedDbc;
    private bool _isChartTab;
    private readonly ChartXViewportSync _xViewport = new();
    private readonly Dictionary<CartesianChart, IChartXAxisViewport> _xViewports = new();
    private readonly List<CartesianChart> _charts = new();
    private readonly Dictionary<CartesianChart, (LineSeries<ObservablePoint> Series, SignalSelectionKey Key)> _plotSeries = new();
    private readonly List<SignalSelectionKey> _renderedKeys = [];
    private readonly SolidColorPaint _cursorPaint = new(SKColors.Orange.WithAlpha(64));

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
        _appliedDbc = _dbcHolder.Current;
        _vm.SetDbc(_appliedDbc);
        _ = InitializeAsync(cachedFilePath, sourceName, fileSizeBytes);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        SizeChanged += OnPageSizeChanged;
        _dbcHolder.Changed += OnDbcHolderChanged;
        ApplyHolderDbc();
        ApplyChartFullscreen();
    }

    protected override void OnDisappearing()
    {
        SizeChanged -= OnPageSizeChanged;
        _dbcHolder.Changed -= OnDbcHolderChanged;
        base.OnDisappearing();
    }

    private void OnPageSizeChanged(object? sender, EventArgs e)
    {
        ApplyChartFullscreen();
    }

    /// <summary>Hot-apply the app-wide DBC so WeChat shares reach the open session.</summary>
    private void OnDbcHolderChanged() => ApplyHolderDbc();

    private void ApplyHolderDbc()
    {
        if (ReferenceEquals(_appliedDbc, _dbcHolder.Current)) return;
        _appliedDbc = _dbcHolder.Current;
        _vm.SetDbc(_appliedDbc);
        ScrollToLatest();
    }

    private void ApplyChartFullscreen()
    {
        var isFullscreen = _isChartTab && Width > Height;
        ControlsRow.IsVisible = !isFullscreen;
        DbcStatusRow.IsVisible = !isFullscreen;
        SeekSlider.IsVisible = !isFullscreen;
        StatusRow.IsVisible = !isFullscreen && !_isChartTab;
        FilterRow.IsVisible = !isFullscreen && !_isChartTab;
        TabRow.IsVisible = !isFullscreen;
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
            ApplyHolderDbc();
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
        _isChartTab = false;
        FramesGrid.IsVisible = true;
        ChartGrid.IsVisible = false;
        StatusRow.IsVisible = true;
        FilterRow.IsVisible = true;
        ApplyChartFullscreen();
    }

    private void ShowChartTab()
    {
        _isChartTab = true;
        FramesGrid.IsVisible = false;
        ChartGrid.IsVisible = true;
        StatusRow.IsVisible = false;
        FilterRow.IsVisible = false;
        RenderChart();
        ApplyChartFullscreen();
    }

    private void OnChartRenderChanged(object? sender, EventArgs e) => RenderChart();

    private void RenderChart()
    {
        var chart = _vm.Chart;

        SelectedSignalsLabel.Text = chart.SelectedSignals.Count == 0
            ? string.Empty
            : string.Join("  |  ", chart.SelectedSignals.Select(s => s.DisplayName));
        ChartEmptyLabel.Text = chart.Messages.Count == 0
            ? "请先加载 DBC"
            : "请选择 1–4 个 DBC 信号";
        ChartEmptyLabel.IsVisible = chart.SelectedSignals.Count == 0;

        var renderableSignals = chart.SelectedSignals
            .Where(s => chart.RenderPoints.ContainsKey(s.Key))
            .ToList();

        // 结构未变（同一组信号）时只刷新数据与游标：播放期间每 100ms 的
        // RefreshRender 不再整树重建 native 图表视图，缩放手势也不会被打断。
        if (_charts.Count == renderableSignals.Count &&
            _renderedKeys.SequenceEqual(renderableSignals.Select(s => s.Key)))
        {
            UpdatePlotData(chart);
            return;
        }

        foreach (var plot in _charts)
            plot.UpdateStarted -= OnPlotUpdateStarted;

        ChartHost.Children.Clear();
        ChartHost.RowDefinitions.Clear();
        _charts.Clear();
        _plotSeries.Clear();
        _renderedKeys.Clear();
        _xViewports.Clear();
        _xViewport.Clear();

        var seriesColors = new[]
        {
            SKColors.MediumBlue,
            SKColors.IndianRed,
            SKColors.SeaGreen,
            SKColors.DarkOrange,
        };

        var renderableCount = chart.SelectedSignals.Count(s => chart.RenderPoints.ContainsKey(s.Key));
        for (var index = 0; index < chart.SelectedSignals.Count; index++)
        {
            var selection = chart.SelectedSignals[index];
            if (!chart.RenderPoints.TryGetValue(selection.Key, out var points)) continue;

            var paint = new SolidColorPaint(seriesColors[index % seriesColors.Length]);
            var series = new LineSeries<ObservablePoint>
            {
                Name = selection.DisplayName,
                Values = points.Select(p => new ObservablePoint(p.Timestamp, p.Value)).ToArray(),
                // min-max 包络需要点标记辅助读图；点径 6 是既有视觉基线。
                GeometrySize = 6,
                GeometryFill = paint,
                GeometryStroke = paint,
                Stroke = paint,
                Fill = null,
                LineSmoothness = 0,
            };

            var yAxis = new Axis
            {
                Name = selection.Key.SignalName + (string.IsNullOrEmpty(selection.Unit) ? "" : $" ({selection.Unit})"),
                NameTextSize = 11,
                TextSize = 10,
                MinStep = chart.SelectedSignals.Count > 2 ? 1 : 0,
                ForceStepToMin = chart.SelectedSignals.Count > 2,
                NamePaint = paint,
                LabelsPaint = paint,
                Labeler = value => value.ToString("0.###", CultureInfo.InvariantCulture),
                SeparatorsPaint = new SolidColorPaint(SKColors.LightGray.WithAlpha(64)),
            };

            var xAxis = new Axis
            {
                Name = "时间 (s)",
                NameTextSize = 11,
                TextSize = 10,
                Labeler = value => value.ToString("F2", CultureInfo.InvariantCulture),
                MinStep = 1,
                IsVisible = _charts.Count == renderableCount - 1,
            };

            var plot = new CartesianChart
            {
                ZoomMode = ZoomAndPanMode.X,
                ZoomingSpeed = 0.8,
                LegendPosition = LegendPosition.Hidden,
                Series = [series],
                XAxes = [xAxis],
                YAxes = [yAxis],
                Sections = chart.Cursor is { } cursor
                    ? [new RectangularSection
                       {
                       Xi = cursor.Timestamp,
                       Xj = cursor.Timestamp,
                       ScalesYAt = 0,
                       Fill = _cursorPaint,
                       }]
                    : [],
            };
            plot.UpdateStarted += OnPlotUpdateStarted;

            var height = chart.SelectedSignals.Count switch
            {
                1 => 600,
                2 => 280,
                _ => 130,
            };
            ChartHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(height) });
            ChartHost.Children.Add(plot);
            Grid.SetRow(plot, _charts.Count);
            _charts.Add(plot);
            _xViewports[plot] = new AxisXViewport(xAxis);
            _plotSeries[plot] = (series, selection.Key);
            _renderedKeys.Add(selection.Key);
        }

        _xViewport.Attach(_xViewports.Values);
    }

    private void UpdatePlotData(TraceChartViewModel chart)
    {
        foreach (var plot in _charts)
        {
            if (!_plotSeries.TryGetValue(plot, out var entry)) continue;
            if (!chart.RenderPoints.TryGetValue(entry.Key, out var points)) continue;

            entry.Series.Values = points.Select(p => new ObservablePoint(p.Timestamp, p.Value)).ToArray();
            plot.Sections = chart.Cursor is { } cursor
                ? [new RectangularSection
                   {
                       Xi = cursor.Timestamp,
                       Xj = cursor.Timestamp,
                       ScalesYAt = 0,
                       Fill = _cursorPaint,
                   }]
                : [];
        }
    }

    private void OnPlotUpdateStarted(IChartView chart)
    {
        if (chart is CartesianChart plot && _xViewports.TryGetValue(plot, out var viewport))
            _xViewport.SyncFrom(viewport);
    }

    private sealed class AxisXViewport(Axis axis) : IChartXAxisViewport
    {
        public bool TryGetRange(out ChartAxisRange range)
        {
            if (axis.MinLimit is { } minimum
                && axis.MaxLimit is { } maximum
                && double.IsFinite(minimum)
                && double.IsFinite(maximum)
                && minimum < maximum)
            {
                range = new ChartAxisRange(minimum, maximum);
                return true;
            }

            range = default;
            return false;
        }

        public void SetRange(ChartAxisRange range)
        {
            axis.MinLimit = range.Minimum;
            axis.MaxLimit = range.Maximum;
        }
    }

    private void OnZoomInClicked(object? sender, EventArgs e) => ZoomChart(ZoomDirection.ZoomIn);

    private void OnZoomOutClicked(object? sender, EventArgs e) => ZoomChart(ZoomDirection.ZoomOut);

    private void OnResetZoomClicked(object? sender, EventArgs e)
    {
        _xViewport.Reset();
        // 就地更新路径不清轴限位；强制结构重建以恢复自动缩放。
        _renderedKeys.Clear();
        RenderChart();
    }

    private void ZoomChart(ZoomDirection direction)
    {
        var plot = _charts.FirstOrDefault();
        if (plot is null
            || plot.CoreChart is not CartesianChartEngine engine
            || plot.Width <= 0
            || !_xViewports.TryGetValue(plot, out var viewport))
            return;

        var center = new LvcPoint(plot.Width / 2, plot.Height / 2);
        engine.Zoom(ZoomAndPanMode.ZoomX | ZoomAndPanMode.NoFit, center, direction, null);
        _xViewport.SyncFrom(viewport);
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


