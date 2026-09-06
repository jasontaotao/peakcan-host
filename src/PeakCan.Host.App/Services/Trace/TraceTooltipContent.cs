using PeakCan.HIL.Core.Analysis;
using PeakCan.Host.Core.Analysis;

namespace PeakCan.Host.App.Services.Trace;

/// <summary>
/// Pure presentation model for the Trace Viewer hover tooltip.
/// Keeping formatting out of the view makes the CANoe-style
/// name / time / value contract directly testable.
/// </summary>
public sealed record TraceTooltipContent(string DisplayName, string Time, string Value)
{
    /// <summary>Builds tooltip text from a nearest-sample hit.</summary>
    public static TraceTooltipContent Format(
        string displayName,
        double timestampSeconds,
        DateTime? wallClockOrigin,
        double value,
        string? unit)
    {
        var formattedValue = value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(unit))
            formattedValue += " " + unit.Trim();

        return new TraceTooltipContent(
            displayName,
            TraceTimeFormatter.Format(timestampSeconds, wallClockOrigin),
            formattedValue);
    }
}

