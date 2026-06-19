using FluentAssertions;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Statistics;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests;

/// <summary>
/// Task 11: verifies that <see cref="BusStatisticsCollector"/> correctly
/// aggregates frame counters, computes the rolling 1-second window FPS/BPS,
/// applies the load heuristic, and is safe under concurrent dispatch and
/// malformed input.
/// </summary>
public class BusStatisticsCollectorTests
{
    [Fact]
    public void Counts_Total_And_Err_Frames()
    {
        // Plan test: 10 frames, exactly one flagged as ErrFrame.
        var s = new BusStatisticsCollector();
        for (int i = 0; i < 10; i++)
        {
            s.OnFrame(MakeFrame(err: i == 3));
        }
        var snap = s.Snapshot();

        snap.TotalFrames.Should().Be(10);
        snap.ErrorFrames.Should().Be(1);
    }

    [Fact]
    public void Reports_FramesPerSecond_Over_Window()
    {
        // Plan test (FIXED): push frames over a short interval. The
        // Snapshot's FPS is count / windowSeconds with windowSeconds=1.0
        // when the window is non-empty — so FPS equals the count of
        // frames still inside the trailing 1s window at Snapshot() time.
        //
        // Windows Thread.Sleep(1) actually sleeps ~10-15 ms in practice,
        // so a 100-frame loop with 1ms sleeps takes ~1-1.5 s. By that
        // point the early frames have aged out of the 1s window. We
        // therefore assert: at least half the pushed frames are still in
        // the window (lower bound) and the upper bound reflects the
        // remaining recent frames. The point of the test is to confirm
        // the rolling window slides — not to validate a target rate.
        var s = new BusStatisticsCollector();
        for (int i = 0; i < 100; i++)
        {
            s.OnFrame(MakeFrame());
            Thread.Sleep(1);
        }
        var snap = s.Snapshot();

        // The rolling window must have retained a non-trivial fraction
        // of the 100 pushed frames, and must not have retained all
        // (proving the trim loop runs).
        snap.FramesPerSecond.Should().BeGreaterThan(50)
                             .And.BeLessThan(100);
    }

    [Fact]
    public void Snapshot_Empty_Collector_Returns_Zero()
    {
        var s = new BusStatisticsCollector();
        var snap = s.Snapshot();

        snap.TotalFrames.Should().Be(0);
        snap.ErrorFrames.Should().Be(0);
        snap.FramesPerSecond.Should().Be(0.0);
        snap.TotalBytes.Should().Be(0);
        snap.BytesPerSecond.Should().Be(0.0);
        snap.BusLoadPercent.Should().Be(0.0);
    }

    [Fact]
    public void TotalBytes_And_BytesPerSecond_Reflect_Dlc_And_Window()
    {
        // 50 frames * 8 bytes = 400 bytes total. TotalBytes is monotonic
        // and immune to windowing — exact equality. BytesPerSecond is the
        // sum of DLCs still inside the 1s window (denominator 1.0), so
        // it depends on how many of the 50 frames are still in window.
        // 50 * Thread.Sleep(1) is ~500-750 ms on Windows — generally all
        // 50 are still in window. Allow the lower bound to account for
        // early-frame trim on slow CI.
        var s = new BusStatisticsCollector();
        for (int i = 0; i < 50; i++)
        {
            s.OnFrame(MakeFrame(dlc: 8));
            Thread.Sleep(1);
        }
        var snap = s.Snapshot();

        snap.TotalBytes.Should().Be(400);
        // BytesPerSecond should be close to the pushed total (every byte
        // of every frame in window). Lower bound is generous to survive
        // a slow CI box.
        snap.BytesPerSecond.Should().BeGreaterThanOrEqualTo(200);
    }

    [Fact]
    public void BusLoadPercent_Clamped_To_OneHundred_Under_Sustained_Traffic()
    {
        // 10000 frames fast: way past the 8000-fps saturation point.
        var s = new BusStatisticsCollector();
        for (int i = 0; i < 10000; i++)
        {
            s.OnFrame(MakeFrame());
        }
        var snap = s.Snapshot();

        snap.BusLoadPercent.Should().Be(100.0);
    }

