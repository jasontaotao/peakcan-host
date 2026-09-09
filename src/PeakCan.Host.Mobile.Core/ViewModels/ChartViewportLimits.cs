namespace PeakCan.Host.Mobile.Core.ViewModels;

/// <summary>Limits chart zoom before native line rendering becomes visually unstable.</summary>
public static class ChartViewportLimits
{
    /// <summary>The largest supported zoom factor relative to the full X range.</summary>
    public const double DefaultMaxZoomFactor = 32;

    /// <summary>
    /// Expands a viewport that is narrower than <paramref name="maxZoomFactor"/> permits.
    /// The current viewport center is preserved when it is inside the full range.
    /// </summary>
    public static ChartAxisRange ClampToMinimumSpan(
        ChartAxisRange requested,
        ChartAxisRange full,
        double maxZoomFactor = DefaultMaxZoomFactor)
    {
        if (!IsValid(requested) || !IsValid(full) || !double.IsFinite(maxZoomFactor) || maxZoomFactor <= 1)
            return requested;

        var fullSpan = full.Maximum - full.Minimum;
        if (fullSpan <= 0) return requested;

        var minimumSpan = fullSpan / maxZoomFactor;
        var requestedSpan = requested.Maximum - requested.Minimum;
        if (requestedSpan >= minimumSpan) return requested;

        var center = Math.Clamp((requested.Minimum + requested.Maximum) / 2, full.Minimum, full.Maximum);
        return new ChartAxisRange(center - minimumSpan / 2, center + minimumSpan / 2);
    }

    private static bool IsValid(ChartAxisRange range) =>
        double.IsFinite(range.Minimum)
        && double.IsFinite(range.Maximum)
        && range.Minimum < range.Maximum;
}
