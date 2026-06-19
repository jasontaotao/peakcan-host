using FluentAssertions;
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
        var ch1 = Substitute.For<ICanChannel>();
        var ch2 = Substitute.For<ICanChannel>();
        var sink1 = Substitute.For<IFrameSink>();
        var sink2 = Substitute.For<IFrameSink>();
        var router = new ChannelRouter();
        router.RegisterChannel(ch1);
        router.RegisterChannel(ch2);
        router.AttachSink(sink1);
        router.AttachSink(sink2);
        var frame = new CanFrame(new CanId(1, FrameFormat.Standard), new byte[] { 0xAA }, FrameFlags.None, ChannelId.None, default);

        ch1.FrameReceived += Raise.Event<Action<CanFrame>>(frame);
        ch2.FrameReceived += Raise.Event<Action<CanFrame>>(frame);

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
}
