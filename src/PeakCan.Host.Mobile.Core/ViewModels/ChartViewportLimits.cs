namespace PeakCan.Host.Mobile.Core.ViewModels;

/// <summary>Limits chart zoom before native line rendering becomes visually unstable.</summary>
public static class ChartViewportLimits
{
    /// <summary>The largest supported zoom factor relative to the full X range.</summary>
    public const double DefaultMaxZoomFactor = 1024;

    /// <summary>
    /// Expands a viewport that is narrower than <paramref name="maxZoomFactor"/> permits.
    /// The current viewport center is kept inside the largest zoomable range.
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

        var halfSpan = minimumSpan / 2;
        var center = (requested.Minimum + requested.Maximum) / 2;
        var boundedCenter = Math.Clamp(center, full.Minimum + halfSpan, full.Maximum - halfSpan);
        return new ChartAxisRange(boundedCenter - halfSpan, boundedCenter + halfSpan);
    }

    private static bool IsValid(ChartAxisRange range) =>
        double.IsFinite(range.Minimum)
        && double.IsFinite(range.Maximum)
        && range.Minimum < range.Maximum;
}
