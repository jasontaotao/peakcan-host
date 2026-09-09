namespace PeakCan.Host.Mobile.Core.ViewModels;

/// <summary>A finite, increasing one-dimensional axis range.</summary>
public readonly record struct ChartAxisRange(double Minimum, double Maximum);

/// <summary>Captures and restores chart X zoom without depending on a chart library.</summary>
public sealed class ChartZoomState
{
    public ChartAxisRange? XRange { get; private set; }

    public bool Capture(double? minimum, double? maximum)
    {
        if (minimum is not { } min || maximum is not { } max)
            return false;
        if (!double.IsFinite(min) || !double.IsFinite(max) || min >= max)
            return false;

        XRange = new ChartAxisRange(min, max);
        return true;
    }

    public bool TryGet(out ChartAxisRange range)
    {
        if (XRange is { } captured)
        {
            range = captured;
            return true;
        }

        range = default;
        return false;
    }

    public void Reset() => XRange = null;
}
