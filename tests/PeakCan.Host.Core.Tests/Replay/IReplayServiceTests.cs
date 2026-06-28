using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.Core.Replay;
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
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.asc");
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
}