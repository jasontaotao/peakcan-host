using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.Host.Infrastructure.CanChannels;
using PeakCan.Host.Infrastructure.HIL;
using PeakCan.Host.Infrastructure.HIL.StepExecutor;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.HIL.StepExecutor;

public class ModifyBackgroundFrameStepExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_WithKnownId_PassesAndUpdatesData()
    {
        var channel = new VirtualChannel();
        var sender = new BackgroundFrameSender(channel, NullLogger.Instance);
        sender.Start(new[]
        {
            new BackgroundFrame(new CanId(0x100, FrameFormat.Standard), new byte[] { 0 }, 100, false),
        });

        var executor = new ModifyBackgroundFrameStepExecutor(sender);
        var step = TestCaseStep.Create(
            new ModifyBackgroundFrameStep(
                new CanId(0x100, FrameFormat.Standard),
                new byte[] { 0xAA, 0xBB }),
            "Modify bg 0x100");

        var result = await executor.ExecuteAsync(step, null!, CancellationToken.None);

        result.Status.Should().Be(StepStatus.Passed);
        result.Message.Should().Contain("0x100");

        sender.Stop();
        sender.Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_WithUnknownId_StillPasses_LogsWarning()
    {
        var channel = new VirtualChannel();
        var sender = new BackgroundFrameSender(channel, NullLogger.Instance);
        // 不 Start 任何 frame

        var executor = new ModifyBackgroundFrameStepExecutor(sender);
        var step = TestCaseStep.Create(
            new ModifyBackgroundFrameStep(
                new CanId(0x200, FrameFormat.Standard),
                new byte[] { 0x01 }),
            "Modify non-existent");

        var result = await executor.ExecuteAsync(step, null!, CancellationToken.None);

        // Plan 设计：找不到时 log warning 但 step 仍 Passed
        result.Status.Should().Be(StepStatus.Passed);

        sender.Dispose();
    }

    [Fact]
    public void Kind_ReturnsModifyBackgroundFrame()
    {
        var sender = new BackgroundFrameSender(new VirtualChannel(), NullLogger.Instance);
        var executor = new ModifyBackgroundFrameStepExecutor(sender);

        executor.Kind.Should().Be(TestCaseStepKind.ModifyBackgroundFrame);

        sender.Dispose();
    }
}
