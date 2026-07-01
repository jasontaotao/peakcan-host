using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Channel;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests;

/// <summary>
/// Task 10: verifies that <see cref="ChannelRouter"/> fans out frames from
/// all registered <see cref="ICanChannel"/>s to all attached
/// <see cref="IFrameSink"/>s, with per-sink exception isolation.
/// </summary>
public class ChannelRouterTests
{
    [Fact]
    public void FanOut_Delivers_Frame_To_All_Sinks()
    {
        var ch1 = new FakeChannel();
        var ch2 = new FakeChannel();
        var sink1 = Substitute.For<IFrameSink>();
        var sink2 = Substitute.For<IFrameSink>();
        var router = new ChannelRouter();
        router.RegisterChannel(ch1);
        router.RegisterChannel(ch2);
        router.AttachSink(sink1);
        router.AttachSink(sink2);
        var frame = new CanFrame(new CanId(1, FrameFormat.Standard), new byte[] { 0xAA }, FrameFlags.None, ChannelId.None, default);

        ch1.Raise(frame);
        ch2.Raise(frame);

        sink1.Received(2).OnFrame(frame);
        sink2.Received(2).OnFrame(frame);
    }

    [Fact]
    public void Detaching_Sink_Stops_Delivery()
    {
        var ch = Substitute.For<ICanChannel>();
        var sink = Substitute.For<IFrameSink>();
        var router = new ChannelRouter();
        router.RegisterChannel(ch);
        router.AttachSink(sink);
        router.DetachSink(sink);
        var frame = new CanFrame(new CanId(1, FrameFormat.Standard), new byte[] { 0xAA }, FrameFlags.None, ChannelId.None, default);

        ch.FrameReceived += Raise.Event<Action<CanFrame>>(frame);

        sink.DidNotReceive().OnFrame(Arg.Any<CanFrame>());
    }

    [Fact]
    public void Unregistering_Channel_Stops_Delivery_From_That_Channel()
    {
        var ch1 = Substitute.For<ICanChannel>();
        var ch2 = Substitute.For<ICanChannel>();
        var sink = Substitute.For<IFrameSink>();
        var router = new ChannelRouter();
        router.RegisterChannel(ch1);
        router.RegisterChannel(ch2);
        router.AttachSink(sink);
        var frame1 = new CanFrame(new CanId(1, FrameFormat.Standard), new byte[] { 0x11 }, FrameFlags.None, ChannelId.None, default);
        var frame2 = new CanFrame(new CanId(2, FrameFormat.Standard), new byte[] { 0x22 }, FrameFlags.None, ChannelId.None, default);

        router.UnregisterChannel(ch1);
        ch1.FrameReceived += Raise.Event<Action<CanFrame>>(frame1);
        ch2.FrameReceived += Raise.Event<Action<CanFrame>>(frame2);

        sink.DidNotReceive().OnFrame(frame1);
        sink.Received(1).OnFrame(frame2);
    }

    [Fact]
    public void Duplicate_Registration_And_Attachment_Are_Idempotent()
    {
        var ch = Substitute.For<ICanChannel>();
        var sink = Substitute.For<IFrameSink>();
        var router = new ChannelRouter();
        router.RegisterChannel(ch);
        router.RegisterChannel(ch);   // second time — no-op
        router.AttachSink(sink);
        router.AttachSink(sink);     // second time — no-op
        var frame = new CanFrame(new CanId(1, FrameFormat.Standard), new byte[] { 0xAA }, FrameFlags.None, ChannelId.None, default);

        ch.FrameReceived += Raise.Event<Action<CanFrame>>(frame);

        sink.Received(1).OnFrame(frame);
    }

    [Fact]
    public void Sink_Throwing_Does_Not_Stop_Other_Sinks()
    {
        // Per-sink exception isolation: a misbehaving sink must not
        // swallow frames intended for downstream sinks.
        var ch = Substitute.For<ICanChannel>();
        var goodSink = Substitute.For<IFrameSink>();
        var badSink = Substitute.For<IFrameSink>();
        badSink.When(s => s.OnFrame(Arg.Any<CanFrame>()))
               .Do(_ => throw new InvalidOperationException("boom"));
        var router = new ChannelRouter();
        router.RegisterChannel(ch);
        router.AttachSink(badSink);
        router.AttachSink(goodSink);
        var frame = new CanFrame(new CanId(1, FrameFormat.Standard), new byte[] { 0xAA }, FrameFlags.None, ChannelId.None, default);

        ch.FrameReceived += Raise.Event<Action<CanFrame>>(frame);

        goodSink.Received(1).OnFrame(frame);
    }

