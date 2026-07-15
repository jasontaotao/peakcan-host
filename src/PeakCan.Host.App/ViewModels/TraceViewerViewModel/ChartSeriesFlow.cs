using System.Globalization;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.Replay;

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
    /// Honors the source's per-source <c>CanIdFilter</c> override so
    /// the chart matches what the user sees in the signal table's
    /// <c>N</c> column (consistent with the pre-v3.14.3 behavior
    /// where <c>BuildChartSeries</c> applied the same per-source
    /// resolution).
    /// </para>
    /// </summary>
    private TraceChartSeries? BuildOneChartSeriesForSource(
        TraceSource source, Signal sig, uint lookupId, string idHex, string sigName)
    {
        // v3.4.3 PATCH per-source filter override: if this source has
        // a non-empty per-source filter, use it as the allow-list;
        // otherwise inherit the global one.
        var globalAllowed = CanIdListParser.Parse(CanIdFilter).AllowList;
        var perSourceAllowed = CanIdListParser.Parse(source.CanIdFilter).AllowList;
        var effective = perSourceAllowed ?? globalAllowed;

        var frames = _registry.GetFrames(source.SourceId)
            .Where(f => (f.Id & 0x7FFFFFFFu) == lookupId
                        && (effective is null || effective.Contains(f.Id)))
            .OrderBy(f => f.Timestamp)
            .ToList();
        if (frames.Count == 0) return null;

        var xs = new double[frames.Count];
        var ys = new double[frames.Count];
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        for (int i = 0; i < frames.Count; i++)
        {
            xs[i] = frames[i].Timestamp;
            ys[i] = SignalDecoder.Decode(frames[i].Data, sig);
            if (ys[i] < min) min = ys[i];
            if (ys[i] > max) max = ys[i];
        }

        var displayName = $"{source.DisplayName}.{idHex}.{sigName}";
        var plotModel = new PlotModel();
        // v3.16.9.2 PATCH: X-axis LabelFormatter formats ticks as wall-clock
        // when source carries a WallClockOrigin (parsed from ASC 'date' header);
        // otherwise falls back to a 3-tier elapsed formatter (>=1d / >=1h / <1h).
        // Spec: docs/superpowers/specs/2026-07-09-trace-viewer-enhancements-design.md
        // §3.4 lines 131-139. Uses InvariantCulture so locale cannot change
        // the 'MM/dd' ordering or the decimal point.
        //
        // NB: DateTimeKind.Local arithmetic does NOT normalize across DST
        // transitions. Traces spanning spring-forward may show one-hour gaps;
        // traces spanning fall-back may show repeated hours. Acceptable per
        // spec §7 (local time is the canonical interpretation of Vector's
        // 'date' header). v3.16.9.2 review-MEDIUM-2.
        //
        // NB: lambda captures the `source` reference, NOT the current
        // WallClockOrigin value (spec §3.4 R2). If source.WallClockOrigin is
        // mutated after this axis is created, the formatter re-resolves on
        // every LabelFormatter call so the new origin takes effect.
        // v3.16.9.2 review-HIGH.
        var bottomAxis = new LinearAxis { Position = AxisPosition.Bottom };
        bottomAxis.LabelFormatter = x =>
        {
            var o = source.WallClockOrigin;
            if (o is not null)
                return (o.Value + TimeSpan.FromSeconds(x))
                    .ToString("MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
            // 3-tier elapsed fallback per spec §3.4 (>=1d / >=1h / <1h).
            // v3.16.9.2 review-MEDIUM-1: explicit InvariantCulture on all
            // branches so locale cannot change the decimal point.
            if (x >= 86400.0)
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:F1}d {1:hh\\:mm\\:ss}",
                    x / 86400.0,
                    TimeSpan.FromSeconds(x));
            if (x >= 3600.0)
                return TimeSpan.FromSeconds(x).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            return TimeSpan.FromSeconds(x).ToString(@"mm\:ss\.f", CultureInfo.InvariantCulture);
        };
        plotModel.Axes.Add(bottomAxis);
        plotModel.Axes.Add(new LinearAxis { Position = AxisPosition.Left });
        // v3.16.4 PATCH BUGFIX: materialize the ItemsSource to a
        // List<DataPoint>. The previous deferred LINQ chain
        // (Enumerable.Range(...).Select(...)) was an IEnumerable that
        // OxyPlot's WPF binding machinery does not enumerate reliably
        // — the LineSeries would render with zero points. Forcing
        // .ToList() materializes the data so OxyPlot gets a stable
        // IList it can render.
        var dataPoints = new List<DataPoint>(frames.Count);
        for (int i = 0; i < frames.Count; i++)
            dataPoints.Add(new DataPoint(xs[i], ys[i]));
        // v3.16.9.2 PATCH: show discrete CAN sample points as circle markers
        // so the user can distinguish "trend line" (interpolation) from
        // "real CAN frame" (discrete event). MarkerSize=3 is small enough
        // not to occlude the line at 1920x1080. Spec §3.6.
        var line = new LineSeries
        {
            Color = source.Color,
            LineStyle = source.StrokeStyle,
            ItemsSource = dataPoints,
            MarkerType = MarkerType.Circle,
            MarkerSize = 3.0,
        };
        plotModel.Series.Add(line);
        // v3.16.9 PATCH: add a vertical LineAnnotation tagged "playback-cursor"
        // so ChartViewModel.UpdatePlaybackCursor (TraceChartViewModel.cs:86-100)
        // can find + reposition the red cursor line on every frame.
        // The cursor is a vertical line spanning the full Y axis at X = 0
        // (start of trace). The companion test
        // BuildOneChartSeriesForSource_CreatesPlaybackCursorLineAnnotation
        // pins this contract.
        // The bug was diagnosed in v3.16.6 release notes (line 42: "LineAnnotation
        // was never created") but never actually fixed.
        plotModel.Annotations.Add(new LineAnnotation
        {
            Type = LineAnnotationType.Vertical,
            X = 0.0,
            Color = OxyColors.Red,
            LineStyle = LineStyle.Solid,
            StrokeThickness = 1.5,
            Tag = "playback-cursor",
        });

        return new TraceChartSeries(
            SignalKey: $"{idHex}.{sigName}",
            DisplayName: displayName,
            Unit: sig.Unit,
            Color: source.Color,
            PlotModel: plotModel,
            XValues: xs,
            YValues: ys,
            MinValue: min,
            MaxValue: max,
            IsFocused: false,
            IsCollapsed: false,
            SourceId: source.SourceId,
            IsPlotPending: false,
            // v3.50.5 PATCH: default PlotController includes a Tracker
            // that shows the (X, Y) data point on hover. Without this
            // the PlotView has no controller → no hover tooltip → user
            // can't see coordinates by clicking on a data point.
            // PlotController lives in OxyPlot core (not OxyPlot.Wpf).
            Controller: new PlotController());
    }

    /// <summary>
    /// Format a CAN ID as a hex string for display: "0x123" for standard
    /// (11-bit, IDE bit clear) and "0x00000123" for extended (29-bit,
    /// IDE bit set). Matches the DBC tab's ID display convention so the
    /// Trace Viewer signal list and the DBC message list line up
    /// visually.
    /// </summary>
    private static string FormatCanIdHex(uint id)
    {
        const uint IdeBit = 0x80000000u;
        return (id & IdeBit) == 0
            ? $"0x{id:X3}"
            : $"0x{id:X8}";
    }

    /// <summary>
    /// v3.14.2 PATCH: build the per-frame PlotModel + XValues + YValues
    /// for a single TraceChartSeries on demand. Called when the user
    /// opts a signal in (clicks the chart row's "Plot" affordance).
    /// The Trace Viewer registers a placeholder per (source, signal) row
    /// at load time and only decodes the per-frame data on user demand.
    /// This is the user-facing fix for the "Add Trace hangs 30+ seconds"
    /// bug — the prior eager build decoded 500K+ frames synchronously on
    /// the UI thread.
    /// <para>
    /// Implementation: matches the eager-build loop body verbatim but
    /// runs once per call instead of once per (source, msg, sig) tuple.
    /// Safe to call multiple times — clears the existing PlotModel first.
    /// </para>
    /// </summary>
    public void PlotSignal(TraceChartSeries series)
    {
        if (series is null) throw new ArgumentNullException(nameof(series));
        if (!series.IsPlotPending) return;  // already plotted

        // v3.14.3 PATCH: shared body via BuildOneChartSeriesForSource.
        // Parse SignalKey ("{idHex}.{sig.Name}") to recover the lookup
        // canId; the IDE-bit mask is applied at lookup time so it
        // matches the BucketFramesByCanId keys.
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

        // SignalKey is "{idHex}.{sig.Name}" — the sig.Name is the
        // LAST segment (post-dot). Lookup the Signal in the DBC.
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

        // Replace the placeholder in place (TraceChartSeries is a record;
        // we mutate the chart via the CollectionViewModel).
        var idx = ChartViewModel.Series.IndexOf(series);
        if (idx < 0) return;
        ChartViewModel.Series[idx] = built;
        // v3.14.2 PATCH: resync Y axes + X axis now that the series
        // has real data. RebuildSignalsCore's initial SyncYAxes ran
        // against the empty placeholder, leaving axes at default
        // (NaN). Re-run after the lazy fill so the chart renders
        // with the correct ranges on the very first opt-in.
        ChartViewModel.SyncYAxes();
        ChartViewModel.SyncXAxis(built.XValues[0], built.XValues[^1]);
    }
}