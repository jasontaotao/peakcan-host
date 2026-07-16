// v3.50.5 PATCH: LineSeries subclass that overrides GetNearestPoint to
// rewrite the tracker text into the four-line tooltip matching the CANoe
// screenshot:
//
//   SignalName
//   decodedText     (= DBC VAL_ table text when present, else F2 numeric)
//   yValue          (decoded engineering value, raw)
//   t = X.XXX s
//
// v3.50.5 LESSON captured: OxyPlot 2.2.0 does NOT expose ITrackerConverter
// (the spec assumed it did). The internal pipeline is:
//   base.GetNearestPoint(...) -> builds TrackerHitResult with .Text set via
//   StringHelper.Format(TrackerFormatString, item, Title, XAxis.Title,
//   XAxis.GetValue(X), YAxis.Title, YAxis.GetValue(Y))
// The .Text field is the public string the WPF TrackerControl renders.
// Override GetNearestPoint, mutate .Text after base call, return the
// modified hit.
//
// Sister of the W38 v3.50.4 PATCH `PlotController` namespace lesson
// (OxyPlot core, not OxyPlot.Wpf): LineSeries also lives in
// OxyPlot.Series, so the subclass declaration stays here without
// pulling in OxyPlot.Wpf.
using System.Globalization;
using OxyPlot;
using OxyPlot.Series;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

public sealed class EnumTrackerLineSeries : LineSeries
{
    private readonly Signal _signal;
    private readonly Func<DbcDocument?> _dbcProvider;

    public EnumTrackerLineSeries(Signal signal, Func<DbcDocument?> dbcProvider)
    {
        _signal = signal;
        _dbcProvider = dbcProvider;
    }

    public override TrackerHitResult GetNearestPoint(ScreenPoint point, bool interpolate)
    {
        var hit = base.GetNearestPoint(point, interpolate);
        if (hit is null) return hit;

        // Replace the default "{0}\n{1}\n{2} ..." text with our 4-line layout.
        var yVal = hit.DataPoint.Y;
        var xVal = hit.DataPoint.X;
        var dbc = _dbcProvider();
        var decoded = dbc is not null
            ? (SignalDecoder.TryDecodeEnumText(_signal, yVal, dbc)
               ?? SignalFormatter.FormatValue(_signal.Factor, yVal))
            : SignalFormatter.FormatValue(_signal.Factor, yVal);

        // v3.50.6 PATCH: factor-derived precision for y-value line,
        // sister of WatchedSignalRow.LatestText (uses SignalFormatter too).
        var yDisplay = SignalFormatter.FormatValue(_signal.Factor, yVal);
        hit.Text = $"{_signal.Name}\n{decoded}\n{yDisplay}\nt = {xVal:0.000}s";
        return hit;
    }
}