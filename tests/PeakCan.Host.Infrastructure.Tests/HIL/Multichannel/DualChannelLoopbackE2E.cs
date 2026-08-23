using System.Globalization;
using System.Linq;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Assertions;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.HIL.StepExecutor;
using PeakCan.Host.Infrastructure.HIL;
using PeakCan.Host.Infrastructure.Tests.HIL;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Multichannel;

/// <summary>
/// 双通道 loopback 端到端测试（spec §3.7 / Task 12）：
/// 半 e2e——直构造 MultiChannelAssertionContext + 2 FakeCanChannel + TestSuiteEngine，
/// 不走 HeadlessHostBuilder/RunAsync（多通道路径强制 PeakCanChannel 真硬件，无注入点）。
/// 双通道 loopback：A.FrameReceived → B.SimulateFrame 桥接（模拟物理总线跨通道）。
/// 验证：SendFrame(bus-a) 路由到 A + ExpectFrame(bus-b) 在 B 侧命中 + StepResult.Channel 正确 +
/// case log sink 收到带正确 Channel 的帧 + 单通道路径零回归。
/// </summary>
public sealed class DualChannelLoopbackE2E : IDisposable
{
    private readonly FakeCanChannel _chA = new(handle: 0x51);
    private readonly FakeCanChannel _chB = new(handle: 0x52);
    private readonly MultiChannelAssertionContext _ctx;

    public DualChannelLoopbackE2E()
    {
        var ctxA = new SingleChannelContext(_chA, new FakeDbcLookup(), channelName: "bus-a");
        var ctxB = new SingleChannelContext(_chB, new FakeDbcLookup(), channelName: "bus-b");
        _ctx = new MultiChannelAssertionContext(
            new Dictionary<string, SingleChannelContext>
            {
                ["bus-a"] = ctxA,
                ["bus-b"] = ctxB,
            },
            defaultChannelName: "bus-a");
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _chA.Dispose();
        _chB.Dispose();
    }

    /// <summary>桥接 A→B：A 发出的帧推给 B（改 Channel 为 B.Id，模拟物理总线跨通道接收）。</summary>
    private void BridgeAToB()
    {
        _chA.FrameReceived += f =>
        {
            // 改 Channel 为 B 的物理 Id（模拟帧经总线到达 B 的接收侧）
            _chB.SimulateFrame(f with { Channel = _chB.Id });
        };
    }

    private static TestCaseStep SendFrameStep(uint rawId, byte[] data, string channel) =>
        TestCaseStep.Create(new SendFrameStep(
            new CanId(rawId, FrameFormat.Standard),
            data,
            Fd: false,
            Extended: false)
        { TargetChannel = channel });

    private static TestCaseStep ExpectFrameStep(uint rawId, string channel, int timeoutMs = 5000) =>
        TestCaseStep.Create(new ExpectFrameStep(
            new CanId(rawId, FrameFormat.Standard),
            DataMask: null,
            TimeoutMs: timeoutMs.ToString(CultureInfo.InvariantCulture))
        { TargetChannel = channel });

    private static TestSuite TwoStepSuite(TestCaseStep step1, TestCaseStep step2)
        => new(
            Name: "DualChannelE2E",
            Cases: new[]
            {
                new TestCase("c1", "LoopbackCase", "", null,
                    new[] { step1, step2 }, null, Array.Empty<string>())
            },
            GlobalCaseFixtureKeys: Array.Empty<string>(),
            SuiteFixtureKeys: Array.Empty<string>(),
            Config: new TestSuiteConfig(),
            TimeoutMs: 0,
            Channels: new[]
            {
                new ChannelConfig("bus-a", "51", BaudRate.Can500kbps, Fd: false),
                new ChannelConfig("bus-b", "52", BaudRate.Can500kbps, Fd: false),
            });

    /// <summary>Recording sink：收集所有通道写出的帧（带各自 Channel），验证 case log 标通道。</summary>
    private sealed class RecordingSinkFactory : IHilFrameSinkFactory
    {
        public List<CanFrame> Frames { get; } = new();
        public IHilFrameSink Create(string caseName, int caseIndex)
        {
            var sink = new RecordingSink(Frames);
            return sink;
        }
    }

    private sealed class RecordingSink : IHilFrameSink
    {
        private readonly List<CanFrame> _frames;
        public RecordingSink(List<CanFrame> frames) => _frames = frames;
        public RecordingSink() => _frames = new List<CanFrame>();
        public List<CanFrame> Frames => _frames;
        public void Write(CanFrame f) => _frames.Add(f);
        public void Dispose() { }
    }

    private sealed class FakeDbcLookup : IDbcLookup
    {
        public Message? FindMessage(uint canId) => null;
        public IEnumerable<Message> GetAllMessages() => Array.Empty<Message>();
    }

    /// <summary>构造 engine：真实 SendFrame + ExpectFrame 执行器（internal，Core 已 InternalsVisibleTo）。
    /// AssertionPrimitives 注入 _ctx（MultiChannelAssertionContext）使 ExpectFrame 走 channel 路由。</summary>
    private TestSuiteEngine CreateEngine()
    {
        var primitives = new AssertionPrimitives(_ctx);
        return new TestSuiteEngine(
            new HeadlessFixtureResolver(),
            new IStepExecutor[]
            {
                new SendFrameStepExecutor(),
                new ExpectFrameStepExecutor(primitives),
            });
    }

