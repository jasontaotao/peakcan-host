using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Composition;
using PeakCan.Host.App.Services;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Replay;
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
    /// v3.18.5 PATCH (BLF offline playback): a <c>Result.Fail(InvalidState)</c>
    /// ("No active channel" / "Not connected") means the user is replaying
    /// OFFLINE to view the timeline without a hardware channel connected — a
    /// LEGAL use case (user confirmed "both connected and offline must work").
    /// Pre-v3.18.5 the adapter threw ReplaySendException on this, which the
    /// timeline's first-failure handler turned into "Replay aborted" on the
    /// FIRST frame — making offline playback impossible. Now InvalidState is
    /// a silent skip (the frame just isn't sent; timeline keeps advancing).
    /// Real hardware errors (HardwareNotAvailable / IoError) still throw —
    /// those are genuine failures the user must see.
    /// </summary>
    [Fact]
    public async Task SendFrameAsync_InvalidStateNoChannel_DoesNotThrow_SilentSkip()
    {
        var sendService = Substitute.For<SendService>(NullLogger<SendService>.Instance);
        sendService.SendAsync(Arg.Any<CanFrame>(), Arg.Any<CancellationToken>())
            .Returns(Result<Unit>.Fail(ErrorCode.InvalidState, "No active channel"));

        var adapter = new ReplayFrameSinkAdapter(sendService);
        var frame = new ReplayFrame(0.005, 0x100, 2, new byte[] { 0xAA, 0xBB }, FrameFlags.None);

        var act = async () => await adapter.SendFrameAsync(frame);
        await act.Should().NotThrowAsync(
            "InvalidState (no active channel) is an offline-replay scenario, not a failure — must not abort playback");
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

    // === v3.18.4 PATCH (BLF extended-ID send crash + CPU sink) ===
    // Root cause (取证 from peak-20260826.log): real Vector BLF frames carry
    // 29-bit extended CAN IDs with bit31 set (e.g. 0x98ffc23a). The adapter
    // hardcoded FrameFormat.Standard, so CanId..ctor(0x98ffc23a, Standard)
    // threw ArgumentOutOfRangeException on EVERY frame. 742 throws/s →
    // threadpool starvation + 22MB of stack-trace logs + slider 2-3s jumps.
    // Verified against python-can's BLFReader: arbitration_id = can_id &
    // 0x1FFFFFFF, is_extended_id = (can_id & 0x80000000) != 0.
    // Fix: mask the 29-bit ID + pick FrameFormat by the extended bit.

    /// <summary>
    /// A 29-bit extended ID with bit31 set (real BLF extended frame) must NOT
    /// throw ArgumentOutOfRangeException when the adapter builds the CanId.
    /// Pre-fix this threw on every extended frame. RED: fails on unfixed code.
    /// <para>
    /// 重构后：parser 已掩码 bit31 并填 IsExtended，consumer 直接读标记，
    /// 输入是裸 29 位值 + IsExtended:true（模拟 parser 修复后的输出）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task SendFrameAsync_ExtendedId_DoesNotThrow()
    {
        // 0x18ffc23a: 裸 29 位扩展 ID（parser 已掩码 bit31），IsExtended=true。
        var sendService = Substitute.For<SendService>(NullLogger<SendService>.Instance);
        sendService.SendAsync(Arg.Any<CanFrame>(), Arg.Any<CancellationToken>())
            .Returns(Result<Unit>.Ok(new Unit()));

        var adapter = new ReplayFrameSinkAdapter(sendService);
        var frame = new ReplayFrame(0.5, 0x18ffc23a, 8,
            new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 },
            FrameFlags.None, IsExtended: true);

        var act = async () => await adapter.SendFrameAsync(frame);
        await act.Should().NotThrowAsync("extended BLF frames must not crash the send path");
        await sendService.Received(1).SendAsync(Arg.Any<CanFrame>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The adapter must forward the bare 29-bit ID and pick
    /// FrameFormat.Extended for an extended frame. After the parser-layer
    /// refactor, the input is already masked (IsExtended=true, Id=bare value);
    /// the adapter reads IsExtended and constructs CanId accordingly.
    /// Captures the CanFrame handed to SendService via Arg.Do and asserts on it.
    /// </summary>
    [Fact]
    public async Task SendFrameAsync_ExtendedId_Forwards29BitIdAndExtendedFormat()
    {
        CanFrame? captured = null;
        var sendService = Substitute.For<SendService>(NullLogger<SendService>.Instance);
        sendService.SendAsync(Arg.Do<CanFrame>(f => captured = f), Arg.Any<CancellationToken>())
            .Returns(Result<Unit>.Ok(new Unit()));

        var adapter = new ReplayFrameSinkAdapter(sendService);
        var frame = new ReplayFrame(0.5, 0x18ffc23a, 8,
            new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 },
            FrameFlags.None, IsExtended: true);

        await adapter.SendFrameAsync(frame);

        captured.Should().NotBeNull();
        // parser 已掩码 bit31，consumer 透传裸 29 位值
        captured!.Value.Id.Raw.Should().Be(0x18ffc23au,
            "parser 掩码 bit31 后 consumer 透传裸 29 位 ID");
        captured.Value.Id.Format.Should().Be(FrameFormat.Extended,
            "IsExtended=true → 29-bit extended format");
    }

    /// <summary>
    /// A standard 11-bit ID (no bit31) must stay Standard format and the ID
    /// unchanged — regression guard so the extended fix doesn't break the
    /// existing standard-frame path.
    /// </summary>
    [Fact]
    public async Task SendFrameAsync_Standard11BitId_StaysStandardFormat()
    {
        CanFrame? captured = null;
        var sendService = Substitute.For<SendService>(NullLogger<SendService>.Instance);
        sendService.SendAsync(Arg.Do<CanFrame>(f => captured = f), Arg.Any<CancellationToken>())
            .Returns(Result<Unit>.Ok(new Unit()));

        var adapter = new ReplayFrameSinkAdapter(sendService);
        var frame = new ReplayFrame(0.5, 0x100, 8,
            new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 },
            FrameFlags.None);

        await adapter.SendFrameAsync(frame);

        captured.Should().NotBeNull();
        captured!.Value.Id.Raw.Should().Be(0x100u, "standard 11-bit ID passes through unchanged");
        captured.Value.Id.Format.Should().Be(FrameFormat.Standard, "no bit31 → standard 11-bit format");
    }
}