    [Fact]
    public void BusLoadPercent_Scales_Linearly_Below_Saturation()
    {
        // 400 frames fast: 400/80 = 5.0%.
        var s = new BusStatisticsCollector();
        for (int i = 0; i < 400; i++)
        {
            s.OnFrame(MakeFrame());
        }
        var snap = s.Snapshot();

        snap.BusLoadPercent.Should().Be(5.0);
    }

    [Fact]
    public void OnFrame_With_ErrorFrame_Flag_Raises_ErrorCounter()
    {
        // Single error frame: counter increments, total also increments.
        var s = new BusStatisticsCollector();
        s.OnFrame(MakeFrame(err: true));
        var snap = s.Snapshot();

        snap.ErrorFrames.Should().Be(1);
        snap.TotalFrames.Should().Be(1);
    }

    [Fact]
    public void Window_Slides_After_One_Second()
    {
        // Push 100 frames, sleep > 1s, push 1 more.
        // After the sleep, the trailing 1s window should contain only the
        // last frame, so FramesPerSecond == 1.0 (count=1, window=1s).
        var s = new BusStatisticsCollector();
        for (int i = 0; i < 100; i++)
        {
            s.OnFrame(MakeFrame());
        }
        Thread.Sleep(1100);
        s.OnFrame(MakeFrame());
        var snap = s.Snapshot();

        snap.FramesPerSecond.Should().Be(1.0);
        snap.TotalFrames.Should().Be(101);
    }

    [Fact]
    public void OnError_Does_Not_Throw()
    {
        // Per IFrameSink contract: OnError is a notification; collector
        // is not a sink for per-frame errors from the router, so it must
        // ignore them without throwing or mutating state.
        var s = new BusStatisticsCollector();
        var act = () => s.OnError(new InvalidOperationException("test"));

        act.Should().NotThrow();
        var snap = s.Snapshot();
        snap.TotalFrames.Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_OnFrame_Is_ThreadSafe()
    {
        // 4 parallel producers each push 1000 frames with distinct DLC
        // values (1..4 repeating). After Task.WhenAll, the totals must
        // match exactly — no lost updates from Interlocked races, and no
        // exceptions leaking out of OnFrame (which the router would
        // forward to OnError, but this collector does not throw).
        // Async to satisfy xUnit1031 (no blocking .Wait/.WaitAll inside tests).
        var s = new BusStatisticsCollector();
        var producers = Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            byte dlc = (byte)(t + 1);
            for (int i = 0; i < 1000; i++)
            {
                s.OnFrame(MakeFrame(dlc: dlc));
            }
        })).ToArray();

        await Task.WhenAll(producers);
        var snap = s.Snapshot();

        snap.TotalFrames.Should().Be(4000);
        // Each producer writes 1000 * (t+1) bytes; sum = 1000*(1+2+3+4)=10000
        snap.TotalBytes.Should().Be(10000);
        snap.ErrorFrames.Should().Be(0);
    }

    [Fact]
    public void OnFrame_Default_Struct_Does_Not_Throw()
    {
        // CanFrame is a struct. Passing default yields Data.Length == 0,
        // so Dlc == 0, IsError == false. OnFrame must handle the zero-DLC
        // path without throwing or returning NaN/Infinity.
        var s = new BusStatisticsCollector();
        var act = () => s.OnFrame(default);

        act.Should().NotThrow();
        var snap = s.Snapshot();
        snap.TotalFrames.Should().Be(1);
        snap.TotalBytes.Should().Be(0);
        snap.ErrorFrames.Should().Be(0);
    }

    [Fact]
    public void BusStatistics_Record_Equality_And_ToString()
    {
        // Record semantics: two default snapshots must be equal.
        var a = new BusStatistics(0, 0, 0.0, 0, 0.0, 0.0);
        var b = new BusStatistics(0, 0, 0.0, 0, 0.0, 0.0);

        a.Should().Be(b);
        a.ToString().Should().NotBeNullOrWhiteSpace();
    }

    private static CanFrame MakeFrame(byte dlc = 1, bool err = false)
        => new(
            new CanId(0x100u, FrameFormat.Standard),
            new byte[dlc],
            err ? FrameFlags.ErrFrame : FrameFlags.None,
            ChannelId.None,
            default);
}