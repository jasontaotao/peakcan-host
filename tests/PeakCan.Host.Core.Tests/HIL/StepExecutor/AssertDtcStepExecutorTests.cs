using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.HIL.StepExecutor;
using PeakCan.HIL.Core.Tests.HIL.Fakes;
using Xunit;

namespace PeakCan.HIL.Core.Tests.HIL.StepExecutor;

public class AssertDtcStepExecutorTests
{
    private static TestCaseStep CreateStep(ushort? dtcCode, bool expectPresent) =>
        TestCaseStep.Create(new AssertDtcStep(dtcCode, expectPresent));

    [Fact]
    public async Task ExecuteAsync_DtcPresent_ExpectPresent_Passes()
    {
        // Arrange
        var session = new FakeIUdsSession(new[] { new DtcInfo(0x1234, 0x01) }); // status bit 0 = active
        var executor = new AssertDtcStepExecutor(session);
        var step = CreateStep(0x1234, expectPresent: true);

        // Act
        var result = await executor.ExecuteAsync(step, null!, default);

        // Assert
        Assert.Equal(StepStatus.Passed, result.Status);
    }

    [Fact]
    public async Task ExecuteAsync_DtcAbsent_ExpectPresent_Fails()
    {
        // Arrange
        var session = new FakeIUdsSession(Array.Empty<DtcInfo>());
        var executor = new AssertDtcStepExecutor(session);
        var step = CreateStep(0x1234, expectPresent: true);

        // Act
        var result = await executor.ExecuteAsync(step, null!, default);

        // Assert
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("not found", result.Message);
    }

    [Fact]
    public async Task ExecuteAsync_DtcAbsent_ExpectAbsent_Passes()
    {
        // Arrange
        var session = new FakeIUdsSession(Array.Empty<DtcInfo>());
        var executor = new AssertDtcStepExecutor(session);
        var step = CreateStep(0x1234, expectPresent: false);

        // Act
        var result = await executor.ExecuteAsync(step, null!, default);

        // Assert
        Assert.Equal(StepStatus.Passed, result.Status);
    }

    [Fact]
    public async Task ExecuteAsync_DtcPresent_ExpectAbsent_Fails()
    {
        // Arrange
        var session = new FakeIUdsSession(new[] { new DtcInfo(0x1234, 0x04) }); // status bit 2 = confirmed
        var executor = new AssertDtcStepExecutor(session);
        var step = CreateStep(0x1234, expectPresent: false);

        // Act
        var result = await executor.ExecuteAsync(step, null!, default);

        // Assert
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("unexpectedly present", result.Message);
    }

    [Fact]
    public async Task ExecuteAsync_NullDtcCode_AnyDtc_Passes()
    {
        // Arrange
        var session = new FakeIUdsSession(new[] { new DtcInfo(0x5678, 0x01) });
        var executor = new AssertDtcStepExecutor(session);
        var step = CreateStep(null, expectPresent: true);

        // Act
        var result = await executor.ExecuteAsync(step, null!, default);

        // Assert
        Assert.Equal(StepStatus.Passed, result.Status);
    }

    [Fact]
    public async Task ExecuteAsync_UdsError_Fails()
    {
        // Arrange
        var session = new FakeIUdsSession(readException: new UdsSessionTransportException("timeout"));
        var executor = new AssertDtcStepExecutor(session);
        var step = CreateStep(0x1234, expectPresent: true);

        // Act
        var result = await executor.ExecuteAsync(step, null!, default);

        // Assert
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("UDS error", result.Message);
    }
}