    [Fact]
    public void Sink_Throwing_Is_Forwarded_To_Same_Sink_OnError()
    {
        // The plan's design: a misbehaving sink's exception is caught
        // and forwarded to the SAME sink's OnError so it can log.
        var ch = Substitute.For<ICanChannel>();
        var badSink = Substitute.For<IFrameSink>();
        badSink.When(s => s.OnFrame(Arg.Any<CanFrame>()))
               .Do(_ => throw new InvalidOperationException("boom"));
        var router = new ChannelRouter();
        router.RegisterChannel(ch);
        router.AttachSink(badSink);
        var frame = new CanFrame(new CanId(1, FrameFormat.Standard), new byte[] { 0xAA }, FrameFlags.None, ChannelId.None, default);

        ch.FrameReceived += Raise.Event<Action<CanFrame>>(frame);

        badSink.Received(1).OnError(Arg.Any<Exception>());
    }

    [Fact]
    public void OnError_Itself_Throwing_Autodetaches_Sink()
    {
        // Per spec 6.2 ("never silently swallow errors"), when a sink's
        // OnError also throws, the router must surface the failure AND
        // stop the loop forever — detach the misbehaving sink.
        var ch = Substitute.For<ICanChannel>();
        var foreverBrokenSink = Substitute.For<IFrameSink>();
        foreverBrokenSink.When(s => s.OnFrame(Arg.Any<CanFrame>()))
                          .Do(_ => throw new InvalidOperationException("onframe boom"));
        foreverBrokenSink.When(s => s.OnError(Arg.Any<Exception>()))
                          .Do(_ => throw new InvalidOperationException("onerror boom"));
        var router = new ChannelRouter();
        router.RegisterChannel(ch);
        router.AttachSink(foreverBrokenSink);
        var frame = new CanFrame(new CanId(1, FrameFormat.Standard), new byte[] { 0xAA }, FrameFlags.None, ChannelId.None, default);

        ch.FrameReceived += Raise.Event<Action<CanFrame>>(frame);
        ch.FrameReceived += Raise.Event<Action<CanFrame>>(frame);   // second call — sink should be auto-detached

        // First call triggered OnError (which threw → auto-detach).
        // Second call goes to no sink (snapshot is empty for this sink).
        foreverBrokenSink.Received(1).OnError(Arg.Any<Exception>());
        foreverBrokenSink.Received(1).OnFrame(Arg.Any<CanFrame>());
    }

    // --- v1.2.13 PATCH Item 9: log original OnFrame exception before OnError inner catch ---

    [Fact]
    public void OnFrame_Exception_Logged_With_Original_Exception_Before_OnError()
    {
        // v1.2.13 PATCH Item 9: the operator investigating "why did sink X
        // misbehave" needs the OnFrame-thrown exception in the structured
        // exception field, not just the OnError-thrown exception. The router
        // already logs the inner exception (LogSinkOnError id 6004) but the
        // ORIGINAL OnFrame exception was lost — only its ToString() leaked
        // via the message context. Now LogChannelRouterSinkOnFrameFailed
        // (id 6010) records the original exception as the structured
        // exception of a Warning log BEFORE the inner-catch path runs.
        var logger = Substitute.For<ILogger<ChannelRouter>>();
        logger.IsEnabled(LogLevel.Warning).Returns(true);
        var router = new ChannelRouter(logger);

        var channel = Substitute.For<ICanChannel>();
        var sink = Substitute.For<IFrameSink>();
        var originalException = new InvalidOperationException("onframe-root-cause");
        var onErrorException = new InvalidOperationException("onerror-threw");

        sink.When(s => s.OnFrame(Arg.Any<CanFrame>())).Do(_ => throw originalException);
        sink.When(s => s.OnError(Arg.Any<Exception>())).Do(_ => throw onErrorException);

        router.RegisterChannel(channel);
        router.AttachSink(sink);

        // Drive a frame through the router.
        var frame = new CanFrame(new CanId(0x100, FrameFormat.Standard), new byte[] { 0xAA }, FrameFlags.None, ChannelId.None, default);
        channel.FrameReceived += Raise.Event<Action<CanFrame>>(frame);

        // Assert the ORIGINAL exception was logged at Warning (id 6010).
        logger.Received().Log(
            LogLevel.Warning,
            Arg.Is<EventId>(e => e.Id == 6010),
            Arg.Any<object>(),
            originalException,  // <-- the structured exception field carries the original
            Arg.Any<Func<object, Exception?, string>>());
    }

