using System;
using System.Collections.Generic;
using ScottPlot;
using ScottPlot.Interactivity;
using ScottPlot.Interactivity.UserActions;

namespace PeakCan.Host.App.Services.Trace;

/// <summary>
/// Appended to each subplot's <see cref="UserInputProcessor.UserActionResponses"/>.
/// When wheel/drag changes an X axis, broadcast the new range to all other subplots
/// (through <c>TraceChartViewModel.SyncXAxis</c>, excluding the initiator).
/// Drag motion is throttled; mouse-up always broadcasts pending state.
/// </summary>
public sealed class SharedXAxisSyncResponse : IUserActionResponse
{
    private readonly string _signalKey;
    private readonly Action<double, double, string> _broadcast;
    private readonly Func<bool> _isEnabled;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeSpan _throttle = TimeSpan.FromMilliseconds(40);

    private bool _isDragging;
    private DateTime _lastBroadcast = DateTime.MinValue;
    private (double Min, double Max)? _pending;

    public SharedXAxisSyncResponse(
        string signalKey,
        Action<double, double, string> broadcast,
        Func<bool> isEnabled,
        Func<DateTime>? utcNow = null)
    {
        _signalKey = signalKey;
        _broadcast = broadcast;
        _isEnabled = isEnabled;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public ResponseInfo Execute(IPlotControl control, IUserAction action, KeyboardState keys)
    {
        if (!_isEnabled()) return ResponseInfo.NoActionRequired;

        bool isDiscrete = action is MouseWheelUp or MouseWheelDown;
        bool isDragStart = action is LeftMouseDown;
        bool isDragMove = action is MouseMove && _isDragging;
        bool isDragEnd = action is LeftMouseUp;

        if (isDragStart)
            _isDragging = true;

        if (!isDiscrete && !isDragMove && !isDragEnd)
            return ResponseInfo.NoActionRequired;

        var xAxis = control.Plot.Axes.Bottom;
        _pending = (xAxis.Min, xAxis.Max);
        var now = _utcNow();

        if (isDiscrete || isDragEnd || now - _lastBroadcast >= _throttle)
        {
            _broadcast(_pending.Value.Min, _pending.Value.Max, _signalKey);
            _pending = null;
            _lastBroadcast = now;
        }

        if (isDragEnd)
            _isDragging = false;

        return ResponseInfo.NoActionRequired;
    }

    public void ResetState(IPlotControl control)
    {
        _isDragging = false;
        _pending = null;
        _lastBroadcast = DateTime.MinValue;
    }
}
