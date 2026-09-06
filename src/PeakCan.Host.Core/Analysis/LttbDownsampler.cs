namespace PeakCan.Host.Core.Analysis;

/// <summary>
/// v12 Step 2: LTTB (Largest Triangle Three Buckets) downsampling.
/// Preserves extremes and transition edges by selecting the point in
/// each bucket that forms the largest triangle with the previous
/// selected point and the next bucket's average point.
/// <para>
/// Pure function, no dependencies. Thread-safe.
/// </para>
/// </summary>
public static class LttbDownsampler
{
    /// <summary>
    /// Downsample <paramref name="points"/> to at most
    /// <paramref name="maxPoints"/> representative points.
    /// </summary>
    /// <param name="points">Input time-series, sorted by X ascending.</param>
    /// <param name="maxPoints">Target output count. Must be >= 3.
    /// If <paramref name="points"/>.Count <= <paramref name="maxPoints"/>,
    /// returns the input unchanged.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxPoints"/> < 3.
    /// </exception>
    public static IReadOnlyList<(double X, double Y)> Downsample(
        IReadOnlyList<(double X, double Y)> points,
        int maxPoints)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (maxPoints < 3)
            throw new ArgumentOutOfRangeException(nameof(maxPoints), "maxPoints must be >= 3");
        if (points.Count <= maxPoints)
            return points;

        int n = points.Count;
        var result = new List<(double X, double Y)>(maxPoints);

        // Always keep the first point.
        result.Add(points[0]);

        double bucketSize = (double)(n - 2) / (maxPoints - 2);
        int selectedIdx = 0;

        for (int i = 0; i < maxPoints - 2; i++)
        {
            int bucketStart = (int)Math.Floor(i * bucketSize) + 1;
            int bucketEnd = (int)Math.Floor((i + 1) * bucketSize) + 1;
            if (bucketEnd > n - 1) bucketEnd = n - 1;

            // Average of the next bucket (or last point for the final middle bucket).
            double avgX, avgY;
            if (i == maxPoints - 3)
            {
                avgX = points[n - 1].X;
                avgY = points[n - 1].Y;
            }
            else
            {
                int nextStart = (int)Math.Floor((i + 1) * bucketSize) + 1;
                int nextEnd = (int)Math.Floor((i + 2) * bucketSize) + 1;
                if (nextEnd > n - 1) nextEnd = n - 1;

                double sumX = 0, sumY = 0;
                int count = nextEnd - nextStart;
                for (int j = nextStart; j < nextEnd; j++)
                {
                    sumX += points[j].X;
                    sumY += points[j].Y;
                }
                avgX = count > 0 ? sumX / count : points[nextStart].X;
                avgY = count > 0 ? sumY / count : points[nextStart].Y;
            }

            // Select the point in the current bucket with the largest
            // triangle area: |cross product| / 2.
            var prev = points[selectedIdx];
            double maxArea = -1;
            int bestIdx = bucketStart;

            for (int j = bucketStart; j < bucketEnd; j++)
            {
                var curr = points[j];
                // Triangle area = |(prev - avg) x (prev - curr)| / 2
                double area = Math.Abs(
                    (prev.X - avgX) * (curr.Y - prev.Y) -
                    (prev.X - curr.X) * (avgY - prev.Y)
                ) * 0.5;

                if (area > maxArea)
                {
                    maxArea = area;
                    bestIdx = j;
                }
            }

            result.Add(points[bestIdx]);
            selectedIdx = bestIdx;
        }

        // Always keep the last point.
        result.Add(points[n - 1]);

        return result;
    }
}
