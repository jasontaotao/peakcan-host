using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceChartViewModel
{
    // Flow B: Playback (v3.16.9 + v3.16.9.1 PATCH).
    // Methods + throttling state moved verbatim from TraceChartViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - UpdatePlaybackCursor -> PlaybackCursorX (property, main)
    //                          -> Series (state, main)
    //                          -> InvalidatePlotCallCount (property exposed from this partial's [ObservableProperty])
    //   - SetTotalDuration -> TotalDuration (property, main)
    //
    // State co-location principle: the 5 throttling fields below are
    // private and only read/written by UpdatePlaybackCursor (and the
    // test via InvalidatePlotCallCount). They co-locate with their
    // only consumer per the W3-W7 helper-co-location lesson.

    // v3.16.9 PATCH: throttling state for UpdatePlaybackCursor. The
    // playback timer fires every 1 ms (ReplayTimeline.OnTick period=1),
    // but OxyPlot.PlotModel.InvalidatePlot on every 1 ms call causes
    // the WPF window to freeze (the layout pass for the chart cannot
    // keep up with 1000 plot invalidations / second per series).
    // Throttle to one update per ~16 ms (60 fps) — the human eye cannot
    // distinguish 60 fps cursor motion from 1000 fps cursor motion, and
    // 60 fps is the WPF default render cadence.
    // v3.16.9.1 PATCH (code-review H1): use Stopwatch ticks (monotonic,
    // high-resolution, immune to wall-clock NTP/clock-jump adjustments)
    // instead of DateTime.UtcNow. The project already uses Stopwatch in
    // RateLimitedSendService.cs:50,105,110,130 — using it here is
    // consistent with existing patterns. DateTime.UtcNow would silently
    // disable the throttle on a clock-jump backward, re-creating the
    // original freeze bug.
    // v3.16.9.1 PATCH: sentinel value for "never invalidated". Must be 0
    // (not long.MinValue) because (Stopwatch.GetTimestamp() - long.MinValue)
    // overflows to a NEGATIVE number on the first call, which would
    // cause elapsedMs < 16 to be FALSE (negative number is < 16 is true,
    // so the throttle would skip the first invalidate). 0 means
    // "uninitialized" — the first call's elapsedMs will be a large
    // positive number (current ticks - 0), and the throttle will allow
    // the first invalidate.
    private long _lastCursorInvalidateTicks = 0L;
    private double _lastCursorX = double.NaN;
    private const double CursorInvalidateIntervalMs = 16.0;
    private static readonly double StopwatchTicksToMs = 1000.0 / Stopwatch.Frequency;
    // v3.16.9.1 PATCH (code-review M2): test hook. The throttle test
    // needs an observable InvalidatePlot call count; without this,
    // removing the throttle would still pass the test (false-positive
    // green). Counter increments inside the foreach so per-series
    // invalidations are summed.
    [ObservableProperty]
    private int _invalidatePlotCallCount;

    public void UpdatePlaybackCursor(double x)
    {
        PlaybackCursorX = x;
        // Skip the actual InvalidatePlot call if either:
        // (a) the new X is the same as the last-rendered X (no movement),
        //     which happens when OnTick emits multiple frames at the
        //     same timestamp (rounded values), or
        // (b) less than 16 ms (Stopwatch ticks) have passed since the
        //     last invalidate. Using Stopwatch (not DateTime) avoids
        //     wall-clock-jump disarming the throttle.
        // Without (a), a duplicate-timestamp frame burst would burn the
        // full 1000 fps invalidate rate. Without (b), the user's
        // window freezes mid-playback. Both are empirical findings from
        // v3.16.9 PATCH user reproduction: "clicked Play and the window
        // froze" — root-caused to UpdatePlaybackCursor invalidating at
        // the timer cadence (1 ms) instead of the render cadence (16 ms).
        var nowTicks = Stopwatch.GetTimestamp();
        var elapsedMs = (nowTicks - _lastCursorInvalidateTicks) * StopwatchTicksToMs;
        if (x == _lastCursorX || elapsedMs < CursorInvalidateIntervalMs)
            return;
        _lastCursorInvalidateTicks = nowTicks;
        _lastCursorX = x;
        foreach (var s in Series)
        {
            var cursor = s.PlotModel.Annotations.OfType<OxyPlot.Annotations.LineAnnotation>()
                .FirstOrDefault(a => a.Tag as string == "playback-cursor");
            if (cursor != null)
            {
                cursor.X = x;
                s.PlotModel.InvalidatePlot(false);
                InvalidatePlotCallCount++;
            }
        }
    }

    public void SetTotalDuration(double seconds) => TotalDuration = seconds;
}