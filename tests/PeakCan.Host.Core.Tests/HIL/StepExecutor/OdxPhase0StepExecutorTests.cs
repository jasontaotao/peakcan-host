using FluentAssertions;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.HIL.StepExecutor;
using PeakCan.HIL.Core.Uds;
using PeakCan.HIL.Core.Uds.IsoTp;

namespace PeakCan.HIL.Core.Tests.HIL.StepExecutor;

/// <summary>
/// ODX Phase 0 (Task 0.2) executor tests: ECUReset / CommunicationControl /
/// IOControl executors plus the AssertNrc.Data payload and SecurityAccess
/// SeedOnly branches.
/// <para>
/// AssertNrc 执行器注入 <see cref="IUdsSession"/>（非 UdsClient）—— 直接 mock 接口
/// （参照现有 AssertNrcStepExecutorTests 的 mock 模式；<see cref="SpySession.ReadDtcInformation"/>
/// 抛 NotSupported，执行器只用 SendRequestAsync）。其余执行器注入 <see cref="UdsClient"/>，
/// 继承并 override SendRequestAsync（RecordingUdsClient 模式）。
/// </para>
/// </summary>
public class OdxPhase0StepExecutorTests
{
    /// <summary>Task B 第二步（spec 2026-08-27 §Q1）：executor 吃 resolver，默认分支回落该 session。</summary>
    private static UdsSessionResolver Resolver(IUdsSession session)
        => new UdsSessionResolver(new Dictionary<string, IUdsSession>(StringComparer.Ordinal), () => session);
    private sealed class SpySession : IUdsSession
    {
        public byte? LastSid;
        public byte[]? LastData;
        public Exception? NextException;

        public Task<IReadOnlyList<DtcInfo>> ReadDtcInformation(byte statusMask, CancellationToken ct)
            => throw new NotSupportedException("执行器只用 SendRequestAsync");

        public Task<byte[]> ReadDataByIdentifierAsync(ushort did, CancellationToken ct)
            => throw new NotSupportedException("执行器只用 SendRequestAsync");

        public Task WriteDataByIdentifierAsync(ushort did, byte[] data, CancellationToken ct)
            => throw new NotSupportedException("执行器只用 SendRequestAsync");

        public Task SendRequestAsync(byte serviceId, byte[]? data = null, CancellationToken ct = default)
        {
            // 先记录再抛，这样 NRC 分支测试也能断言 payload 已送达。
            LastSid = serviceId;
            LastData = data;
            if (NextException is not null)
            {
                var e = NextException;
                NextException = null;
                throw e;
            }

            return Task.CompletedTask;
        }

        // Task B 第二步（spec 2026-08-27 §Q1）新增接口方法——本测试只用 SendRequestAsync。
        public Task<DiagnosticSessionResponse> DiagnosticSessionControlAsync(byte sessionType, CancellationToken ct)
            => throw new NotSupportedException("SpySession 未实现");
        public Task ClearDiagnosticInformationAsync(uint groupOfDtc, CancellationToken ct)
            => throw new NotSupportedException("SpySession 未实现");
        public Task<byte[]> RoutineControlAsync(byte routineControlType, ushort routineId, byte[]? data, CancellationToken ct)
            => throw new NotSupportedException("SpySession 未实现");
        public Task<byte[]> RequestSeedAsync(byte level, CancellationToken ct)
            => throw new NotSupportedException("SpySession 未实现");
        public Task<byte[]> SecurityAccessAsync(byte level, CancellationToken ct)
            => throw new NotSupportedException("SpySession 未实现");
        public Task<byte> EcuResetAsync(byte resetType, CancellationToken ct)
            => throw new NotSupportedException("SpySession 未实现");
        public Task TesterPresentAsync(bool suppressPosResponse, CancellationToken ct)
            => throw new NotSupportedException("SpySession 未实现");
        public Task<byte[]> IOControlAsync(ushort did, byte controlType, byte[]? data, byte controlEnableMask = 0xFF, CancellationToken ct = default)
            => throw new NotSupportedException("SpySession 未实现");
    }

