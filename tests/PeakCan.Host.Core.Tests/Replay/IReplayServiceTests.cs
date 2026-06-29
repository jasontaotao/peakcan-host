using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.Core.Replay;
using System.Globalization;
using System.Reflection;
using Xunit;

namespace PeakCan.Host.Core.Tests.Replay;

internal sealed class FakeReplayFrameSink : IReplayFrameSink
{
    public List<ReplayFrame> Sent { get; } = new();
    public ValueTask SendFrameAsync(ReplayFrame frame, CancellationToken ct = default)
    {
        Sent.Add(frame);
        return ValueTask.CompletedTask;
    }
}

public class IReplayServiceTests
{
    private static string WriteTempAsc(string content)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"test-{Guid.NewGuid():N}.asc");
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// v1.4.0 MINOR: LoadAsync parses file and populates TotalDuration.
    /// </summary>
    [Fact]
    public async Task LoadAsync_ParsesFileAndPopulatesTotalDuration()
    {
        var path = WriteTempAsc(@"
date Wed Jun 28 10:00:00 2026
base 0x7e0 500k
 0.000000 51  100  8  AA BB CC DD EE FF 00 11
 0.500000 51  200  4  01 02 03 04
 1.000000 51  300  2  AA BB
");
        try
        {
            var sink = new FakeReplayFrameSink();
            using var service = new ReplayService(sink, NullLogger<ReplayService>.Instance);
            await service.LoadAsync(path);

            service.TotalDuration.Should().Be(1.0);
            service.State.Should().Be(ReplayState.Stopped);
            service.Speed.Should().Be(1.0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// v1.4.0 MINOR: FrameEmitted event fires during playback.
    /// </summary>
    [Fact]
    public async Task FrameEmitted_EventFiresDuringPlayback()
    {
        var path = WriteTempAsc(@"
date Wed Jun 28 10:00:00 2026
base 0x7e0 500k
 0.000000 51  100  8  AA BB CC DD EE FF 00 11
 0.050000 51  200  8  01 02 03 04 05 06 07 08
");
        try
        {
            var sink = new FakeReplayFrameSink();
            using var service = new ReplayService(sink, NullLogger<ReplayService>.Instance);
            var emitted = new List<ReplayFrame>();
            service.FrameEmitted += f => emitted.Add(f);
            await service.LoadAsync(path);

            service.Play();
            await Task.Delay(200);
            service.Stop();

            emitted.Should().HaveCount(2);
            emitted[0].Id.Should().Be(0x100u);
            emitted[1].Id.Should().Be(0x200u);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// v1.4.0 MINOR: SetSpeed updates Speed property.
    /// </summary>
    [Fact]
    public async Task SetSpeed_UpdatesSpeedProperty()
    {
        var path = WriteTempAsc(@"
date Wed Jun 28 10:00:00 2026
base 0x7e0 500k
 0.000000 51  100  2  AA BB
");
        try
        {
            var sink = new FakeReplayFrameSink();
            using var service = new ReplayService(sink, NullLogger<ReplayService>.Instance);
            await service.LoadAsync(path);

            service.Speed.Should().Be(1.0);
            service.SetSpeed(2.0);
            service.Speed.Should().Be(2.0);
            service.SetSpeed(0.5);
            service.Speed.Should().Be(0.5);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// v1.4.0 MINOR review (I-3): LoadAsync wraps FileNotFoundException
    /// in ReplayLoadException.
    /// </summary>
    [Fact]
    public async Task LoadAsync_NonexistentFile_ThrowsReplayLoadException()
    {
        var sink = new FakeReplayFrameSink();
        using var service = new ReplayService(sink, NullLogger<ReplayService>.Instance);

        var act = async () => await service.LoadAsync("/nonexistent/path.asc");

        await act.Should().ThrowAsync<ReplayLoadException>();
    }

    /// <summary>
    /// v1.4.0 MINOR review (I-3): LoadAsync surfaces a file with no
    /// data lines (headers only) as ReplayFormatException from the parser.
    /// </summary>
    [Fact]
    public async Task LoadAsync_MalformedFile_ThrowsReplayFormatException()
    {
        var path = WriteTempAsc(@"
date Wed Jun 28 10:00:00 2026
base 0x7e0 500k
");
        try
        {
            var sink = new FakeReplayFrameSink();
            using var service = new ReplayService(sink, NullLogger<ReplayService>.Instance);

            var act = async () => await service.LoadAsync(path);

            await act.Should().ThrowAsync<ReplayFormatException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// v1.4.0 MINOR review (I-4): SetSpeed re-anchors playback position so
    /// CurrentTimestamp continues from where it was, just at a new speed.
    /// </summary>
    [Fact]
    public async Task SetSpeed_PreservesCurrentTimestamp()
    {
        // 11 frames at 100ms intervals from t=0..1.0s
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("date Wed Jun 28 10:00:00 2026");
        sb.AppendLine("base 0x7e0 500k");
        for (var i = 0; i <= 10; i++)
        {
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, " {0:F6} 51  100  2  AA BB", 0.1 * i));
        }
        var path = WriteTempAsc(sb.ToString());
        try
        {
            var sink = new FakeReplayFrameSink();
            using var service = new ReplayService(sink, NullLogger<ReplayService>.Instance);
            await service.LoadAsync(path);

            // Phase 1: play at 1x for 200ms
            service.Play();
            await Task.Delay(200);
            service.Pause();
            var t1 = service.CurrentTimestamp;

            // Phase 2: bump speed to 2x, play 200ms wall-clock (≈ 0.4s of timeline)
            service.SetSpeed(2.0);
            service.Play();
            await Task.Delay(200);
            service.Pause();
            var t2 = service.CurrentTimestamp;

            // t2 must have advanced past t1
            t2.Should().BeGreaterThan(t1, "playback should continue after speed change");
            // And the wall-clock delta (200ms) at 2x should advance ~0.4s of timeline.
            var delta = t2 - t1;
            delta.Should().BeInRange(0.15, 0.65, "re-anchor should preserve position; 2x over 200ms ≈ 0.4s");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------- v1.5.0 MINOR Task 4: Loop + CanIdFilter + PlaybackEnded ----------

    /// <summary>
    /// v1.5.0 MINOR Task 4: <see cref="IReplayService.Loop"/> getter returns
    /// the value previously stored by the setter. (Honest round-trip; does
    /// NOT assert propagation to the internal <see cref="ReplayTimeline"/> —
    /// see <see cref="SetLoop_PropagatesToInternalTimeline"/> for that.)
    /// </summary>
    [Fact]
    public void SetLoop_GetterReturnsWhatWasSet()
    {
        var sink = new FakeReplayFrameSink();
        using var service = new ReplayService(sink, NullLogger<ReplayService>.Instance);

        service.Loop.Should().BeFalse("Loop defaults to false");
        service.Loop = true;
        service.Loop.Should().BeTrue("Loop setter stores the value");
    }

    /// <summary>
    /// v1.5.0 MINOR Task 4 (review I-1): Setting
    /// <see cref="IReplayService.Loop"/> must propagate to the internal
    /// <see cref="ReplayTimeline"/> so that EOF behavior actually changes.
    /// Verifies propagation by reflection into the real service's private
    /// <c>_timeline</c> field — catches a future refactor that decouples
    /// <c>ReplayService.Loop</c> from <c>ReplayTimeline.Loop</c>.
    /// </summary>
    [Fact]
    public void SetLoop_PropagatesToInternalTimeline()
    {
        var sink = new FakeReplayFrameSink();
        using var service = new ReplayService(sink, NullLogger<ReplayService>.Instance);

        // Grab the real internal timeline the service owns.
        var timelineField = typeof(ReplayService).GetField(
            "_timeline",
            BindingFlags.Instance | BindingFlags.NonPublic);
        timelineField.Should().NotBeNull("ReplayService must own a private _timeline field");
        var timeline = (ReplayTimeline)timelineField!.GetValue(service)!;

        // Defaults: service.Loop == false → timeline.Loop == false.
        service.Loop.Should().BeFalse("Loop defaults to false");
        timeline.Loop.Should().BeFalse("internal timeline inherits the default");

        // Set service.Loop = true; the underlying timeline must reflect it.
        service.Loop = true;
        timeline.Loop.Should().BeTrue(
            "ReplayService.Loop must propagate to the internal ReplayTimeline.Loop " +
            "so that OnTick rewinds to frame 0 on EOF instead of stopping");

        // And toggling back must also propagate.
        service.Loop = false;
        timeline.Loop.Should().BeFalse(
            "setter must keep the two properties in sync in both directions");
    }

    /// <summary>
    /// v1.5.0 MINOR Task 4: Setting <see cref="IReplayService.CanIdFilter"/> stores
    /// the filter and exposes it back. Filter tri-state semantics: null=all pass,
    /// empty=nothing passes, non-empty=only matching IDs.
    /// </summary>
    [Fact]
    public void SetCanIdFilter_UpdatesService()
    {
        var sink = new FakeReplayFrameSink();
        using var service = new ReplayService(sink, NullLogger<ReplayService>.Instance);

        service.CanIdFilter.Should().BeNull("CanIdFilter defaults to null (all pass)");

        var filter = new HashSet<uint> { 0x100u, 0x200u };
        service.CanIdFilter = filter;
        service.CanIdFilter.Should().BeSameAs(filter, "setter stores the same instance");

        // Empty set is distinct from null — must be preserved as non-null empty.
        service.CanIdFilter = new HashSet<uint>();
        service.CanIdFilter.Should().NotBeNull("empty set is preserved as non-null");
        service.CanIdFilter.Should().BeEmpty("empty set semantics: no frames pass");
    }
}
