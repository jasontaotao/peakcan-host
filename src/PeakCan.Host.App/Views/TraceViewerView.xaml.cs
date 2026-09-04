using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.HIL.Core.Analysis;
using ScottPlot;
using ScottPlot.Interactivity.UserActionResponses;
using ScottPlot.WPF;

namespace PeakCan.Host.App.Views;

public partial class TraceViewerView : Window
{
    /// <summary>v3.62.0: 保存 OnCompleted 处理器引用以便取消订阅。</summary>
    private readonly Dictionary<string, Action<ProgressiveScatterSource>> _completedHandlers = new();

    private Popup? _tooltipPopup;
    private TextBlock? _tooltipName;
    private TextBlock? _tooltipTime;
    private TextBlock? _tooltipValue;

    public TraceViewerView()
    {
        InitializeComponent();
        // v3.62.0: 窗口关闭时取消所有 OnCompleted 订阅
        // v3.x (会话状态剥离 Task 3): VM 已 transient——窗口关闭即释放 VM（Dispose
        // 反注册对 singleton 的订阅），否则每次开/关窗口都会泄漏一个 VM。
        Closed += (_, _) =>
        {
            UnsubscribeAllCompletedHandlers();
            if (DataContext is IDisposable d) d.Dispose();
        };
    }

    /// <summary>v3.62.0: 取消所有 OnCompleted 订阅（防止内存泄漏）。</summary>
    private void UnsubscribeAllCompletedHandlers()
    {
        if (DataContext is not TraceViewerViewModel vm) return;
        foreach (var (signalKey, handler) in _completedHandlers)
        {
            var series = vm.ChartViewModel.Series.FirstOrDefault(s => s.SignalKey == signalKey);
            if (series?.ProgressiveSource is not null)
                series.ProgressiveSource.OnCompleted -= handler;
        }
        _completedHandlers.Clear();
    }

    public TraceViewerView(TraceViewerViewModel vm) : this()
    {
        DataContext = vm;
    }

    // v3.9.2 PATCH H2: OnAddTraceClick was deleted — v3.9.1 PATCH Bug #2
    // moved the toolbar button to Command="{Binding AddTraceCommand}" so
    // the click handler became dead code (XAML no longer wires
    // Click="OnAddTraceClick"). AddTraceCommand opens the file dialog via
    // CommandParameter="" and surfaces failures via vm.ErrorMessage /
    // vm.StatusMessage.

    // DELETED (v3.13.0 PATCH F3): OnLoadDbcClick. The Trace Viewer
    // toolbar "Load DBC…" button (XAML line 31) was removed because
    // LoadedDbcPath was never bound in TraceViewerView.xaml — the
    // toolbar click had no UI feedback. The Trace Viewer still reads
    // _dbcService.Current for decoding; DbcView tab is now the single
    // entry point for DBC loading.

