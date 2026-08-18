using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.HIL.StepExecutor;

namespace PeakCan.HIL.Core.Tests.HIL.StepExecutor;

/// <summary>
/// AssertVariableStepExecutor 测试：变量断言（hex 字节精确比较 + 数值容差比较）。
/// 用 StubAssertionContext 只实现 IStepVariableStore（executor 只用这一通道），
/// 与 UdsStepExecutorTests.StubAssertionContext 同一模式。
/// </summary>
public class AssertVariableStepExecutorTests
{
    private readonly AssertVariableStepExecutor _executor = new();

    /// <summary>只实现 IStepVariableStore；IAssertionContext 其余成员不用即抛。</summary>
    private sealed class StubAssertionContext : IAssertionContext, IStepVariableStore
    {
        public IDictionary<string, object> Variables { get; } = new Dictionary<string, object>();
        public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame) => throw new NotSupportedException();
        public double? GetSignalValue(string signalName, int maxAgeMs = 5000) => throw new NotSupportedException();
        public double CurrentTimestamp => throw new NotSupportedException();
        public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct) => throw new NotSupportedException();
        public IReadOnlyList<DecodedFrame> GetRecentDecodedFrames() => throw new NotSupportedException();
    }

    [Fact]
    public async Task ExecuteAsync_HexBytes_Match_Passes()
    {
        // Arrange
        var ctx = new StubAssertionContext();
        ctx.Variables["var1"] = new byte[] { 0xAA, 0xBB };

        // Act
        var result = await _executor.ExecuteAsync(
            TestCaseStep.Create(new AssertVariableStep("var1", ExpectedHexBytes: new byte[] { 0xAA, 0xBB }, TimeoutMs: "200")),
            ctx, default);

        // Assert
        Assert.Equal(StepStatus.Passed, result.Status);
        Assert.Contains("matches", result.Message);
    }

    [Fact]
    public async Task ExecuteAsync_HexBytes_Mismatch_Fails()
    {
        // Arrange
        var ctx = new StubAssertionContext();
        ctx.Variables["var1"] = new byte[] { 0xAA, 0xBB };

        // Act
        var result = await _executor.ExecuteAsync(
            TestCaseStep.Create(new AssertVariableStep("var1", ExpectedHexBytes: new byte[] { 0xAA, 0xCC }, TimeoutMs: "200")),
            ctx, default);

        // Assert
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("mismatch", result.Message);
    }

    [Fact]
    public async Task ExecuteAsync_Numeric_WithinTolerance_Passes()
    {
        // Arrange
        var ctx = new StubAssertionContext();
        ctx.Variables["var2"] = 1.55d;

        // Act
        var result = await _executor.ExecuteAsync(
            TestCaseStep.Create(new AssertVariableStep("var2", ExpectedNumeric: "1.5", Tolerance: "0.1", TimeoutMs: "200")),
            ctx, default);

        // Assert
        Assert.Equal(StepStatus.Passed, result.Status);
        Assert.Contains("matches", result.Message);
    }

    [Fact]
    public async Task ExecuteAsync_Numeric_OutOfTolerance_Fails()
    {
        // Arrange
        var ctx = new StubAssertionContext();
        ctx.Variables["var2"] = 2.0d;

        // Act
        var result = await _executor.ExecuteAsync(
            TestCaseStep.Create(new AssertVariableStep("var2", ExpectedNumeric: "1.5", Tolerance: "0.1", TimeoutMs: "200")),
            ctx, default);

        // Assert
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("mismatch", result.Message);
    }

    [Fact]
    public async Task ExecuteAsync_MissingVariable_Fails()
    {
        // Arrange - 无键
        var ctx = new StubAssertionContext();

        // Act
        var result = await _executor.ExecuteAsync(
            TestCaseStep.Create(new AssertVariableStep("nokey", ExpectedNumeric: "1.0", TimeoutMs: "100")),
            ctx, default);

        // Assert
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("not available", result.Message);
    }

    [Fact]
    public async Task ExecuteAsync_TypeMismatch_ByteArray_WhenNumericExpected_Fails()
    {
        // Arrange - 变量是 byte[]，期望数值 → 类型不匹配
        var ctx = new StubAssertionContext();
        ctx.Variables["var3"] = new byte[] { 0x01, 0x02 };

        // Act
        var result = await _executor.ExecuteAsync(
            TestCaseStep.Create(new AssertVariableStep("var3", ExpectedNumeric: "1.5", TimeoutMs: "200")),
            ctx, default);

        // Assert
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("mismatch", result.Message);
    }

    [Fact]
    public async Task ExecuteAsync_NoExpectedValue_Fails()
    {
        // Arrange - ExpectedNumeric 与 ExpectedHexBytes 均 null
        var ctx = new StubAssertionContext();
        ctx.Variables["var4"] = 1.0d;

        // Act
        var result = await _executor.ExecuteAsync(
            TestCaseStep.Create(new AssertVariableStep("var4", TimeoutMs: "200")),
            ctx, default);

        // Assert
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("No expected value specified", result.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ContextNotStepVariableStore_Fails()
    {
        // Arrange - 上下文不实现 IStepVariableStore（e.g. 纯 FakeAssertionContext）
        var ctx = new PeakCan.HIL.Core.Tests.HIL.Fakes.FakeAssertionContext();

        // Act
        var result = await _executor.ExecuteAsync(
            TestCaseStep.Create(new AssertVariableStep("var1", ExpectedNumeric: "1.0", TimeoutMs: "200")),
            ctx, default);

        // Assert
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("does not support IStepVariableStore", result.Message);
    }
}
