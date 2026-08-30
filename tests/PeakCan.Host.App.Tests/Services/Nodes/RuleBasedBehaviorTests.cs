using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.J1939;
using PeakCan.Host.App.Services.Nodes;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Nodes;

/// <summary>手写 INodeContext 测试替身：捕获 Send/Report，手动引发 MessageArrived。</summary>
internal sealed class FakeNodeContext(NodeRuntimeState? runtime = null) : INodeContext
{
    public NodeIdentity Identity { get; } = new J1939NodeIdentity(0x56);
    public NodeRuntimeState Runtime { get; } = runtime ?? new NodeRuntimeState();
    public TimeProvider Clock { get; } = new FakeTimeProvider();
    public List<(MessageRef Ref, NodePayloadSource Payload)> Sent { get; } = new();
    public List<(NodeActivityKind Kind, string Detail)> Reports { get; } = new();
    public event Action<NodeMessageArrived>? MessageArrived;
#pragma warning disable CS0067 // SendFailed 在此替身中不引发（行为引擎仅订阅）
    public event Action<Exception>? SendFailed;
#pragma warning restore CS0067
    public event Action<NodeActivityKind, string>? Reported;

    public void Send(MessageRef target, NodePayloadSource payload) => Sent.Add((target, payload));
    public void Report(NodeActivityKind kind, string detail) { Reports.Add((kind, detail)); Reported?.Invoke(kind, detail); }
    public void Arrive(MessageRef refr, byte sa = 0xF4, byte[]? payload = null)
        => MessageArrived?.Invoke(new NodeMessageArrived(refr, sa, payload ?? new byte[] { 0xAA }, 1.0));
}

public class RuleBasedBehaviorTests
{
    private static readonly J1939MessageRef BroRef = new(0x000900, 6, TpMode.Single, 0xF4);
    private static readonly J1939MessageRef CcsRef = new(0x001200, 6, TpMode.Bam, null, 0xF4);
    private static readonly J1939MessageRef CrmRef = new(0x000100, 6, TpMode.Single, null, 0xF4);

    // 计划缺陷修正：生产中 NodeHostService 把 ctx.MessageArrived 入队后由 consumer 调用 behavior.OnMessageArrived；
    // 本替身无宿主，需按同一接线把到达直派给行为（行为本身不自订阅——保持 SDK 读线程只入队的架构）。
    private static RuleBasedBehavior CreateBehavior(FakeNodeContext ctx)
    {
        var behavior = new RuleBasedBehavior(
            new List<NodeMessage>
            {
                new(CrmRef, 250, new FixedHexSource("AA 00"), true),        // 周期发
                new(CcsRef, 50, new FixedHexSource("A0 0F 88 13 3C 00 01"), false),  // Enabled=false：等规则启动
            },
            new List<ResponseRule>
            {
                new(BroRef, null, new StartMessageAction(CcsRef), 0),
                new(new J1939MessageRef(0x001200, 6, null, null),
                    new BytePattern(0, 0xFF, 0xEE),                          // 条件：payload[0]==0xEE
                    new SetSignalAction("CCS", "voltage", 400.0), 0),
            });
        ctx.MessageArrived += behavior.OnMessageArrived;
        return behavior;
    }

    [Fact]
    public void Start_Schedules_Enabled_Messages_Only()
    {
        var ctx = new FakeNodeContext();
        var behavior = CreateBehavior(ctx);
        behavior.Attach(ctx);
        ((FakeTimeProvider)ctx.Clock).Advance(TimeSpan.FromMilliseconds(300));

        ctx.Sent.Where(s => s.Ref == CrmRef).Should().NotBeEmpty();     // 周期消息已发
        ctx.Sent.Where(s => s.Ref == CcsRef).Should().BeEmpty();        // Enabled=false 未发
    }

    [Fact]
    public void Rule_StartMessage_Enables_Periodic_Sending()
    {
        var ctx = new FakeNodeContext();
        var behavior = CreateBehavior(ctx);
        behavior.Attach(ctx);
        ctx.Arrive(BroRef);

        ((FakeTimeProvider)ctx.Clock).Advance(TimeSpan.FromMilliseconds(120));

        ctx.Sent.Where(s => s.Ref == CcsRef).Should().NotBeEmpty();     // BRO 触发 CCS 周期
        ctx.Reports.Should().Contain(r => r.Kind == NodeActivityKind.RuleMatched);
    }