    // v3.0.2 PATCH Task 2: header buttons inside each subplot DataTemplate.
    // DataContext inside the template is the TraceChartSeries row, so we
    // cast sender.DataContext to TraceChartSeries and the window's
    // DataContext (the VM) to TraceViewerViewModel, then forward to the
    // chart VM's SetFocus / ToggleCollapse methods (both added in Task 1).
    private void OnFocusSubplotClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe
            && fe.DataContext is TraceChartSeries s
            && DataContext is TraceViewerViewModel vm)
        {
            vm.ChartViewModel.SetFocus(s);
        }
    }

    private void OnCollapseSubplotClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe
            && fe.DataContext is TraceChartSeries s
            && DataContext is TraceViewerViewModel vm)
        {
            vm.ChartViewModel.ToggleCollapse(s);
        }
    }

    // v3.0.2 PATCH Task 2: feed ChartAreaHeight (which feeds
    // AdaptiveHeight via RecomputeHeights) from the chart area's actual
    // height. Loaded fires once on first render; SizeChanged fires on
    // window resize and GridSplitter drag.
    private void OnChartScrollLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is TraceViewerViewModel vm && sender is FrameworkElement fe)
        {
            vm.ChartViewModel.ChartAreaHeight = fe.ActualHeight;
        }
    }

    private void OnChartScrollSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is TraceViewerViewModel vm)
        {
            vm.ChartViewModel.ChartAreaHeight = e.NewSize.Height;
        }
    }

    /// <summary>
    /// v3.14.3 PATCH: checkbox Click handler for the opt-in column.
    /// Mirrors the v1.2.7 SignalView pattern — WPF's
    /// DataGridCheckBoxColumn edit lifecycle is unreliable on .NET 10
    /// when the parent grid is IsReadOnly=True. The Click event fires
    /// regardless of edit-mode state, so it's the primary path for
    /// chart-plot opt-in.
    /// <para>
    /// Reads the CheckBox.IsChecked UI value (just toggled by the
    /// click) and forwards the explicit opt-in intent to the VM via
    /// <c>SetPlotOptIn</c>. The TwoWay binding on IsPlotted updates
    /// the row's INPC field as a side effect, but we don't depend on
    /// it — the UI-side IsChecked is the source of truth at click time.
    /// </para>
    /// </summary>
    /// <summary>
    /// v3.16.0 MINOR: open the DBC tree picker dialog. The user
    /// selects one or more signals; we call
    /// <c>TraceViewerViewModel.AddToWatch</c> for each (cross-source).
    /// v3.16.2 PATCH BUGFIX: after the picker returns, finalize the
    /// watch state (drop placeholder + refresh frame counts + plot all
    /// in one WPF render pass). This avoids the ItemContainerGenerator
    /// confusion that arises when AddToWatch's internal "Add row +
    /// Remove placeholder" sequence interleaves with the picker's
    /// burst of multiple AddToWatch calls.
    /// </summary>
    private void OnAddToWatchClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TraceViewerViewModel vm) return;
        var doc = vm.GetDbcForPicker();
        if (doc is null) return;  // VM already surfaced a status message
        var pickerVm = new DbcTreePickerViewModel(doc);
        var dialog = new DbcTreePickerWindow(pickerVm) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var added = new List<PeakCan.Host.App.ViewModels.WatchedSignalRow>();
        foreach (var (canId, signalName) in dialog.SelectedSignals)
            added.Add(vm.AddToWatchForPicker(canId, signalName, ""));
        // Finalize after the AddToWatch burst settles: drop the
        // placeholder (if any), refresh frame counts, plot. One pass.
        vm.FinalizePickerAdds(added);
    }

    private void OnPlotCheckboxClick(object sender, RoutedEventArgs e)
    {
        // v3.16.4 PATCH BUGFIX (multi-agent review): the prior guard
        // `if (row.IsPlotted == isChecked) return;` was the cause of
        // the "☑ Plot click is a no-op" symptom. The CheckBox's
        // TwoWay binding writes the new IsPlotted value to
        // `row.IsPlotted` BEFORE the Click event fires, so by the
        // time we read it here, `row.IsPlotted == isChecked` is
        // ALWAYS true. The guard fired on every click → SetPlotOptIn
        // was never called → chart series was never added/removed.
        //
        // The fix: unconditionally call SetPlotOptIn with the
        // CheckBox.IsChecked (the UI's truth at click time). The VM
        // uses the new value to decide plot vs unplot. Matches the
        // working pattern in SignalView.xaml.cs:61-69.
        if (sender is CheckBox { IsChecked: bool isChecked } cb
            && cb.DataContext is WatchedSignalRow row
            && DataContext is TraceViewerViewModel vm)
        {
            vm.SetPlotOptIn(row, isChecked);
        }
    }

    // === v3.50.0 MINOR T3: green-line anchor drag handlers ===
    // v3.62.0: 蓝线左键拖拽 + 空白区域平移

    /// <summary>True while the user is dragging inside any PlotView. Gates
    /// <see cref="OnPlotViewMouseMove"/> so we only forward anchor updates
    /// during an active drag (otherwise every mouse hover would commit an
    /// anchor at the cursor X).</summary>
    private bool _isDraggingGreenLine;
    private bool _isDraggingBlueLine;

    private void OnPlotViewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
        if (sender is not WpfPlot pv) return;
        if (pv.Plot is null) return;
        if (DataContext is not TraceViewerViewModel vm) return;

        // 像素坐标检测是否靠近锚线（避免时间域转换的精度损失）
        var pos = e.GetPosition(pv);
        double dpiScale = GetDpiScale(pv);
        var pixel = new Pixel((float)(pos.X * dpiScale), (float)(pos.Y * dpiScale));

        // 1) 靠近绿线 → 拖绿线
        if (vm.IsGreenLineAnchorActive && vm.IsGreenLineVisible)
        {
            try
            {
                var gp = pv.Plot.GetPixel(new Coordinates(vm.AnchorTimestampSeconds, 0));
                if (Math.Abs(pixel.X - gp.X) < 5)
                {
                    _isDraggingGreenLine = true;
                    if (sender is System.Windows.IInputElement ie) ie.CaptureMouse();
                    if (TryGetAnchorSeconds(sender, e, out var ts)) CommitAnchor(ts);
                    e.Handled = true;
                    return;
                }
            }
            catch { /* 图表未就绪，忽略 */ }
        }

        // 2) 靠近蓝线 → 拖蓝线
        if (vm.IsBlueLineAnchorActive && vm.IsBlueLineVisible)
        {
            try
            {
                var bp = pv.Plot.GetPixel(new Coordinates(vm.BlueAnchorTimestampSeconds, 0));
                if (Math.Abs(pixel.X - bp.X) < 5)
                {
                    _isDraggingBlueLine = true;
                    if (sender is System.Windows.IInputElement ie) ie.CaptureMouse();
                    if (TryGetAnchorSeconds(sender, e, out var ts)) CommitAnchorBlue(ts);
                    e.Handled = true;
                    return;
                }
            }
            catch { /* 图表未就绪，忽略 */ }
        }

        // 3) 绿线不存在 → 数据区首次创建并拖拽；轴区放行给 ScottPlot 平移
        if (!vm.IsGreenLineAnchorActive)
        {
            if (!IsInsideDataArea(pv, pixel)) return;

            _isDraggingGreenLine = true;
            if (sender is System.Windows.IInputElement ie) ie.CaptureMouse();
            if (TryGetAnchorSeconds(sender, e, out var ts)) CommitAnchor(ts);
            e.Handled = true;
            return;
        }

        // 4) 空白区域 → 不拦截，ScottPlot 处理平移
    }

    /// <summary>v3.50.2 PATCH T3: right-button click commits the BLUE
    /// comparison anchor at the cursor's X. Single-shot (no drag) — right
    /// drag is not bound to a follow-up, so the blue anchor is a
    /// discrete "click and place" interaction, distinct from the green
    /// anchor's drag-to-track UX.</summary>
    private void OnPlotViewRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Right) return;
        if (TryGetAnchorSeconds(sender, e, out var ts))
        {
            if (DataContext is TraceViewerViewModel vm)
            {
                vm.RefreshAtAnchorBlue(ts);
            }
            e.Handled = true;
        }
    }

    private void OnPlotViewMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        HideTrackerTooltip();
    }

    private void OnPlotViewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        bool wasDragging = _isDraggingGreenLine || _isDraggingBlueLine;
        _isDraggingGreenLine = false;
        _isDraggingBlueLine = false;
        if (wasDragging && sender is System.Windows.IInputElement ie)
            ie.ReleaseMouseCapture();
    }

    /// <summary>v3.62.0: WpfPlot 滚轮冒泡事件。
    /// ScottPlot 先在隧道阶段处理缩放，冒泡阶段我们标记已处理阻止 ScrollViewer 滚动。</summary>
    private void OnChartPlotMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        e.Handled = true;
    }

    private void OnPlotViewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // 绿线拖拽
        if (_isDraggingGreenLine)
        {
            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
            if (TryGetAnchorSeconds(sender, e, out var ts))
            {
                CommitAnchor(ts);
                e.Handled = true;
            }
            return;
        }

        // 蓝线拖拽
        if (_isDraggingBlueLine)
        {
            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
            if (TryGetAnchorSeconds(sender, e, out var ts))
            {
                CommitAnchorBlue(ts);
                e.Handled = true;
            }
            return;
        }

        // 拖拽平移中不显示 tooltip
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            HideTrackerTooltip();
            return;
        }

        // 非拖拽 → cursor 反馈 + hover tooltip
        if (sender is WpfPlot pv && pv.DataContext is TraceChartSeries s && DataContext is TraceViewerViewModel vm)
        {
            var pos = e.GetPosition(pv);
            double dpiScale = GetDpiScale(pv);
            var pixel = new Pixel((float)(pos.X * dpiScale), (float)(pos.Y * dpiScale));

            bool nearLine = false;
            if (vm.IsGreenLineAnchorActive && vm.IsGreenLineVisible && pv.Plot is not null)
            {
                try
                {
                    var gp = pv.Plot.GetPixel(new Coordinates(vm.AnchorTimestampSeconds, 0));
                    if (Math.Abs(pixel.X - gp.X) < 5) nearLine = true;
                }
                catch { }
            }
            if (!nearLine && vm.IsBlueLineAnchorActive && vm.IsBlueLineVisible && pv.Plot is not null)
            {
                try
                {
                    var bp = pv.Plot.GetPixel(new Coordinates(vm.BlueAnchorTimestampSeconds, 0));
                    if (Math.Abs(pixel.X - bp.X) < 5) nearLine = true;
                }
                catch { }
            }
            pv.Cursor = nearLine ? System.Windows.Input.Cursors.SizeWE : System.Windows.Input.Cursors.Arrow;

            ShowTrackerTooltip(pv, s);
        }
    }

    /// <summary>v3.62.0 MINOR: WpfPlot Loaded handler. Delegates to VM.PopulatePlot
    /// so the VM-configured elements (LabelFormatter, color, scatter) are applied
    /// to the WpfPlot's internal Plot. Also wires the progressive fill RefreshCallback.</summary>
    private void OnChartPlotLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfPlot plot) return;
        if (plot.DataContext is not TraceChartSeries series) return;
        if (DataContext is not TraceViewerViewModel vm) return;

        // Register the WpfPlot's Plot with the VM (for anchor lines, axis sync)
        vm.RegisterPlot(series.SignalKey, plot.Plot);

        // Let the VM populate the WpfPlot's Plot (scatter + axes + LabelFormatter)
        vm.PopulatePlot(plot.Plot, series);

        // X-axis sync + wheel-axis behavior + double-click fit are all
        // wired here because UserInputProcessor lives on WpfPlot/IPlotControl.
        ConfigurePlotInteractivity(plot, series, vm);

        // Set up the RefreshCallback (VM → View bridge)
        series.RefreshCallback = () => plot.Refresh();

        // Wire the progressive fill callback so background decode triggers UI updates
        if (vm.TryGetActiveFillRequest(series.SignalKey, out var fillRequest))
        {
            fillRequest.RefreshCallback = () => plot.Refresh();
        }

        // 方案 C-2: 填充完成后基于实际数据重新适配 Y 轴（只扩大不缩小）
        if (series.ProgressiveSource is not null)
        {
            Action<ProgressiveScatterSource> handler = source =>
            {
                var (actualMin, actualMax) = source.GetActualYRange();
                double yMin, yMax;

                if (actualMax - actualMin > 1e-9)
                {
                    // 实际数据有效 → 用实际数据范围 + padding
                    var range = actualMax - actualMin;
                    var pad = range * 0.1;  // 10% padding
                    yMin = actualMin - pad;
                    yMax = actualMax + pad;
                }
                else
                {
                    // 实际数据无效（全 NaN 或单点） → fallback 到 DBC 范围
                    var sig = series.Signal;
                    if (sig is not null && sig.Min < sig.Max)
                    {
                        var range = sig.Max - sig.Min;
                        var pad = range * 0.05;
                        yMin = sig.Min - pad;
                        yMax = sig.Max + pad;
                    }
                    else
                    {
                        return;  // 无有效范围
                    }
                }

                // v3.62.0: 切回 UI 线程操作 UI 元素
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    if (plot.Plot is not null)
                    {
                        plot.Plot.Axes.SetLimitsY(yMin, yMax);
                        plot.Refresh();
                    }
                });
            };

            // 保存引用以便后续取消订阅
            _completedHandlers[series.SignalKey] = handler;
            series.ProgressiveSource.OnCompleted += handler;

            // 修复竞态条件：若填充已在订阅前完成，立即触发 Y 轴适配
            if (series.ProgressiveSource.IsCompleted)
                handler(series.ProgressiveSource);
        }

        plot.Refresh();
    }

    /// <summary>Inverse-transform the cursor X to a timestamp, then SNAP to the
    /// nearest actual sample point.
    /// v3.62.0: ScottPlot GetCoordinates (with DPI correction) + binary search.</summary>
    private bool TryGetAnchorSeconds(object sender, System.Windows.Input.MouseEventArgs e,
                                     out double timestampSeconds)
    {
        timestampSeconds = double.NaN;
        if (sender is not WpfPlot pv) return false;
        if (pv.DataContext is not TraceChartSeries series) return false;
        if (DataContext is not TraceViewerViewModel vm) return false;

        var plot = pv.Plot;
        if (plot is null) return false;

        // Fix #1: Convert WPF DIP to device pixels for ScottPlot
        var posDip = e.GetPosition(pv);
        double dpiScale = GetDpiScale(pv);
        var posPixel = new ScottPlot.Pixel((float)(posDip.X * dpiScale), (float)(posDip.Y * dpiScale));

        var coordinates = plot.GetCoordinates(posPixel);
        if (double.IsNaN(coordinates.X) || double.IsInfinity(coordinates.X)) return false;

        // Fix #2: Snap to nearest actual sample point via binary search
        var idx = BinarySearchNearest(series.XValues, coordinates.X);
        if (idx < 0) return false;
        timestampSeconds = series.XValues[idx];
        return true;
    }

    /// <summary>Get the DPI scale factor for a WPF element (1.0 = 96 DPI).</summary>
    private static double GetDpiScale(System.Windows.FrameworkElement element)
    {
        var source = System.Windows.PresentationSource.FromVisual(element);
        if (source?.CompositionTarget != null)
            return source.CompositionTarget.TransformToDevice.M11;
        return 1.0;
    }

    /// <summary>Binary search for the index of the nearest timestamp in a sorted array.</summary>
    private static int BinarySearchNearest(IReadOnlyList<double> sorted, double target)
    {
        if (sorted.Count == 0) return -1;
        if (sorted.Count == 1) return 0;
        int lo = 0, hi = sorted.Count - 1;
        while (lo < hi - 1)
        {
            int mid = lo + (hi - lo) / 2;
            if (sorted[mid] <= target) lo = mid;
            else hi = mid;
        }
        // Return whichever of lo/hi is closer to target
        return Math.Abs(sorted[lo] - target) <= Math.Abs(sorted[hi] - target) ? lo : hi;
    }

    private void CommitAnchor(double timestampSeconds)
    {
        if (DataContext is TraceViewerViewModel vm)
        {
            vm.RefreshAtAnchor(timestampSeconds);
        }
    }

    private void CommitAnchorBlue(double timestampSeconds)
    {
        if (DataContext is TraceViewerViewModel vm)
        {
            vm.RefreshAtAnchorBlue(timestampSeconds);
        }
    }

    /// <summary>v3.62.0 MINOR: Tracker tooltip on mouse hover (non-drag).
    /// Finds the nearest data point on the Scatter and displays signal name + time + value.
    /// Called from the existing OnPlotViewMouseMove handler.</summary>
    private void ShowTrackerTooltip(WpfPlot pv, TraceChartSeries series)
    {
        var plot = pv.Plot;
        if (plot is null || series.ProgressiveSource?.IsCompleted != true || plot.LastRender.DataRect.HasArea)
        {
            HideTrackerTooltip();
            return;
        }

        var pos = System.Windows.Input.Mouse.GetPosition(pv);
        double dpiScale = GetDpiScale(pv);
        var pixel = new ScottPlot.Pixel((float)(pos.X * dpiScale), (float)(pos.Y * dpiScale));
        var coordinates = plot.GetCoordinates(pixel);

        var scatter = plot.GetPlottables().OfType<ScottPlot.Plottables.Scatter>().FirstOrDefault();
        if (scatter is null)
        {
            HideTrackerTooltip();
            return;
        }

        var hit = scatter.GetNearest(coordinates, plot.LastRender, 15);
        if (!hit.IsReal)
        {
            HideTrackerTooltip();
            return;
        }

        EnsureTooltipPopup();
        if (_tooltipPopup is null || _tooltipName is null || _tooltipTime is null || _tooltipValue is null) return;

        var tooltip = TraceTooltipContent.Format(
            series.DisplayName,
            hit.Coordinates.X,
            series.Source?.WallClockOrigin,
            hit.Coordinates.Y,
            series.Unit);
        _tooltipName.Text = tooltip.DisplayName;
        _tooltipTime.Text = tooltip.Time;
        _tooltipValue.Text = tooltip.Value;

        _tooltipPopup.PlacementTarget = pv;
        _tooltipPopup.Placement = PlacementMode.RelativePoint;
        _tooltipPopup.HorizontalOffset = 12;
        _tooltipPopup.VerticalOffset = 16;
        _tooltipPopup.IsOpen = true;
    }

    private void HideTrackerTooltip()
    {
        if (_tooltipPopup is not null) _tooltipPopup.IsOpen = false;
    }

    private void EnsureTooltipPopup()
    {
        if (_tooltipPopup is not null) return;

        _tooltipName = new TextBlock { FontWeight = FontWeights.SemiBold };
        _tooltipTime = new TextBlock { Opacity = 0.9 };
        _tooltipValue = new TextBlock { Opacity = 0.95 };
        var stack = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };
        stack.Children.Add(_tooltipName);
        stack.Children.Add(_tooltipTime);
        stack.Children.Add(_tooltipValue);
        var border = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(220, 24, 24, 24)),
            CornerRadius = new CornerRadius(4),
            IsHitTestVisible = false,
            Child = stack,
        };

        _tooltipPopup = new Popup
        {
            AllowsTransparency = true,
            StaysOpen = true,
            IsHitTestVisible = false,
            Child = border,
        };
    }

    private void ConfigurePlotInteractivity(WpfPlot plot, TraceChartSeries series, TraceViewerViewModel vm)
    {
        var responses = plot.UserInputProcessor.UserActionResponses;

        // WPF Loaded can fire more than once; keep exactly one sync response per plot.
        foreach (var old in responses.OfType<SharedXAxisSyncResponse>().ToList())
            responses.Remove(old);
        responses.Add(new SharedXAxisSyncResponse(
            series.SignalKey,
            vm.ChartViewModel.SyncXAxis,
            () => vm.IsXAxisSyncEnabled));

        var wheelZoom = responses.OfType<MouseWheelZoom>().FirstOrDefault();
        if (wheelZoom is not null) wheelZoom.ZoomAxisUnderMouse = true;

        // ScottPlot 5.0.55 defaults to DoubleClickBenchmark; replace it with Fit.
        foreach (var oldBenchmark in responses.OfType<DoubleClickBenchmark>().ToList())
            responses.Remove(oldBenchmark);
        foreach (var oldFit in responses.OfType<DoubleClickResponse>().ToList())
            responses.Remove(oldFit);
        responses.Add(new DoubleClickResponse(
            ScottPlot.Interactivity.StandardMouseButtons.Left,
            (control, _) => FitSubplot(control.Plot, series, vm, control)));
    }

    private void OnFitSubplotClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe
            && fe.DataContext is TraceChartSeries series
            && DataContext is TraceViewerViewModel vm
            && vm.TryGetPlot(series.SignalKey, out var plot))
        {
            FitSubplot(plot, series, vm);
        }
    }

    private static void FitSubplot(Plot plot, TraceChartSeries series, TraceViewerViewModel vm, IPlotControl? control = null)
    {
        if (TryGetYFitRange(series, out var yMin, out var yMax))
            plot.Axes.SetLimitsY(yMin, yMax);

        var xMax = vm.TotalDuration > 0 ? vm.TotalDuration : 1.0;
        plot.Axes.SetLimitsX(0, xMax);
        vm.ChartViewModel.SyncXAxis(0, xMax);
        series.RefreshCallback?.Invoke();
    }

    private static bool TryGetYFitRange(TraceChartSeries series, out double yMin, out double yMax)
    {
        yMin = double.NaN;
        yMax = double.NaN;

        if (series.ProgressiveSource is not null)
        {
            var (min, max) = series.ProgressiveSource.GetActualYRange();
            if (max - min > 1e-9)
            {
                var pad = (max - min) * 0.1;
                yMin = min - pad;
                yMax = max + pad;
                return true;
            }
        }

        var sig = series.Signal;
        if (sig is not null && sig.Min < sig.Max)
        {
            var pad = (sig.Max - sig.Min) * 0.05;
            yMin = sig.Min - pad;
            yMax = sig.Max + pad;
            return true;
        }

        return false;
    }

    private static bool IsInsideDataArea(WpfPlot pv, Pixel pixel)
    {
        if (pv.Plot?.LastRender is not { } render) return false;
        return render.DataRect.Contains(pixel);
    }
}
