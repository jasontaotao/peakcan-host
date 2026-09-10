namespace PeakCan.Host.Mobile.Core.Services;

/// <summary>
/// Bounded, thread-safe storage for one signal's chart history. Capacity is the
/// maximum number of representative samples; when exceeded, adjacent samples are
/// progressively thinned instead of dropping the beginning of a long trace.
/// Rendering uses min/max buckets or visually weighted adaptive sampling for responsive mobile charts.
/// </summary>
public sealed class SignalSeriesStore
{
    public const int DefaultCapacity = 300_000;
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly List<SignalSample> _samples;

    public SignalSeriesStore(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _samples = new List<SignalSample>(Math.Min(capacity, 4_096));
    }

    public int Count
    {
        get { lock (_gate) return _samples.Count; }
    }

    public void Add(double timestamp, double value)
    {
        if (!double.IsFinite(timestamp) || !double.IsFinite(value)) return;
        lock (_gate)
        {
            InsertSortedLocked(new SignalSample(timestamp, value));
            if (_samples.Count > _capacity)
                ThinLocked();
        }
    }

    /// <summary>
    /// Atomically replaces buffered samples, discards non-finite input, sorts by
    /// timestamp, and thins excess samples while preserving both ends of the
    /// full timestamp range. Used by file backfill so a late chart selection can
    /// display the complete trace instead of only future frames.
    /// </summary>
    public void ReplaceSamples(IEnumerable<SignalSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var ordered = samples
            .Where(s => double.IsFinite(s.Timestamp) && double.IsFinite(s.Value))
            .OrderBy(s => s.Timestamp)
            .ToList();

        lock (_gate)
        {
            _samples.Clear();
            _samples.AddRange(ordered);
            while (_samples.Count > _capacity)
                ThinLocked();
        }
    }

    public void Clear()
    {
        lock (_gate) _samples.Clear();
    }

    public IReadOnlyList<ChartPoint> GetRenderPoints(int bucketCount)
    {
        lock (_gate)
        {
            if (_samples.Count == 0) return [];
            return RenderLocked(_samples[0].Timestamp, _samples[^1].Timestamp, bucketCount);
        }
    }

    public IReadOnlyList<ChartPoint> GetRenderPoints(double start, double end, int bucketCount)
    {
        if (start > end) return [];
        lock (_gate)
        {
            if (_samples.Count == 0) return [];
            return RenderLocked(start, end, bucketCount);
        }
    }
    /// <summary>
    /// Selects actual samples across the full range using visually weighted adaptive
    /// sampling, keeping the rendered path as a line between real samples.
    /// </summary>
    public IReadOnlyList<ChartPoint> GetViewportRenderPoints(int pointCount)
    {
        lock (_gate)
        {
            if (_samples.Count == 0) return [];
            return AdaptiveLocked(0, _samples.Count, pointCount);
        }
    }
    /// <summary>
    /// Selects actual samples in the visible viewport using visually weighted adaptive
    /// sampling. Flat dense runs are thinned aggressively while large value changes
    /// are retained, and the path avoids painting a filled envelope when zoomed.
    /// </summary>
    public IReadOnlyList<ChartPoint> GetViewportRenderPoints(double start, double end, int pointCount)
    {
        if (start > end || pointCount <= 0) return [];
        lock (_gate)
        {
            if (_samples.Count == 0) return [];

            var first = 0;
            while (first < _samples.Count && _samples[first].Timestamp < start) first++;
            var last = first;
            while (last < _samples.Count && _samples[last].Timestamp <= end) last++;
            var count = last - first;
            if (count == 0) return [];
            if (count <= pointCount)
            {
                var result = new List<ChartPoint>(count);
                for (var i = first; i < last; i++)
                    result.Add(new ChartPoint(_samples[i].Timestamp, _samples[i].Value));
                return result;
            }

            return AdaptiveLocked(first, last, pointCount);
        }
    }

    private sealed class SampleTimestampComparer : IComparer<SignalSample>
    {
        public int Compare(SignalSample x, SignalSample y) => x.Timestamp.CompareTo(y.Timestamp);
    }

    private void InsertSortedLocked(SignalSample sample)
    {
        if (_samples.Count == 0 || sample.Timestamp >= _samples[^1].Timestamp)
        {
            _samples.Add(sample);
            return;
        }

        var index = _samples.BinarySearch(0, _samples.Count, sample, new SampleTimestampComparer());
        if (index < 0) index = ~index;
        _samples.Insert(index, sample);
    }

