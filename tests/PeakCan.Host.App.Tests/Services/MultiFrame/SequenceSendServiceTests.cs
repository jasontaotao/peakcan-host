using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.MultiFrame;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Channel;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.MultiFrame;

/// <summary>
/// v2.1.0 MINOR tests for <see cref="SequenceSendService"/> —
/// concurrent vs sequential dispatch, iteration count, delay,
/// cancellation, and progress reporting.
/// </summary>
public sealed class SequenceSendServiceTests
{
    /// <summary>Minimal ICanChannel that records every frame written.</summary>
    private sealed class RecordingChannel : ICanChannel
    {
        public ChannelId Id { get; }
        public bool IsConnected { get; private set; }
        public List<CanFrame> Written { get; } = new();
        public bool FailAll { get; set; }
        public int WriteDelayMs { get; set; }
        public RecordingChannel(ChannelId id) { Id = id; IsConnected = true; }
#pragma warning disable CS0067
        public event Action<CanFrame>? FrameReceived;
#pragma warning restore CS0067
        public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
        { IsConnected = true; return Task.FromResult(Result<Unit>.Ok(default)); }
        public Task DisconnectAsync(CancellationToken ct = default)
        { IsConnected = false; return Task.CompletedTask; }
        public async ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
        {
            if (WriteDelayMs > 0) await Task.Delay(WriteDelayMs, ct).ConfigureAwait(false);
            else ct.ThrowIfCancellationRequested();
            Written.Add(frame);
            return FailAll
                ? Result<Unit>.Fail(ErrorCode.IoError, "fake channel failure")
                : Result<Unit>.Ok(default);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static SendService NewService(RecordingChannel ch)
    {
        // SendService uses ActiveChannel property assignment instead of a
        // ctor-injected channel (matches production wiring where
        // AppShellViewModel sets ActiveChannel on connect).
        var svc = new SendService(NullLogger<SendService>.Instance);
        svc.ActiveChannel = ch;
        return svc;
    }

    private static CanFrame MakeFrame(ushort id) =>
        new(new CanId(id, FrameFormat.Standard), new byte[] { 0xDE, 0xAD },
            FrameFlags.None, ChannelId.None, default);

    [Fact]
    public async Task SendAsync_Concurrent_FiresAllFrames()
    {
        var ch = new RecordingChannel(ChannelId.None);
        var svc = new SequenceSendService(NewService(ch));
        var frames = new[] { MakeFrame(0x100), MakeFrame(0x200), MakeFrame(0x300) };

        var r = await svc.SendAsync(frames, SequenceSendService.Mode.Concurrent,
            delayMs: 0, iterations: 1);

        r.SentCount.Should().Be(3);
        r.FailureCount.Should().Be(0);
        r.IterationsCompleted.Should().Be(1);
        ch.Written.Should().HaveCount(3);
    }

    [Fact]
    public async Task SendAsync_Sequential_FiresInOrder()
    {
        var ch = new RecordingChannel(ChannelId.None);
        var svc = new SequenceSendService(NewService(ch));
        var frames = new[] { MakeFrame(0x100), MakeFrame(0x200), MakeFrame(0x300) };

        var r = await svc.SendAsync(frames, SequenceSendService.Mode.Sequential,
            delayMs: 0, iterations: 1);

        r.SentCount.Should().Be(3);
        ch.Written[0].Id.Raw.Should().Be(0x100u);
        ch.Written[1].Id.Raw.Should().Be(0x200u);
        ch.Written[2].Id.Raw.Should().Be(0x300u);
    }

    [Fact]
    public async Task SendAsync_Iterations_RepeatsSequenceNTimes()
    {
        var ch = new RecordingChannel(ChannelId.None);
        var svc = new SequenceSendService(NewService(ch));
        var frames = new[] { MakeFrame(0x100), MakeFrame(0x200) };

        var r = await svc.SendAsync(frames, SequenceSendService.Mode.Concurrent,
            delayMs: 0, iterations: 5);

        r.SentCount.Should().Be(10); // 2 frames × 5 iterations
        r.IterationsCompleted.Should().Be(5);
        ch.Written.Should().HaveCount(10);
    }

    [Fact]
    public async Task SendAsync_Sequential_DelayRespectedBetweenFrames()
    {
        var ch = new RecordingChannel(ChannelId.None);
        var svc = new SequenceSendService(NewService(ch));
        var frames = new[] { MakeFrame(0x100), MakeFrame(0x200), MakeFrame(0x300) };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await svc.SendAsync(frames, SequenceSendService.Mode.Sequential,
            delayMs: 50, iterations: 1);
        sw.Stop();

        // 3 frames: 2 inter-frame gaps (delay only between consecutive frames)
        // Allow generous upper bound for CI noise.
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(100);
        sw.ElapsedMilliseconds.Should().BeLessThan(2000);
    }

    [Fact]
    public async Task SendAsync_ChannelFails_AllCountedAsFailures()
    {
        var ch = new RecordingChannel(ChannelId.None) { FailAll = true };
        var svc = new SequenceSendService(NewService(ch));
        var frames = new[] { MakeFrame(0x100), MakeFrame(0x200) };

        var r = await svc.SendAsync(frames, SequenceSendService.Mode.Concurrent,
            delayMs: 0, iterations: 1);

        r.SentCount.Should().Be(0);
        r.FailureCount.Should().Be(2);
        r.AllSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_EmptyFrames_ReturnsZeroResult()
    {
        var ch = new RecordingChannel(ChannelId.None);
        var svc = new SequenceSendService(NewService(ch));

        var r = await svc.SendAsync(Array.Empty<CanFrame>(),
            SequenceSendService.Mode.Concurrent, delayMs: 0, iterations: 1);

        r.SentCount.Should().Be(0);
        r.FailureCount.Should().Be(0);
        r.IterationsCompleted.Should().Be(0);
    }

    [Fact]
    public async Task SendAsync_ProgressReporter_ReceivesIncrementalUpdates()
    {
        var ch = new RecordingChannel(ChannelId.None);
        var svc = new SequenceSendService(NewService(ch));
        var frames = new[] { MakeFrame(0x100), MakeFrame(0x200), MakeFrame(0x300) };
        var reported = new List<int>();

        await svc.SendAsync(frames, SequenceSendService.Mode.Sequential,
            delayMs: 0, iterations: 1,
            progress: new Progress<int>(v => reported.Add(v)));

        // Progress is dispatched async; spin briefly to allow all callbacks.
        await Task.Delay(50);
        reported.Should().NotBeEmpty();
        reported.Last().Should().Be(3);
    }

    [Fact]
    public async Task SendAsync_Cancellation_PropagatesBetweenIterations()
    {
        // Channel with per-write delay so iterations don't outrun the
        // cancel timer. Without the delay, 100 iterations of a single
        // frame complete in microseconds and cancel never fires.
        var ch = new RecordingChannel(ChannelId.None) { WriteDelayMs = 30 };
        var svc = new SequenceSendService(NewService(ch));
        var frames = new[] { MakeFrame(0x100) };
        using var cts = new CancellationTokenSource();

        // Cancel after ~50ms; each iteration takes ~30ms so this fires
        // between iteration 1 and 2 (cancel reaches the loop's
        // ThrowIfCancellationRequested guard between iterations).
        cts.CancelAfter(50);
        var act = async () => await svc.SendAsync(frames,
            SequenceSendService.Mode.Concurrent, delayMs: 0, iterations: 100, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SendAsync_RejectsInvalidArgs()
    {
        var ch = new RecordingChannel(ChannelId.None);
        var svc = new SequenceSendService(NewService(ch));
        var frames = new[] { MakeFrame(0x100) };

        // iterations < 1
        var act1 = async () => await svc.SendAsync(frames,
            SequenceSendService.Mode.Concurrent, 0, 0);
        await act1.Should().ThrowAsync<ArgumentOutOfRangeException>();

        // delayMs < 0
        var act2 = async () => await svc.SendAsync(frames,
            SequenceSendService.Mode.Sequential, -1, 1);
        await act2.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}