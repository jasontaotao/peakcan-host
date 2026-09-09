namespace PeakCan.Host.Mobile.Core.Services;

public static class ChartRenderFilter
{
    public static IReadOnlyList<ChartPoint> ClampToViewport(
        IReadOnlyList<ChartPoint> points,
        double? minimum,
        double? maximum)
    {
        if (points.Count == 0) return points;
        if (minimum is null || maximum is null) return points;
        if (!double.IsFinite(minimum.Value) || !double.IsFinite(maximum.Value) || minimum >= maximum)
            return points;
        var result = new List<ChartPoint>(points.Count);
        foreach (var point in points)
        {
            if (!double.IsFinite(point.Timestamp) || !double.IsFinite(point.Value)) continue;
            if (point.Timestamp < minimum || point.Timestamp > maximum) continue;
            result.Add(point);
        }
        return result;
    }
}