    [Fact]
    public async Task DualChannel_SendFrameOnBusA_ExpectFrameOnBusB_Passes()
    {
        // Arrange：桥接 A→B + 连通道 + 构造 suite（bus-a 发 0x100，bus-b 等 0x100）
        BridgeAToB();
        await _ctx.ConnectAllAsync(name => (BaudRate.Can500kbps, false), default);

        var suite = TwoStepSuite(
            SendFrameStep(0x100, new byte[] { 0xAA }, "bus-a"),
            ExpectFrameStep(0x100, "bus-b"));

        var engine = CreateEngine();

        // Act
        var result = await engine.ExecuteAsync(suite, _ctx, new TestSuiteConfig(), null, default);

        // Assert：两步全过（bus-a 发出 → 桥接 → bus-b 缓冲命中 ExpectFrame）
        Assert.True(result.AllPassed,
            $"Expected all passed, got {result.PassedCases}/{result.TotalCases}. " +
            $"Failures: {string.Join("; ", result.CaseResults.SelectMany(c => c.StepResults.Where(s => !s.Passed).Select(s => s.Message)))}");
    }

    [Fact]
    public async Task DualChannel_StepResultChannelReflectsTargetChannel()
    {
        // 验证 StepResult.Channel 正确标出每步的 TargetChannel（Task 9 执行器填充）
        BridgeAToB();
        await _ctx.ConnectAllAsync(name => (BaudRate.Can500kbps, false), default);

        var suite = TwoStepSuite(
            SendFrameStep(0x200, new byte[] { 0x01 }, "bus-a"),
            ExpectFrameStep(0x200, "bus-b"));

        var engine = CreateEngine();
        var result = await engine.ExecuteAsync(suite, _ctx, new TestSuiteConfig(), null, default);

        var steps = result.CaseResults.Single().StepResults;
        Assert.Equal("bus-a", steps[0].Channel);
        Assert.Equal("bus-b", steps[1].Channel);
    }

    [Fact]
    public async Task DualChannel_CaseLogSink_ReceivesFramesWithCorrectChannel()
    {
        // 验证 case log sink 收到带正确 Channel 的帧（A.Id/B.Id——asc channel 列的源）
        BridgeAToB();
        await _ctx.ConnectAllAsync(name => (BaudRate.Can500kbps, false), default);

        var sinkFactory = new RecordingSinkFactory();
        var suite = TwoStepSuite(
            SendFrameStep(0x300, new byte[] { 0x42 }, "bus-a"),
            ExpectFrameStep(0x300, "bus-b"));

        var engine = CreateEngine();
        var result = await engine.ExecuteAsync(suite, _ctx, new TestSuiteConfig(), null, default,
            sinkFactory, frameStats: null);

        // 等待 sink 收到两路帧（ConsumerLoop 异步消费，全量跑时线程竞争可能慢；
        // 轮询比 WaitForFrameDrainAsync 的 500ms 内部 cap 更稳——drain 在 cap 内未排空会提前返回）
        var chA = new ChannelId(0x51);
        var chB = new ChannelId(0x52);
        var pollDeadline = DateTime.UtcNow.AddSeconds(5);
        while ((DateTime.UtcNow < pollDeadline) &&
               (!sinkFactory.Frames.Any(f => f.Channel == chA) ||
                !sinkFactory.Frames.Any(f => f.Channel == chB)))
        {
            await Task.Delay(20);
        }

        // sink 扇出到所有通道，应收到 A 发出帧（Channel=A.Id=0x51）+ B 桥接收到帧（Channel=B.Id=0x52）
        Assert.Contains(sinkFactory.Frames, f => f.Channel == chA);  // A 侧
        Assert.Contains(sinkFactory.Frames, f => f.Channel == chB);  // B 侧（桥接改 Channel）

        Assert.True(result.AllPassed);
    }

    [Fact]
    public async Task DualChannel_SendOnBusB_NotSeenByBusA_WhenNoBridge_B()
    {
        // 反向验证：无 B→A 桥接时，bus-b 发的帧不会出现在 bus-a 的 ExpectFrame（应超时失败）
        // 只桥 A→B（单向），bus-b 发帧 bus-a 收不到 → ExpectFrame(bus-a) 超时 Fail
        // 但 SendFrame(bus-b) 帧会 loopback 到 bus-b 自己（FakeCanChannel.WriteAsync 同步 raise）
        // bus-a 的 ExpectFrame 等的是 bus-a 侧缓冲——bus-b 发的没桥到 bus-a → 超时
        BridgeAToB();  // 只 A→B
        await _ctx.ConnectAllAsync(name => (BaudRate.Can500kbps, false), default);

        var suite = TwoStepSuite(
            SendFrameStep(0x400, new byte[] { 0x01 }, "bus-b"),   // bus-b 发
            ExpectFrameStep(0x400, "bus-a", timeoutMs: 1000));    // bus-a 等（无 B→A 桥，超时）

        var engine = CreateEngine();
        var result = await engine.ExecuteAsync(suite, _ctx, new TestSuiteConfig(), null, default);

        // ExpectFrame(bus-a) 应超时失败（bus-b 发的没到 bus-a）
        Assert.False(result.AllPassed);
        var expectStep = result.CaseResults.Single().StepResults[1];
        Assert.Equal(StepStatus.Failed, expectStep.Status);
        Assert.Equal("bus-a", expectStep.Channel);
    }
}
