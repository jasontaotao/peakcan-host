using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.HIL.StepExecutor;
using PeakCan.HIL.Core.Tests.HIL.Fakes;
using PeakCan.HIL.Core.Uds;

namespace PeakCan.HIL.Core.Tests.HIL.StepExecutor;

/// <summary>
/// ReadDid / WriteDid executor 接口注入测试（spec 2026-08-27 Task B 第一步，Q1）：
/// executor 依赖 IUdsSession 而非 concrete UdsClient——多通道路由（resolver）的前置统一。
/// 断言口径与 AssertDtcStepExecutorTests 一致：StepStatus + 消息关键词 + 变量写入。
/// 异常契约遵循 Contracts 边界：NRC → UdsNrcException，传输失败 → UdsSessionTransportException。
/// </summary>
public class DidStepExecutorTests
{
    /// <summary>Task B 第二步（spec 2026-08-27 §Q1）：executor 吃 resolver，默认分支回落该 session。</summary>
    private static UdsSessionResolver Resolver(IUdsSession session)
        => new UdsSessionResolver(new Dictionary<string, IUdsSession>(StringComparer.Ordinal), () => session);
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

    // ---- ReadDid ----

    [Fact]
    public async Task ReadDid_ViaIUdsSession_ReturnsDataAndWritesVariable()
    {
        // Arrange
        var session = new FakeIUdsSession(readDidResponse: new byte[] { 0xAA, 0xBB });
        var executor = new ReadDidStepExecutor(Resolver(session));
        var ctx = new StubAssertionContext();

        // Act
        var result = await executor.ExecuteAsync(
            TestCaseStep.Create(new ReadDidStep(0xF190)), ctx, default);

        // Assert
        Assert.Equal(StepStatus.Passed, result.Status);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, (byte[])ctx.Variables["did_0xF190"]);
        Assert.Equal((ushort)0xF190, session.LastReadDid);
    }

    [Fact]
    public async Task ReadDid_ViaIUdsSession_WithOutputVar_UsesCustomKey()
    {
        // Arrange — OutputVar 覆盖默认 did_ 变量键
        var session = new FakeIUdsSession(readDidResponse: new byte[] { 0x01 });
        var executor = new ReadDidStepExecutor(Resolver(session));
        var ctx = new StubAssertionContext();

        // Act
        var result = await executor.ExecuteAsync(
            TestCaseStep.Create(new ReadDidStep(0xF190, "vin")), ctx, default);

        // Assert
        Assert.Equal(StepStatus.Passed, result.Status);
        Assert.Equal(new byte[] { 0x01 }, (byte[])ctx.Variables["vin"]);
    }

    [Fact]
    public async Task ReadDid_NrcViaIUdsSession_Fails()
    {
        // Arrange — 接口契约：NRC 以 UdsNrcException 抛出
        var session = new FakeIUdsSession(readDidException: new UdsNrcException(0x22, 0x31));
        var executor = new ReadDidStepExecutor(Resolver(session));

        // Act
        var result = await executor.ExecuteAsync(
            TestCaseStep.Create(new ReadDidStep(0xF190)), new StubAssertionContext(), default);

        // Assert
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("ReadDID", result.Message);
    }

    [Fact]
    public async Task ReadDid_TransportErrorViaIUdsSession_Fails()
    {
        // Arrange — 接口契约：传输失败以 UdsSessionTransportException 抛出
        var session = new FakeIUdsSession(readDidException: new UdsSessionTransportException("P2 timeout"));
        var executor = new ReadDidStepExecutor(Resolver(session));

        // Act
        var result = await executor.ExecuteAsync(
            TestCaseStep.Create(new ReadDidStep(0xF190)), new StubAssertionContext(), default);

        // Assert
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("ReadDID", result.Message);
        // 回归锚定（review MEDIUM）：adapter 不加 "ReadDID ... failed:" 前缀，executor 只拼一次
        Assert.DoesNotContain("failed: ReadDID", result.Message);
    }

    // ---- WriteDid ----

    [Fact]
    public async Task WriteDid_ViaIUdsSession_WritesData()
    {
        // Arrange
        var session = new FakeIUdsSession();
        var executor = new WriteDidStepExecutor(Resolver(session));

        // Act
        var result = await executor.ExecuteAsync(
            TestCaseStep.Create(new WriteDidStep(0xF190, new byte[] { 0x01, 0x02 })),
            new StubAssertionContext(), default);

        // Assert
        Assert.Equal(StepStatus.Passed, result.Status);
        Assert.True(session.WriteDidCalled);
        Assert.Equal((ushort)0xF190, session.LastWrittenDid);
        Assert.Equal(new byte[] { 0x01, 0x02 }, session.LastWrittenData);
    }

    [Fact]
    public async Task WriteDid_NrcViaIUdsSession_Fails()
    {
        // Arrange — 0x33 = securityAccessDenied
        var session = new FakeIUdsSession(writeDidException: new UdsNrcException(0x2E, 0x33));
        var executor = new WriteDidStepExecutor(Resolver(session));

        // Act
        var result = await executor.ExecuteAsync(
            TestCaseStep.Create(new WriteDidStep(0xF190, new byte[] { 0x01 })),
            new StubAssertionContext(), default);

        // Assert
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("WriteDID", result.Message);
    }

    [Fact]
    public async Task WriteDid_TransportErrorViaIUdsSession_Fails()
    {
        // Arrange — 补齐写路径传输错误用例（review LOW：与读路径对称）
        var session = new FakeIUdsSession(writeDidException: new UdsSessionTransportException("P2 timeout"));
        var executor = new WriteDidStepExecutor(Resolver(session));

        // Act
        var result = await executor.ExecuteAsync(
            TestCaseStep.Create(new WriteDidStep(0xF190, new byte[] { 0x01 })),
            new StubAssertionContext(), default);

        // Assert
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("WriteDID", result.Message);
        Assert.DoesNotContain("failed: WriteDID", result.Message);
    }
}