    private sealed class SpyUdsClient : UdsClient
    {
        public byte? LastSid;
        public byte[]? LastData;
        public readonly List<(byte SID, byte[]? Data)> Calls = new();
        public byte[] SeedResponse = new byte[] { 0x01, 0xDE, 0xAD };
        public bool ThrowOnTesterPresent;

        public SpyUdsClient() : base(
            new IsoTpLayer(new CanIdConfig { RequestId = 0x7E0, ResponseId = 0x7E8 }, _ => { }),
            new UdsTimer())
        {
        }

        public override Task<byte[]> SendRequestAsync(
            byte serviceId, byte[]? data = null, CancellationToken ct = default)
        {
            Calls.Add((serviceId, data));
            LastSid = serviceId;
            LastData = data;
            if (serviceId == 0x3E && ThrowOnTesterPresent)
                throw new UdsException("ECU did not respond to TesterPresent after reset");
            return Task.FromResult(serviceId == 0x27 ? SeedResponse : new byte[] { 0x40 });
        }
    }

    private sealed class DummyContext : IAssertionContext, IStepVariableStore
    {
        public IDictionary<string, object> Variables { get; } = new Dictionary<string, object>();
        public double CurrentTimestamp => 0;

        public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame) => new NullDisposable();
        public double? GetSignalValue(string signalName, int maxAgeMs = 5000) => null;
        public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct)
            => ValueTask.FromResult(Result<Unit>.Ok(default));
        public IReadOnlyList<DecodedFrame> GetRecentDecodedFrames() => Array.Empty<DecodedFrame>();

        private sealed class NullDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }

    [Fact]
    public async Task AssertNrc_WithData_SendsPayload()
    {
        var uds = new SpySession();
        var ex = new AssertNrcStepExecutor(Resolver(uds));
        var step = TestCaseStep.Create(new AssertNrcStep(0x22, 0x33, new byte[] { 0xF1, 0x90 }));

        var result = await ex.ExecuteAsync(step, new DummyContext(), default);

        result.Passed.Should().BeFalse();            // 正响应 → 期望 NRC 未出现 → fail
        uds.LastSid.Should().Be(0x22);
        uds.LastData.Should().BeEquivalentTo(new byte[] { 0xF1, 0x90 });
    }

    [Fact]
    public async Task AssertNrc_WithoutData_BackCompat()
    {
        var uds = new SpySession();
        var ex = new AssertNrcStepExecutor(Resolver(uds));
        var step = TestCaseStep.Create(new AssertNrcStep(0x22, 0x33));

        await ex.ExecuteAsync(step, new DummyContext(), default);

        uds.LastData.Should().BeNull();              // 旧语义：仅发 SID
    }

    [Fact]
    public async Task SecurityAccess_SeedOnly_FetchesSeedWithoutUnlocking()
    {
        var uds = new SpyUdsClient();
        var ex = new SecurityAccessStepExecutor(Resolver(new UdsSessionAdapter(uds)));
        var step = TestCaseStep.Create(new SecurityAccessStep(0x01, SeedOnly: true));
        var ctx = new DummyContext();

        var result = await ex.ExecuteAsync(step, ctx, default);

        result.Passed.Should().BeTrue();
        uds.LastSid.Should().Be(0x27);
        uds.LastData.Should().BeEquivalentTo(new byte[] { 0x01 });   // seed request，非 key-verify
        ((byte[])ctx.Variables["security_seed"]).Should().BeEquivalentTo(
            new byte[] { 0xDE, 0xAD },
            "seed 响应 [0x01, 0xDE, 0xAD] 剥离 subfunction 后应存入变量 store");
    }

    [Fact]
    public async Task CommunicationControl_Sends_PhysicalAddressing()
    {
        var uds = new SpyUdsClient();
        var ex = new CommunicationControlStepExecutor(Resolver(new UdsSessionAdapter(uds)));
        var step = TestCaseStep.Create(new CommunicationControlStep(0x00));

        var result = await ex.ExecuteAsync(step, new DummyContext(), default);

        result.Passed.Should().BeTrue();
        uds.LastSid.Should().Be(0x28);
        uds.LastData.Should().BeEquivalentTo(new byte[] { 0x00 });
    }

    [Fact]
    public async Task ECUReset_SendsReset_ThenPollsTesterPresent()
    {
        var uds = new SpyUdsClient();
        var ex = new ECUResetStepExecutor(Resolver(new UdsSessionAdapter(uds)));
        var step = TestCaseStep.Create(new ECUResetStep(0x01));

        var result = await ex.ExecuteAsync(step, new DummyContext(), default);

        result.Passed.Should().BeTrue();
        uds.Calls.Should().Contain(c => c.SID == 0x11 && c.Data!.SequenceEqual(new byte[] { 0x01 }),
            "reset leg must send 0x11 + resetType");
        uds.Calls.Should().Contain(c => c.SID == 0x3E && c.Data!.SequenceEqual(new byte[] { 0x80 }),
            "reconnect poll must send suppressed-pos-response TesterPresent 0x3E 0x80");
    }

    [Fact]
    public async Task IOControl_SendsDidMaskParam()
    {
        var uds = new SpyUdsClient();
        var ex = new IOControlStepExecutor(Resolver(new UdsSessionAdapter(uds)));
        var step = TestCaseStep.Create(new IOControlStep(0xF191, 0x03, new byte[] { 0xAB }));

        var result = await ex.ExecuteAsync(step, new DummyContext(), default);

        result.Passed.Should().BeTrue();
        uds.LastSid.Should().Be(0x2F);
        uds.LastData.Should().BeEquivalentTo(new byte[] { 0xF1, 0x91, 0xFF, 0xAB });
    }

    [Fact]
    public async Task AssertNrc_ExpectedNrc_Passes()
    {
        var uds = new SpySession { NextException = new UdsNrcException(0x22, 0x33) };
        var ex = new AssertNrcStepExecutor(Resolver(uds));
        var step = TestCaseStep.Create(new AssertNrcStep(0x22, 0x33, new byte[] { 0xF1, 0x90 }));

        var result = await ex.ExecuteAsync(step, new DummyContext(), default);

        result.Passed.Should().BeTrue();                 // NRC 0x33 匹配期望 → pass
        result.Status.Should().Be(StepStatus.Passed);
        uds.LastSid.Should().Be(0x22);
        uds.LastData.Should().BeEquivalentTo(new byte[] { 0xF1, 0x90 },
            "NRC 分支也必须把 Data payload 送到 SendRequestAsync");
    }

    [Fact]
    public async Task ECUReset_NoReconnect_ReturnsFailed()
    {
        var uds = new SpyUdsClient { ThrowOnTesterPresent = true };
        var ex = new ECUResetStepExecutor(Resolver(new UdsSessionAdapter(uds)), reconnectTimeoutMs: 50);
        var step = TestCaseStep.Create(new ECUResetStep(0x01));

        var result = await ex.ExecuteAsync(step, new DummyContext(), default);

        result.Passed.Should().BeFalse();
        result.Status.Should().Be(StepStatus.Failed);
        result.Message.Should().Contain("did not reconnect",
            "reset 被确认但 TesterPresent 在超时窗口内无响应 → 必须报 Failed，而非静默 Pass");
        uds.Calls.Should().Contain(c => c.SID == 0x11, "reset leg 必须先发出");
        uds.Calls.Should().Contain(c => c.SID == 0x3E, "reconnect poll 应尝试 TesterPresent");
    }
}