    /// <summary>
    /// Visually weighted sampling. Dense, flat runs are reduced aggressively while
    /// samples that form large triangles with their current neighbours -- peaks,
    /// valleys, steps and other high-change points -- are retained first.
    /// </summary>
    private List<ChartPoint> AdaptiveLocked(int first, int last, int pointCount)
    {
        var count = last - first;
        if (pointCount < 3 || count < 3)
        {
            return
            [
                new(_samples[first].Timestamp, _samples[first].Value),
                new(_samples[last - 1].Timestamp, _samples[last - 1].Value),
            ];
        }

        double minimumTime = _samples[first].Timestamp;
        double maximumTime = _samples[last - 1].Timestamp;
        double minimumValue = double.MaxValue;
        double maximumValue = double.MinValue;
        for (var i = first; i < last; i++)
        {
            if (_samples[i].Value < minimumValue) minimumValue = _samples[i].Value;
            if (_samples[i].Value > maximumValue) maximumValue = _samples[i].Value;
        }

        var timeSpan = maximumTime - minimumTime;
        var valueSpan = maximumValue - minimumValue;
        if (!double.IsFinite(timeSpan) || timeSpan <= 0) timeSpan = 1;
        if (!double.IsFinite(valueSpan) || valueSpan <= 0) valueSpan = 1;

        var x = new double[count];
        var y = new double[count];
        for (var i = 0; i < count; i++)
        {
            x[i] = (_samples[first + i].Timestamp - minimumTime) / timeSpan;
            y[i] = (_samples[first + i].Value - minimumValue) / valueSpan;
        }

        var previous = new int[count];
        var next = new int[count];
        var area = new double[count];
        var removed = new bool[count];
        var candidates = new PriorityQueue<int, double>(count);

        for (var i = 0; i < count; i++)
        {
            previous[i] = i - 1;
            next[i] = i + 1 == count ? -1 : i + 1;
        }

        double TriangleArea(int left, int middle, int right)
        {
            var result = Math.Abs(
                (x[right] - x[left]) * (y[middle] - y[left])
                - (x[middle] - x[left]) * (y[right] - y[left]));

            return double.IsFinite(result) ? result : 0;
        }

        for (var i = 1; i < count - 1; i++)
        {
            area[i] = TriangleArea(previous[i], i, next[i]);
            candidates.Enqueue(i, area[i]);
        }

        var remaining = count;
        var budget = Math.Min(pointCount, count);
        while (remaining > budget && candidates.TryDequeue(out var index, out var priority))
        {
            if (removed[index] || area[index] != priority) continue;

            var left = previous[index];
            var right = next[index];
            removed[index] = true;
            remaining--;

            if (left >= 0) next[left] = right;
            if (right >= 0) previous[right] = left;

            if (left > 0)
            {
                area[left] = TriangleArea(previous[left], left, right);
                candidates.Enqueue(left, area[left]);
            }

            if (right < count - 1 && right > 0)
            {
                area[right] = TriangleArea(left, right, next[right]);
                candidates.Enqueue(right, area[right]);
            }
        }

        var sampled = new List<ChartPoint>(remaining);
        for (var i = 0; i >= 0 && i < count; i = next[i])
            sampled.Add(new ChartPoint(_samples[first + i].Timestamp, _samples[first + i].Value));

        return sampled;
    }

    private void ThinLocked()
    {
        var first = _samples[0];
        var last = _samples[^1];
        var compacted = new List<SignalSample>((_samples.Count / 2) + 3);
        for (var i = 0; i < _samples.Count; i += 2)
        {
            if (i + 1 >= _samples.Count)
            {
                compacted.Add(_samples[i]);
                break;
            }

            // Keep the sample that introduces more change from the last retained
            // point. This preserves range boundaries and large transients better
            // than blindly dropping the oldest half.
            var previousValue = compacted.Count == 0 ? _samples[i].Value : compacted[^1].Value;
            var candidate = _samples[i];
            var second = _samples[i + 1];
            var firstDelta = Math.Abs(candidate.Value - previousValue);
            var secondDelta = Math.Abs(second.Value - previousValue);
            compacted.Add(firstDelta >= secondDelta ? candidate : second);
        }

        if (compacted.Count == 0 || compacted[0].Timestamp != first.Timestamp)
            compacted.Insert(0, first);
        if (compacted[^1].Timestamp != last.Timestamp)
            compacted.Add(last);

        _samples.Clear();
        _samples.AddRange(compacted);
    }

    private List<ChartPoint> RenderLocked(double start, double end, int bucketCount)
    {
        if (bucketCount <= 0) return [];

        var result = new List<ChartPoint>(bucketCount * 2);
        var width = end > start ? (end - start) / bucketCount : 0;
        var currentBucket = -1;
        var hasBucket = false;
        var min = double.MaxValue;
        var max = double.MinValue;
        var minAt = 0.0;
        var maxAt = 0.0;

        void AppendBucket()
        {
            if (!hasBucket) return;
            if (min == max)
            {
                result.Add(new ChartPoint(minAt, min));
            }
            else if (minAt <= maxAt)
            {
                result.Add(new ChartPoint(minAt, min));
                result.Add(new ChartPoint(maxAt, max));
            }
            else
            {
                result.Add(new ChartPoint(maxAt, max));
                result.Add(new ChartPoint(minAt, min));
            }
        }

        foreach (var sample in _samples)
        {
            if (sample.Timestamp < start || sample.Timestamp > end) continue;
            var bucket = width > 0
                ? Math.Clamp((int)((sample.Timestamp - start) / width), 0, bucketCount - 1)
                : 0;

            if (bucket != currentBucket)
            {
                AppendBucket();
                currentBucket = bucket;
                hasBucket = false;
            }

            var value = sample.Value;
            if (!hasBucket || value < min)
            {
                min = value;
                minAt = sample.Timestamp;
            }
            if (!hasBucket || value > max)
            {
                max = value;
                maxAt = sample.Timestamp;
            }
            hasBucket = true;
        }

        AppendBucket();
        return result;
    }
}
