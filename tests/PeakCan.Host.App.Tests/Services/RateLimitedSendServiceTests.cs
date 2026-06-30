using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.App.Tests.Services;

/// <summary>
/// v1.6.5 PATCH Item 1 — Token-bucket send-rate-limit decorator tests.
/// Verify the decorator's 5 contracts:
///   (1) under-rate-limit → delegate to inner SendService
///   (2) over-rate-limit  → Result.Fail(HardwareBusy), inner NOT called
///   (3) MaxFramesPerSecond &lt;= 0 → unlimited bypass
///   (4) token refill after elapsed time
///   (5) rejection path preserves caller CT (mirror v1.6.2 PATCH test)
///   (6) RejectedFrameCount atomic counter
///   (7) delegated path propagates inner Result success/failure unchanged
/// </summary>
public class RateLimitedSendServiceTests
{
    /// <summary>
    /// Hand-written replacement for NSubstitute — mirrors the
    /// <c>CountingSendService : SendService</c> pattern from
    /// <c>CyclicSendServiceRaceTests.cs:34</c> (CA2012 audit log
    /// prefers subclass over mock for sealed-ish services).
    /// Records every SendAsync call and returns a caller-configurable
    /// <see cref="Result{Unit}"/>.
    /// </summary>
    private sealed class FakeSendService : SendService
    {
        public List<(CanFrame Frame, CancellationToken Ct)> Calls { get; } = new();
        public Result<Unit> NextResult { get; set; } = Result<Unit>.Ok(default);

        public FakeSendService() : base(NullLogger<SendService>.Instance) { }

        public override ValueTask<Result<Unit>> SendAsync(CanFrame frame, CancellationToken ct = default)
        {
            Calls.Add((frame, ct));
            return ValueTask.FromResult(NextResult);
        }
    }

    private static CanFrame BuildFrame(uint id) =>
        new(
            Id: new CanId(id, FrameFormat.Standard),
            Data: new byte[] { 0xDE, 0xAD },
            Flags: FrameFlags.None,
            Channel: ChannelId.None,
            Timestamp: default);

    [Fact]
    public async Task SendAsync_under_rate_limit_delegates_to_inner_SendService()
    {
        // Arrange
        var inner = new FakeSendService();
        var sut = new RateLimitedSendService(inner, 100, NullLogger<RateLimitedSendService>.Instance);
        var frame = BuildFrame(0x123);

        // Act
        var r = await sut.SendAsync(frame);

        // Assert
        r.IsSuccess.Should().BeTrue();
        inner.Calls.Should().ContainSingle()
            .Which.Frame.Id.Raw.Should().Be(0x123u);
    }

    [Fact]
    public async Task SendAsync_over_rate_limit_returns_HardwareBusy_failure()
    {
        // Arrange — 1 fps cap → first send passes (consumes the 1 burst token),
        // second send within the same refill window rejects.
        var inner = new FakeSendService();
        var sut = new RateLimitedSendService(inner, 1, NullLogger<RateLimitedSendService>.Instance);
        var frame = BuildFrame(0x123);

        // Act
        await sut.SendAsync(frame);
        var rejected = await sut.SendAsync(frame);

        // Assert
        rejected.IsSuccess.Should().BeFalse();
        rejected.Error!.Code.Should().Be(ErrorCode.HardwareBusy);
        rejected.Error.Message.Should().Contain("rate limit");
        inner.Calls.Should().HaveCount(1,
            "inner must NOT be called when the decorator rejects");
    }

    [Fact]
    public async Task SendAsync_when_MaxFramesPerSecond_is_zero_does_not_rate_limit()
    {
        // Arrange — opt-out: 0 (or negative) means "unlimited", every send passes.
        var inner = new FakeSendService();
        var sut = new RateLimitedSendService(inner, 0, NullLogger<RateLimitedSendService>.Instance);
        var frame = BuildFrame(0x123);

        // Act
        for (int i = 0; i < 10; i++)
        {
            var r = await sut.SendAsync(frame);
            r.IsSuccess.Should().BeTrue();
        }

        // Assert
        inner.Calls.Should().HaveCount(10);
        sut.RejectedFrameCount.Should().Be(0);
    }

