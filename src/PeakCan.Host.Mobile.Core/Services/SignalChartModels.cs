namespace PeakCan.Host.Mobile.Core.Services;

/// <summary>One timestamped signal value used by chart rendering.</summary>
public readonly record struct SignalSample(double Timestamp, double Value);

/// <summary>A render-ready chart point after min/max downsampling.</summary>
public readonly record struct ChartPoint(double Timestamp, double Value);

/// <summary>A vertical time cursor with optional chart Y bounds.</summary>
public readonly record struct ChartCursor(double Timestamp, double? Minimum = null, double? Maximum = null);
