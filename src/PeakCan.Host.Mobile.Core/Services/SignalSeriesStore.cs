namespace PeakCan.Host.Mobile.Core.Services;

/// <summary>
/// Bounded, thread-safe storage for one signal's chart history. Capacity is the
/// maximum number of representative samples; when exceeded, adjacent samples are
/// progressively thinned instead of dropping the beginning of a long trace.
/// Rendering uses real min/max samples in time buckets to keep signal activity visible.
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
    /// Selects real minimum and maximum samples in time buckets across the full
    /// range, keeping every visible bucket represented by actual samples.
    /// </summary>
    public IReadOnlyList<ChartPoint> GetViewportRenderPoints(int pointCount)
    {
        if (pointCount <= 0) return [];
        lock (_gate)
        {
            if (_samples.Count == 0) return [];
            return ViewportLocked(0, _samples.Count, pointCount);
        }
    }
    /// <summary>
    /// Selects real minimum and maximum samples in visible time buckets. This avoids
    /// gaps that can make active signal ranges look like missing communication.
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

            return ViewportLocked(first, last, pointCount);
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
    /// Buckets the interior samples by time while always retaining both viewport
    /// endpoints. Each bucket emits its real minimum and maximum sample, so no active
    /// time range is silently removed by downsampling.
    /// </summary>
    private List<ChartPoint> ViewportLocked(int first, int last, int pointCount)
    {
        var count = last - first;
        if (count == 0) return [];
        if (pointCount < 3 || count < 3)
        {
            return
            [
                new(_samples[first].Timestamp, _samples[first].Value),
                new(_samples[last - 1].Timestamp, _samples[last - 1].Value),
            ];
        }

        var result = new List<ChartPoint>(pointCount)
        {
            new(_samples[first].Timestamp, _samples[first].Value),
        };

        var innerFirst = first + 1;
        var innerLast = last - 1;
        var innerCount = innerLast - innerFirst;
        if (innerCount <= 0)
        {
            result.Add(new(_samples[last - 1].Timestamp, _samples[last - 1].Value));
            return result;
        }

        var bucketCount = Math.Max(1, (pointCount - 2) / 2);
        var startTime = _samples[innerFirst].Timestamp;
        var endTime = _samples[innerLast - 1].Timestamp;
        var width = endTime > startTime ? (endTime - startTime) / bucketCount : 0;
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

        for (var i = innerFirst; i < innerLast; i++)
        {
            var sample = _samples[i];
            var bucket = width > 0
                ? Math.Clamp((int)((sample.Timestamp - startTime) / width), 0, bucketCount - 1)
                : 0;

            if (bucket != currentBucket)
            {
                AppendBucket();
                currentBucket = bucket;
                hasBucket = false;
            }

            if (!hasBucket || sample.Value <= min)
            {
                min = sample.Value;
                minAt = sample.Timestamp;
            }
            if (!hasBucket || sample.Value >= max)
            {
                max = sample.Value;
                maxAt = sample.Timestamp;
            }
            hasBucket = true;
        }

        AppendBucket();
        result.Add(new(_samples[last - 1].Timestamp, _samples[last - 1].Value));
        return result;
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
            if (!hasBucket || value <= min)
            {
                min = value;
                minAt = sample.Timestamp;
            }
            if (!hasBucket || value >= max)
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