    [Fact]
    public void Rule_Condition_Gates_Action()
    {
        var ctx = new FakeNodeContext();
        var behavior = CreateBehavior(ctx);
        behavior.Attach(ctx);
        ctx.Arrive(new J1939MessageRef(0x001200, 6, TpMode.Bam, 0x56), payload: new byte[] { 0xAA });  // 不满足条件

        ctx.Runtime.TryGetSignalValue("CCS", "voltage", out _).Should().BeFalse();

        ctx.Arrive(new J1939MessageRef(0x001200, 6, TpMode.Bam, 0x56), payload: new byte[] { 0xEE });  // 满足
        ctx.Runtime.TryGetSignalValue("CCS", "voltage", out var v).Should().BeTrue();
        v.Should().Be(400.0);
    }

    [Fact]
    public void StopMessage_Disables_Periodic_Sending()
    {
        var ctx = new FakeNodeContext();
        var behavior = new RuleBasedBehavior(
            new List<NodeMessage> { new(CcsRef, 50, new FixedHexSource("01"), true) },
            new List<ResponseRule>
            {
                new(new J1939MessageRef(0x001900, 6, null, 0xF4), null,
                    new StopMessageAction(CcsRef), 0),
            });
        ctx.MessageArrived += behavior.OnMessageArrived;   // 模拟宿主 consumer 直派
        behavior.Attach(ctx);
        ctx.Arrive(new J1939MessageRef(0x001900, 6, TpMode.Single, 0xF4));
        var before = ctx.Sent.Count;

        ((FakeTimeProvider)ctx.Clock).Advance(TimeSpan.FromMilliseconds(200));

        ctx.Sent.Count.Should().Be(before);   // BST 停发 CCS
    }

    [Fact]
    public void DelayMs_Defers_Action()
    {
        var ctx = new FakeNodeContext();
        var behavior = new RuleBasedBehavior(
            Array.Empty<NodeMessage>(),
            new List<ResponseRule>
            {
                new(BroRef, null,
                    new SendMessageAction(CrmRef, new FixedHexSource("AA")), 100),
            });
        ctx.MessageArrived += behavior.OnMessageArrived;   // 模拟宿主 consumer 直派
        behavior.Attach(ctx);

        ctx.Arrive(BroRef);
        ctx.Sent.Should().BeEmpty();                                          // 未到延迟

        ((FakeTimeProvider)ctx.Clock).Advance(TimeSpan.FromMilliseconds(120));
        ctx.Sent.Should().ContainSingle();                                    // 延迟到期
    }

    [Fact]
    public void ScriptAction_Reports_Not_Supported()
    {
        var ctx = new FakeNodeContext();
        var behavior = new RuleBasedBehavior(
            Array.Empty<NodeMessage>(),
            new List<ResponseRule> { new(BroRef, null, new ScriptAction("s.js"), 0) });
        ctx.MessageArrived += behavior.OnMessageArrived;   // 模拟宿主 consumer 直派
        behavior.Attach(ctx);

        ctx.Arrive(BroRef);

        ctx.Sent.Should().BeEmpty();
        ctx.Reports.Should().Contain(r => r.Kind == NodeActivityKind.Error && r.Detail.Contains("尚未支持"));
    }

    [Fact]
    public void Detach_Stops_Scheduling()
    {
        var ctx = new FakeNodeContext();
        var behavior = new RuleBasedBehavior(
            new List<NodeMessage> { new(CrmRef, 250, new FixedHexSource("AA"), true) },
            Array.Empty<ResponseRule>());
        behavior.Attach(ctx);
        behavior.Detach();
        var count = ctx.Sent.Count;

        ((FakeTimeProvider)ctx.Clock).Advance(TimeSpan.FromMilliseconds(1000));

        ctx.Sent.Count.Should().Be(count);
    }
}
