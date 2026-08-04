using System.Globalization;
using ScottPlot;
using PeakCan.Host.App.Services.Trace;
using PeakCan.HIL.Core.Analysis;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.Replay;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewerViewModel
{
    /// <summary>
    /// v3.14.3 PATCH: build one chart subplot for one (source, signal)
    /// pair — the shared body for <see cref="PlotSignal(TraceChartSeries)"/>
    /// (placeholder replacement path) and <see cref="PlotSignalFromTableRow"/>
    /// (creation path). Returns the populated <see cref="TraceChartSeries"/>,
    /// or null if no matching frames exist in this source.
    /// <para>
    /// Honors the source per-source CanIdFilter override so
    /// the chart matches what the user sees in the signal table
    /// N column (consistent with the pre-v3.14.3 behavior
    /// where BuildChartSeries applied the same per-source
    /// resolution).
    /// </para>
    /// <para>
    /// v3.62.0 MINOR: migrated from OxyPlot (PlotModel + LineSeries +
    /// EnumTrackerLineSeries + LineAnnotation) to ScottPlot 5.x (Plot +
    /// Scatter over IScatterSource + VerticalLine).
    /// </para>
    /// </summary>
    private TraceChartSeries? BuildOneChartSeriesForSource(
        TraceSource source, Signal sig, uint lookupId, string idHex, string sigName)
    {
        // v3.4.3 PATCH per-source filter override
        var globalAllowed = CanIdListParser.Parse(CanIdFilter).AllowList;
        var perSourceAllowed = CanIdListParser.Parse(source.CanIdFilter).AllowList;
        var effective = perSourceAllowed ?? globalAllowed;

        var frames = _registry.GetFrames(source.SourceId)
            .Where(f => (f.Id & 0x7FFFFFFFu) == lookupId
                        && (effective is null || effective.Contains(f.Id)))
            .OrderBy(f => f.Timestamp)
            .ToList();
        if (frames.Count == 0) return null;

        // v3.62.0 MINOR: Progressive fill — create empty source, start background fill.
        // UI thread returns immediately; ChartFillEngine decodes in background.
        var displayName = source.DisplayName + "." + idHex + "." + sigName;

        // Create empty ProgressiveScatterSource for background fill
        var progSource = new ProgressiveScatterSource();

        // Compute time range from frames (cheap — just timestamps)
        var xValues = frames.Select(f => f.Timestamp).ToArray();

        // Calculate adaptive gap threshold and start background fill
        double gapThreshold = ChartFillEngine.CalculateGapThreshold(frames);
        var fillRequest = new FillRequest(
            SignalKey: idHex + "." + sigName,
            Frames: frames,
            Signal: sig,
            Source: progSource,
            GapThreshold: gapThreshold);

        // Store active request so View can wire RefreshCallback
        _activeFillRequests[idHex + "." + sigName] = fillRequest;

        _fillEngine.Start(idHex + "." + sigName, fillRequest);

        return new TraceChartSeries(
            SignalKey: idHex + "." + sigName,
            DisplayName: displayName,
            Unit: sig.Unit,
            Color: new Color(source.Color.R, source.Color.G, source.Color.B, source.Color.A),
            Plot: null,  // v3.62.0: View creates and owns the Plot
            XValues: xValues,
            YValues: Array.Empty<double>(),
            MinValue: double.NaN,
            MaxValue: double.NaN,
            IsFocused: false,
            IsCollapsed: false,
            SourceId: source.SourceId,
            IsPlotPending: false,
            Source: source,
            Signal: sig,
            ProgressiveSource: progSource);
    }

    /// <summary>v3.62.0 MINOR: Populate a Plot with chart data (Scatter + axes + LabelFormatter).
    /// Called by View code-behind to populate the WpfPlot's internal Plot.</summary>
    public void PopulatePlot(Plot plot, TraceChartSeries series)
    {
        if (series.Signal is null || series.Source is null) return;

        // Add scatter bound to the progressive source
        var scatter = plot.Add.Scatter(series.ProgressiveSource!);
        scatter.LineColor = series.Color;
        scatter.LineWidth = 1.5f;
        scatter.MarkerSize = 6f;
        scatter.MarkerShape = MarkerShape.FilledCircle;
        scatter.MarkerColor = series.Color;

        // X-axis LabelFormatter (wall-clock or elapsed)
        var tickGen = plot.Axes.Bottom.TickGenerator;
        var labelFormatterProp = tickGen.GetType().GetProperty("LabelFormatter");
        if (labelFormatterProp != null && labelFormatterProp.PropertyType == typeof(Func<double, string>))
        {
            // 统一时间格式化：与 AI chat system prompt / 工具 *_label 走同一个
            // TraceTimeFormatter，三路完全一致。
            Func<double, string> formatter = x =>
                TraceTimeFormatter.Format(x, series.Source?.WallClockOrigin);
            labelFormatterProp.SetValue(tickGen, formatter);
        }

        // Y 轴初始范围：数据填充完成后 OnCompleted 回调会精确适配
        plot.Axes.AutoScale();
    }

    private static string FormatCanIdHex(uint id)
    {
        const uint IdeBit = 0x80000000u;
        return (id & IdeBit) == 0
            ? "0x" + id.ToString("X3")
            : "0x" + id.ToString("X8");
    }

    public void PlotSignal(TraceChartSeries series)
    {
        if (series is null) throw new ArgumentNullException(nameof(series));
        if (!series.IsPlotPending) return;

        var dot = series.SignalKey.IndexOf('.');
        if (dot <= 0) return;
        var idHexStr = series.SignalKey.Substring(0, dot);
        if (!idHexStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return;
        if (!uint.TryParse(idHexStr.AsSpan(2),
                           System.Globalization.NumberStyles.HexNumber,
                           null, out var canId)) return;
        var lookupId = canId & 0x7FFFFFFFu;

        var source = _registry.Sources.FirstOrDefault(s => s.SourceId == series.SourceId);
        if (source is null) return;

        var dbc = _dbcService.Current;
        if (dbc is null) return;
        var sigName = series.SignalKey.Substring(dot + 1);
        var sig = dbc.Messages
            .Where(m => (m.Id & 0x7FFFFFFFu) == lookupId)
            .SelectMany(m => m.Signals)
            .FirstOrDefault(s => s.Name == sigName);
        if (sig is null) return;

        var built = BuildOneChartSeriesForSource(source, sig, lookupId, idHexStr, sigName);
        if (built is null) return;

        var idx = ChartViewModel.Series.IndexOf(series);
        if (idx < 0) return;
        ChartViewModel.Series[idx] = built;
        ChartViewModel.SyncYAxes();
        ChartViewModel.SyncXAxis(built.XValues[0], built.XValues[built.XValues.Count - 1]);
    }
}
