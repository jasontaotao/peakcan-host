using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Composition;
using PeakCan.Host.App.Services;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.App.Tests.Composition;

/// <summary>
/// v1.4.2 PATCH Item 3: ReplayFrameSinkAdapter must surface the first
/// <c>Result&lt;Unit&gt;.Fail</c> from <c>SendService.SendAsync</c> as
/// a <see cref="ReplaySendException"/> so the user gets feedback when
/// playback runs on a disconnected channel. Previously the result was
/// silently dropped (user-hostile silent drop on no-channel).
/// </summary>
public class ReplayFrameSinkAdapterTests
{
    /// <summary>
    /// On a failed <c>Result&lt;Unit&gt;</c>, the adapter throws
    /// <see cref="ReplaySendException"/> with the failure reason in the
    /// message. RED-then-GREEN: this test FAILS on unfixed code (result
    /// discarded, no throw) and PASSES on fixed code.
    /// </summary>
    [Fact]
    public async Task SendFrameAsync_FailResult_ThrowsReplaySendException()
    {
        var sendService = Substitute.For<SendService>(NullLogger<SendService>.Instance);
        sendService.SendAsync(Arg.Any<CanFrame>(), Arg.Any<CancellationToken>())
            .Returns(Result<Unit>.Fail(ErrorCode.InvalidState, "no active channel"));

        var adapter = new ReplayFrameSinkAdapter(sendService);
        var frame = new ReplayFrame(0.005, 0x100, 2, new byte[] { 0xAA, 0xBB }, FrameFlags.None);

        var act = async () => await adapter.SendFrameAsync(frame);
        await act.Should().ThrowAsync<ReplaySendException>()
            .Where(ex => ex.Message.Contains("no active channel"));
    }

    /// <summary>
    /// On a successful <c>Result&lt;Unit&gt;</c>, the adapter does not
    /// throw and returns a completed <see cref="ValueTask"/>.
    /// </summary>
    [Fact]
    public async Task SendFrameAsync_OkResult_DoesNotThrow()
    {
        var sendService = Substitute.For<SendService>(NullLogger<SendService>.Instance);
        sendService.SendAsync(Arg.Any<CanFrame>(), Arg.Any<CancellationToken>())
            .Returns(Result<Unit>.Ok(new Unit()));

        var adapter = new ReplayFrameSinkAdapter(sendService);
        var frame = new ReplayFrame(0.000, 0x100, 1, new byte[] { 0xCC }, FrameFlags.None);

        var act = async () => await adapter.SendFrameAsync(frame);
        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// The exception message must contain the frame's timestamp and
    /// CAN ID so the user can locate the failing frame in the timeline
    /// (especially for long ASC files where the failure point matters).
    /// </summary>
    [Fact]
    public async Task SendFrameAsync_ExceptionMessage_ContainsFrameTimestampAndId()
    {
        var sendService = Substitute.For<SendService>(NullLogger<SendService>.Instance);
        sendService.SendAsync(Arg.Any<CanFrame>(), Arg.Any<CancellationToken>())
            .Returns(Result<Unit>.Fail(ErrorCode.HardwareNotAvailable, "PCAN error 0x1001"));

        var adapter = new ReplayFrameSinkAdapter(sendService);
        var frame = new ReplayFrame(0.123, 0x7DF, 8,
            new byte[] { 0x02, 0x09, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00 },
            FrameFlags.None);

        var ex = await Assert.ThrowsAsync<ReplaySendException>(
            async () => await adapter.SendFrameAsync(frame));

        ex.Message.Should().Contain("0.123", "frame timestamp for traceability");
        ex.Message.Should().Contain("0x7DF", "frame CAN ID for traceability");
        ex.Message.Should().Contain("PCAN error 0x1001", "underlying send reason");
    }

    /// <summary>
    /// Ctor must guard against null <see cref="SendService"/> (DI misconfig
    /// surfaces as ArgumentNullException, not NullReferenceException at
    /// first send).
    /// </summary>
    [Fact]
    public void Ctor_NullSendService_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ReplayFrameSinkAdapter(null!));
    }
}