    [Fact]
    public async Task SendAsync_refills_tokens_after_elapsed_time()
    {
        // Arrange — 10 fps cap → burst of 10, then must wait ≥ 100 ms to refill 1 token.
        // 250 ms gives 2.5× safety buffer for CI slow hosts (token bucket uses
        // Stopwatch.GetTimestamp which is monotonic; arithmetic is unaffected by
        // system clock jitter, but timer scheduling can still add a few ms).
        var inner = new FakeSendService();
        var sut = new RateLimitedSendService(inner, 10, NullLogger<RateLimitedSendService>.Instance);
        var frame = BuildFrame(0x123);

        // Act — drain the burst.
        for (int i = 0; i < 10; i++)
        {
            await sut.SendAsync(frame);
        }
        var immediatelyAfterDrain = await sut.SendAsync(frame);

        // Assert — first reject (no tokens left)
        immediatelyAfterDrain.IsSuccess.Should().BeFalse();

        // Wait for refill, then send again.
        await Task.Delay(250);
        var afterRefill = await sut.SendAsync(frame);

        // Assert — accepted after refill window.
        afterRefill.IsSuccess.Should().BeTrue(
            "tokens should refill after 250ms @ 10 tokens/sec");
    }

    [Fact]
    public async Task SendAsync_rejected_path_preserves_caller_cancellation_token()
    {
        // Arrange — mirror v1.6.2 PATCH
        // SendAsync_channel_WriteAsync_OCE_with_unrelated_CT_does_not_swallow:
        // the decorator must NOT cancel, swallow, or replace the caller's CT.
        var inner = new FakeSendService();
        var sut = new RateLimitedSendService(inner, 1, NullLogger<RateLimitedSendService>.Instance);
        var frame = BuildFrame(0x123);
        using var cts = new CancellationTokenSource();

        // Act — drain burst, then reject with caller-provided CT.
        await sut.SendAsync(frame);
        var rejected = await sut.SendAsync(frame, cts.Token);

        // Assert
        rejected.IsSuccess.Should().BeFalse();
        rejected.Error!.Code.Should().Be(ErrorCode.HardwareBusy);
        cts.IsCancellationRequested.Should().BeFalse(
            "decorator must NOT cancel or swallow the caller's token");
        inner.Calls.Should().HaveCount(1,
            "inner must NOT be called when the decorator rejects");
    }

    [Fact]
    public async Task SendAsync_Rejects_Increment_RejectedFrameCount()
    {
        // Arrange
        var inner = new FakeSendService();
        var sut = new RateLimitedSendService(inner, 1, NullLogger<RateLimitedSendService>.Instance);
        var frame = BuildFrame(0x123);

        // Act
        await sut.SendAsync(frame);  // pass
        await sut.SendAsync(frame);  // reject #1
        await sut.SendAsync(frame);  // reject #2

        // Assert
        sut.RejectedFrameCount.Should().Be(2,
            "RejectedFrameCount increments only on rejected sends");
    }

    [Fact]
    public async Task SendAsync_Delegated_Path_Propagates_Result_Success_And_Failure()
    {
        // Arrange — inner SendService can return success or failure.
        // The decorator must forward both unchanged (no Result rewriting).
        var inner = new FakeSendService();
        var sut = new RateLimitedSendService(inner, 100, NullLogger<RateLimitedSendService>.Instance);
        var frame = BuildFrame(0x123);

        // Act + Assert — success path
        inner.NextResult = Result<Unit>.Ok(default);
        var ok = await sut.SendAsync(frame);
        ok.IsSuccess.Should().BeTrue();

        // Act + Assert — failure path
        inner.NextResult = Result<Unit>.Fail(ErrorCode.HardwareNotAvailable, "channel disconnected");
        var fail = await sut.SendAsync(frame);
        fail.IsSuccess.Should().BeFalse();
        fail.Error!.Code.Should().Be(ErrorCode.HardwareNotAvailable);
        fail.Error.Message.Should().Be("channel disconnected",
            "decorator must NOT rewrite the inner result's message");
    }
}
