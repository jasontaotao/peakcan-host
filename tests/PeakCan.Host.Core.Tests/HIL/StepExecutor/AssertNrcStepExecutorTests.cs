using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.HIL.StepExecutor;
using PeakCan.HIL.Core.Tests.HIL.Fakes;
using Xunit;
using PeakCan.HIL.Core.Uds;

namespace PeakCan.HIL.Core.Tests.HIL.StepExecutor;

public class AssertNrcStepExecutorTests
{
    /// <summary>Task B 第二步（spec 2026-08-27 §Q1）：executor 吃 resolver，默认分支回落该 session。</summary>
    private static UdsSessionResolver Resolver(IUdsSession session)
        => new UdsSessionResolver(new Dictionary<string, IUdsSession>(StringComparer.Ordinal), () => session);
    private static TestCaseStep CreateStep(byte serviceId, byte expectedNrc) =>
        TestCaseStep.Create(new AssertNrcStep(serviceId, expectedNrc));

    [Fact]
    public async Task ExecuteAsync_CorrectNrc_Passes()
    {
        // Arrange
        var session = new FakeIUdsSession(sendException: new UdsNrcException(0x22, 0x13));
        var executor = new AssertNrcStepExecutor(Resolver(session));
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
        var executor = new AssertNrcStepExecutor(Resolver(session));
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
        var executor = new AssertNrcStepExecutor(Resolver(session));
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
        var executor = new AssertNrcStepExecutor(Resolver(session));
        var step = CreateStep(0x22, 0x13);

        // Act
        var result = await executor.ExecuteAsync(step, null!, default);

        // Assert
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("UDS error", result.Message);
    }
}
