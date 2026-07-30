using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Core.HIL.StepExecutor;
using PeakCan.Host.Core.Tests.HIL.Fakes;
using Xunit;

namespace PeakCan.Host.Core.Tests.HIL.StepExecutor;

public class AssertNrcStepExecutorTests
{
    private static TestCaseStep CreateStep(byte serviceId, byte expectedNrc) =>
        TestCaseStep.Create(new AssertNrcStep(serviceId, expectedNrc));

    [Fact]
    public async Task ExecuteAsync_CorrectNrc_Passes()
    {
        // Arrange
        var session = new FakeIUdsSession(sendException: new UdsNrcException(0x22, 0x13));
        var executor = new AssertNrcStepExecutor(session);
        var step = CreateStep(0x22, 0x13);

        // Act
        var result = await executor.ExecuteAsync(step, null!, default);

        // Assert
        Assert.Equal(StepStatus.Passed, result.Status);
        Assert.Contains("0x13", result.Message);
    }

    [Fact]
    public async Task ExecuteAsync_WrongNrc_Fails()
    {
        // Arrange
        var session = new FakeIUdsSession(sendException: new UdsNrcException(0x22, 0x31));
        var executor = new AssertNrcStepExecutor(session);
        var step = CreateStep(0x22, 0x13);

        // Act
        var result = await executor.ExecuteAsync(step, null!, default);

        // Assert
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("NRC mismatch", result.Message);
    }

    [Fact]
    public async Task ExecuteAsync_PositiveResponse_Fails()
    {
        // Arrange
        var session = new FakeIUdsSession(); // no exception = positive response
        var executor = new AssertNrcStepExecutor(session);
        var step = CreateStep(0x22, 0x13);

        // Act
        var result = await executor.ExecuteAsync(step, null!, default);

        // Assert
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("positive response", result.Message);
    }

    [Fact]
    public async Task ExecuteAsync_TransportError_Fails()
    {
        // Arrange
        var session = new FakeIUdsSession(sendException: new UdsSessionTransportException("timeout"));
        var executor = new AssertNrcStepExecutor(session);
        var step = CreateStep(0x22, 0x13);

        // Act
        var result = await executor.ExecuteAsync(step, null!, default);

        // Assert
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("UDS error", result.Message);
    }
}
