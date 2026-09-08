using System.Collections.Concurrent;

namespace PeakCan.Host.Mobile.Core.Services;

/// <summary>
/// Bounded, thread-safe storage for one signal's raw samples. Rendering uses
/// min/max buckets so very large traces remain responsive on mobile.
/// </summary>
public sealed class SignalSeriesStore
{
    public const int DefaultCapacity = 300_000;
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Queue<SignalSample> _samples;

    public SignalSeriesStore(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _samples = new Queue<SignalSample>(Math.Min(capacity, 4_096));
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
            _samples.Enqueue(new SignalSample(timestamp, value));
            while (_samples.Count > _capacity)
                _samples.Dequeue();
        }
    }

    /// <summary>
    /// Atomically replaces buffered samples, discards non-finite input, sorts by
    /// timestamp, and retains the most recent samples within capacity. Used by
    /// file backfill so a late chart selection can display the complete trace
    /// without mixing partially loaded input.
    /// </summary>
    public void ReplaceSamples(IEnumerable<SignalSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var ordered = samples
            .Where(s => double.IsFinite(s.Timestamp) && double.IsFinite(s.Value))
            .OrderBy(s => s.Timestamp)
            .ToArray();

        lock (_gate)
        {
            _samples.Clear();
            foreach (var sample in ordered.Skip(Math.Max(0, ordered.Length - _capacity)))
                _samples.Enqueue(sample);
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
            return RenderLocked(_samples.Min(s => s.Timestamp), _samples.Max(s => s.Timestamp), bucketCount);
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




