using OxyPlot;
using OxyPlot.Series;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class SignalChartViewModel
{
    /// <summary>
    /// Add a signal to the chart. Creates a new <see cref="LineSeries"/>
    /// with the next palette color. No-op if the signal is already
    /// charted.
    /// </summary>
    /// <param name="signalKey">Unique key ("Message.Signal").</param>
    /// <param name="displayName">Legend label (signal name).</param>
    public void AddSignal(string signalKey, string displayName)
    {
        if (_seriesByKey.ContainsKey(signalKey)) return;

        var color = Palette[_nextColorSlot % Palette.Length];
        _colorIndex[signalKey] = _nextColorSlot;
        _nextColorSlot++;

        var series = new LineSeries
        {
            Title = displayName,
            Color = color,
            StrokeThickness = 1.5,
            LineJoin = LineJoin.Round,
        };

        _seriesByKey[signalKey] = series;
        _displayNames[signalKey] = displayName;
        PlotModel.Series.Add(series);
        EnsureTimer();
        PlotModel.InvalidatePlot(false);
    }

    /// <summary>
    /// Remove a signal from the chart. No-op if the key is unknown.
    /// </summary>
    public void RemoveSignal(string signalKey)
    {
        if (!_seriesByKey.TryGetValue(signalKey, out var series)) return;

        PlotModel.Series.Remove(series);
        _seriesByKey.Remove(signalKey);
        _displayNames.Remove(signalKey);
        _pendingPoints.Remove(signalKey);
        _colorIndex.Remove(signalKey);

        if (_seriesByKey.Count == 0)
        {
            StopTimer();
            _t0 = null;
        }

        PlotModel.InvalidatePlot(false);
    }
    /// <summary>
    /// Remove all signals and clear the chart. Called on DBC reload.
    /// </summary>
    public void Reset()
    {
        StopTimer();
        PlotModel.Series.Clear();
        _seriesByKey.Clear();
        _displayNames.Clear();
        _colorIndex.Clear();
        _pendingPoints.Clear();
        _t0 = null;
        _nextColorSlot = 0;
        PlotModel.InvalidatePlot(false);
    }
}