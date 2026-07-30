using PeakCan.Host.Core.HIL.Assertions;
using PeakCan.Host.Core.Tests.HIL.Fakes;

namespace PeakCan.Host.Core.Tests.HIL.Assertions;

public class WaitForFrameAsyncTests
{
    private readonly FakeAssertionContext _ctx = new();
    private readonly AssertionPrimitives _primitives;

    public WaitForFrameAsyncTests() => _primitives = new AssertionPrimitives(_ctx);

    [Fact]
    public async Task WaitForFrameAsync_ExactIdMatch_Passes()
    {
        // Arrange
        var task = _primitives.WaitForFrameAsync(new CanId(0x123, FrameFormat.Standard), null, 1000, default);

        // Act
        _ctx.PushFrame(new CanFrame(new CanId(0x123, FrameFormat.Standard),
            ReadOnlyMemory<byte>.Empty, FrameFlags.None, default, default));

        // Assert
        var result = await task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.Passed);
        Assert.Contains("0x123", result.Message);
    }

    [Fact]
    public async Task WaitForFrameAsync_MaskMatch_Passes()
    {
        // Arrange
        var task = _primitives.WaitForFrameAsync(new CanId(0x123, FrameFormat.Standard), new byte[] { 0xFF }, 1000, default);

        // Act: data[0] = 0xFF, mask[0] = 0xFF -> (0xFF & 0xFF) == 0xFF -> match
        _ctx.PushFrame(new CanFrame(new CanId(0x123, FrameFormat.Standard),
            new byte[] { 0xFF, 0x0F }, FrameFlags.None, default, default));

        // Assert
        var result = await task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task WaitForFrameAsync_MaskMismatch_Fails()
    {
        // Arrange
        var task = _primitives.WaitForFrameAsync(new CanId(0x123, FrameFormat.Standard), new byte[] { 0xFF }, 1000, default);

        // Act: data[0] = 0x0F, mask[0] = 0xFF -> (0x0F & 0xFF) != 0xFF -> no match
        _ctx.PushFrame(new CanFrame(new CanId(0x123, FrameFormat.Standard),
            new byte[] { 0x0F, 0xFF }, FrameFlags.None, default, default));

        // Assert
        var result = await task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(result.Passed);
        Assert.Contains("timeout", result.Message);
    }

    [Fact]
    public async Task WaitForFrameAsync_Timeout_Fails()
    {
        // Arrange - no frame will be fired
        // Act
        var result = await _primitives.WaitForFrameAsync(new CanId(0x123, FrameFormat.Standard), null, 50, default);

        // Assert
        Assert.False(result.Passed);
        Assert.Contains("timeout", result.Message);
    }

    [Fact]
    public async Task WaitForFrameAsync_NullMask_MatchesAnyData()
    {
        // Arrange
        var task = _primitives.WaitForFrameAsync(new CanId(0x123, FrameFormat.Standard), null, 1000, default);

        // Act: empty data with null mask -> match
        _ctx.PushFrame(new CanFrame(new CanId(0x123, FrameFormat.Standard),
            ReadOnlyMemory<byte>.Empty, FrameFlags.None, default, default));

        // Assert
        var result = await task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task WaitForFrameAsync_Cancelled_Fails()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var task = _primitives.WaitForFrameAsync(new CanId(0x123, FrameFormat.Standard), null, 10000, cts.Token);

        // Act
        cts.Cancel();

        // Assert
        var result = await task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(result.Passed);
    }
}
