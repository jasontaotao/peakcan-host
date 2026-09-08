namespace PeakCan.Host.Core.Replay;

/// <summary>
/// Bounds out-of-order BLF frames to a small timestamp window. BLF containers
/// may contain frames that are not globally sorted; sorting the whole file
/// would break streaming, so only one bounded window is materialized.
/// </summary>
internal sealed class BlfReorderBuffer
{
    private const double Epsilon = 1e-9;
    private readonly List<ReplayFrame> _pending = new();
    private double? _windowStart;

    public const double WindowSeconds = 1.0;

    public IReadOnlyList<ReplayFrame> Push(ReplayFrame frame)
    {
        if (_windowStart is null)
        {
            _windowStart = Math.Floor(frame.Timestamp);
            _pending.Add(frame);
            return Array.Empty<ReplayFrame>();
        }

        // A frame older than the window start cannot be ordered without
        // unbounded memory. Flush now and treat it as the next bounded window.
        if (frame.Timestamp < _windowStart.Value - Epsilon)
        {
            var lateReady = Flush();
            _windowStart = Math.Floor(frame.Timestamp);
            _pending.Add(frame);
            return lateReady;
        }
        if (frame.Timestamp < _windowStart.Value + WindowSeconds - Epsilon)
        {
            _pending.Add(frame);
            return Array.Empty<ReplayFrame>();
        }

        var ready = _pending.OrderBy(f => f.Timestamp).ToArray();
        _pending.Clear();
        _windowStart = Math.Floor(frame.Timestamp);
        _pending.Add(frame);
        return ready;
    }

    public IReadOnlyList<ReplayFrame> Flush()
    {
        var ready = _pending.OrderBy(f => f.Timestamp).ToArray();
        _pending.Clear();
        _windowStart = null;
        return ready;
    }
}