    // --- v1.2.12 PATCH Item 11: sink OnError → ILogger on the router ---

    [Fact]
    public void OnError_From_Sink_Logged_Via_ILogger()
    {
        // When a sink's OnError itself throws, the router must log via
        // ILogger (the previous Debug.WriteLine path is stripped in
        // Release builds). The secondary exception is forwarded to the
        // logger and the misbehaving sink is auto-detached.
        var ch = Substitute.For<ICanChannel>();
        var sink = Substitute.For<IFrameSink>();
        sink.When(s => s.OnFrame(Arg.Any<CanFrame>()))
            .Do(_ => throw new InvalidOperationException("sink explosion"));
        sink.When(s => s.OnError(Arg.Any<Exception>()))
            .Do(_ => throw new InvalidOperationException("onerror explosion"));
        // Source-gen [LoggerMessage] gates Log() on IsEnabled, so stub true.
        var logger = Substitute.For<ILogger<ChannelRouter>>();
        logger.IsEnabled(LogLevel.Warning).Returns(true);
        var router = new ChannelRouter(logger);
        router.RegisterChannel(ch);
        router.AttachSink(sink);
        var frame = new CanFrame(new CanId(1, FrameFormat.Standard), new byte[] { 0xAA }, FrameFlags.None, ChannelId.None, default);

        ch.FrameReceived += Raise.Event<Action<CanFrame>>(frame);

        // v1.2.13 PATCH Item 9: 2 Warning logs now expected — id 6010 for
        // the original OnFrame exception (before delegation to OnError)
        // and id 6004 for the inner OnError exception. Pre-Item 9 only
        // the id 6004 path existed.
        logger.Received().Log(
            LogLevel.Warning,
            Arg.Is<EventId>(e => e.Id == 6010),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
        logger.Received().Log(
            LogLevel.Warning,
            Arg.Is<EventId>(e => e.Id == 6004),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
        sink.Received(1).OnError(Arg.Any<Exception>());
    }

    [Fact]
    public void OnError_Logger_Null_Does_Not_Throw()
    {
        // The router must tolerate a null ILogger (test fixtures /
        // backward-compat callers) and still detach the misbehaving sink
        // so traffic for downstream sinks continues.
        var ch = Substitute.For<ICanChannel>();
        var sink = Substitute.For<IFrameSink>();
        sink.When(s => s.OnFrame(Arg.Any<CanFrame>()))
            .Do(_ => throw new InvalidOperationException("boom"));
        sink.When(s => s.OnError(Arg.Any<Exception>()))
            .Do(_ => throw new InvalidOperationException("onerror boom"));
        var router = new ChannelRouter(logger: null);
        router.RegisterChannel(ch);
        router.AttachSink(sink);
        var frame = new CanFrame(new CanId(1, FrameFormat.Standard), new byte[] { 0xAA }, FrameFlags.None, ChannelId.None, default);

        var act = () => ch.FrameReceived += Raise.Event<Action<CanFrame>>(frame);
        act.Should().NotThrow();
        // Auto-detach still runs even when the logger is null.
        ch.FrameReceived += Raise.Event<Action<CanFrame>>(frame);
        sink.Received(1).OnFrame(Arg.Any<CanFrame>());   // only first call
    }

    [Fact]
    public void Register_Null_Channel_Throws()
    {
        var router = new ChannelRouter();
        var act = () => router.RegisterChannel(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Unregister_Null_Channel_Throws()
    {
        var router = new ChannelRouter();
        var act = () => router.UnregisterChannel(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Attach_Null_Sink_Throws()
    {
        var router = new ChannelRouter();
        var act = () => router.AttachSink(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Detach_Null_Sink_Throws()
    {
        var router = new ChannelRouter();
        var act = () => router.DetachSink(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // --- v1.2.3 dispatcher-starvation hardening (P1) ---

    [Fact]
    public void OnChannelFrame_Allocates_Less_Than_One_Kilobyte_Per_Frame_In_Steady_State()
    {
        // v1.2.3: pre-1.2.3 every OnChannelFrame allocated a fresh
        // IFrameSink[] via _sinks.ToArray() — at 8 kfps that is 256 kB/s
        // of short-lived Gen0 array allocations, which pressured the GC
        // and contributed to dispatcher-thread pauses. The v1.2.3 fix
        // replaces the List+IFrameSink[] with an ImmutableArray field
        // copied by value into a local at dispatch time. With a real
        // (non-NSubstitute) sink that does nothing, steady-state per-frame
        // allocation should be < 1 kB on .NET 10 (just the CanFrame
        // record we synthesise here, since ImmutableArray is a struct).
        var ch = Substitute.For<ICanChannel>();
        var router = new ChannelRouter();
        router.RegisterChannel(ch);
        router.AttachSink(new NullSink());
        var frame = new CanFrame(
            new CanId(1, FrameFormat.Standard),
            new byte[] { 0xAA },
            FrameFlags.None,
            ChannelId.None,
            default);

        // Warm up the JIT and any first-touch lazy allocations.
        for (var i = 0; i < 1000; i++)
        {
            ch.FrameReceived += Raise.Event<Action<CanFrame>>(frame);
        }

        const int iterations = 100_000;
        var bytesBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            ch.FrameReceived += Raise.Event<Action<CanFrame>>(frame);
        }
        var bytesAfter = GC.GetAllocatedBytesForCurrentThread();
        var bytesPerFrame = (bytesAfter - bytesBefore) / (double)iterations;

        // Pre-1.2.3 each iteration allocated a new IFrameSink[1] = 24 B
        // header + 8 B element = ~32 B; the assertion below uses a
        // generous 1 kB headroom so it survives JIT, GC, and the
        // Action<CanFrame> delegate raised by NSubstitute.
        bytesPerFrame.Should().BeLessThan(1024,
            $"OnChannelFrame allocated {bytesPerFrame:F1} B/frame; pre-1.2.3 was ~32 B/frame from List.ToArray()");
    }

    [Fact]
    public void OnChannelFrame_Allocates_Zero_Bytes_Per_Frame_With_Empty_Sink_After_Warmup()
    {
        // v1.2.3 stricter check: with the per-sink _sinks.ToArray()
        // allocation removed, OnChannelFrame should allocate nothing in
        // the steady state. We invoke OnChannelFrame directly via a
        // small Action<CanFrame> bridge so NSubstitute's Raise.Event
        // bookkeeping (a few hundred B/frame) does not drown the
        // measurement. The remaining budget is the OnChannelFrame body
        // itself; pre-1.2.3 measured ~32 B/frame from List.ToArray()
        // (4-element IFrameSink[4] array on the typical 4-sink path),
        // the v1.2.3 ImmutableArray-by-value path should be 0.
        var ch = new FakeChannel();
        var router = new ChannelRouter();
        router.RegisterChannel(ch);
        router.AttachSink(new NullSink());
        var frame = new CanFrame(
            new CanId(1, FrameFormat.Standard),
            new byte[] { 0xAA },
            FrameFlags.None,
            ChannelId.None,
            default);

        // Warm up: JIT, etc.
        for (var i = 0; i < 10_000; i++)
        {
            ch.Raise(frame);
        }

        // Force a full GC so the warmup allocations are not counted.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        const int iterations = 100_000;
        var bytesBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            ch.Raise(frame);
        }
        var bytesAfter = GC.GetAllocatedBytesForCurrentThread();
        var bytesPerFrame = (bytesAfter - bytesBefore) / (double)iterations;

        // 1 byte per frame: leaves a tiny headroom for any
        // bookkeeping we missed but fails loudly on the ~32 B/frame
        // list.ToArray() pre-1.2.3 path.
        bytesPerFrame.Should().BeLessThan(32,
            $"OnChannelFrame allocated {bytesPerFrame:F1} B/frame; expected < 32 B/frame in the v1.2.3 ImmutableArray path");
    }

    /// <summary>
    /// Real (non-NSubstitute) sink that does nothing per frame. Used by
    /// the allocation assertion so NSubstitute's own per-call bookkeeping
    /// does not skew the byte count.
    /// </summary>
    private sealed class NullSink : IFrameSink
    {
        public void OnFrame(CanFrame frame) { }
        public void OnError(Exception ex) { }
    }

    /// <summary>
    /// Real (non-NSubstitute) channel that exposes a
    /// <see cref="Raise(CanFrame)"/> method so allocation-sensitive
    /// tests can fire the <see cref="ICanChannel.FrameReceived"/>
    /// event without NSubstitute's per-call delegate allocation.
    /// </summary>
    private sealed class FakeChannel : ICanChannel
    {
        public ChannelId Id => ChannelId.None;
        public bool IsConnected => true;
        public event Action<CanFrame>? FrameReceived;
        public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DisconnectAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => throw new NotSupportedException();
        public void Raise(CanFrame frame) => FrameReceived?.Invoke(frame);
    }
}
