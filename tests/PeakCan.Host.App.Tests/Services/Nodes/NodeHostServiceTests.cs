using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.J1939;
using PeakCan.Host.App.Services.Nodes;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Nodes;

/// <summary>记录 OnMessageArrived 的假 behavior（经真实 NodeHostService 队列验证投递）。</summary>
internal sealed class RecordingBehavior : INodeBehavior
{
    public List<NodeMessageArrived> Arrived { get; } = new();
    public INodeContext? AttachedTo { get; private set; }
    public void Attach(INodeContext ctx) => AttachedTo = ctx;
    public void Detach() => AttachedTo = null;
    public void OnMessageArrived(NodeMessageArrived message) => Arrived.Add(message);
}

public class NodeHostServiceTests : IDisposable
{
    private readonly List<FakeNodeContext> _contexts = new();
    private readonly RecordingBehavior _behavior = new();

    /// <summary>测试行为固定为共享的 RecordingBehavior（xunit 每测试新建实例，安全）。</summary>
    private NodeHostService CreateHost() => new(
        (config, runtime) =>
        {
            var ctx = new FakeNodeContext(runtime);
            _contexts.Add(ctx);
            return ctx;
        },
        behaviorFactory: _ => _behavior);

    [Fact]
    public async Task Add_Start_Delivers_Message_Via_Queue()
    {
        var host = CreateHost();
        host.AddNode(Config("n1")).IsSuccess.Should().BeTrue();

        host.StartNode("n1").IsSuccess.Should().BeTrue();
        _behavior.AttachedTo.Should().NotBeNull();

        // 后端（SDK 读线程模拟）引发到达 → 入队 → consumer → behavior
        _contexts[0].Arrive(new J1939MessageRef(0x000900, 6, TpMode.Single, 0xF4));

        // 计划原文 await Task.Delay(50) 是对 consumer 调度的时序赌注（无界失败面）；
        // 改为有界轮询（上限 2s）：断言语义不变（仍 ContainSingle），只去掉竞态。
        for (var i = 0; i < 200 && _behavior.Arrived.Count == 0; i++)
            await Task.Delay(10);

        _behavior.Arrived.Should().ContainSingle();
    }

    [Fact]
    public void StartNode_Rejects_Sa_Conflict_Among_Running()
    {
        var host = CreateHost();
        host.AddNode(Config("a", sa: 0x56));
        host.AddNode(Config("b", sa: 0x56));

        host.StartNode("a").IsSuccess.Should().BeTrue();
        var r = host.StartNode("b");

        r.IsSuccess.Should().BeFalse();
        r.Error!.Message.Should().Contain("0x56");
    }

    [Fact]
    public void Stopped_Nodes_May_Share_Sa()
    {
        var host = CreateHost();
        host.AddNode(Config("a", sa: 0x56));
        host.AddNode(Config("b", sa: 0x56));
        host.StartNode("a");
        host.StopNode("a");

        host.StartNode("b").IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Start_And_Stop_Are_Idempotent()
    {
        var host = CreateHost();
        host.AddNode(Config("n1"));

        host.StartNode("n1").IsSuccess.Should().BeTrue();
        host.StartNode("n1").IsSuccess.Should().BeTrue();   // 幂等
        host.StopNode("n1").IsSuccess.Should().BeTrue();
        host.StopNode("n1").IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void RemoveNode_While_Running_Fails()
    {
        var host = CreateHost();
        host.AddNode(Config("n1"));
        host.StartNode("n1");

        host.RemoveNode("n1").IsSuccess.Should().BeFalse();

        host.StopNode("n1");
        host.RemoveNode("n1").IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void StartAll_Filters_By_Tag()
    {
        // 计划缺陷修正：三个节点沿用默认 SA 0x56 会让 'c' 在 StartAll 时撞上 SA 冲突规则
        //（'a' 已运行，同 SA 拒启——正是本任务实现的校验），期望 {"a","c"} 永不可能成立；
        // 本测试目标是标签过滤，改用互异 SA。
        var host = CreateHost();
        host.AddNode(Config("a", sa: 0x10, tag: "gbt"));
        host.AddNode(Config("b", sa: 0x11, tag: null));
        host.AddNode(Config("c", sa: 0x12, tag: "gbt"));

        host.StartAll("gbt");

        host.Nodes.Where(n => n.IsRunning).Select(n => n.Config.Name).Should().Equal("a", "c");
    }

    [Fact]
    public void Activity_Reports_Started_Stopped_And_RuleMatched()
    {
        var host = CreateHost();
        var activities = new List<NodeActivity>();
        host.Activity += activities.Add;
        host.AddNode(Config("n1"));
        host.StartNode("n1");
        _contexts[0].Report(NodeActivityKind.RuleMatched, "test");
        host.StopNode("n1");
        host.StopAll();

        activities.Select(a => a.Kind).Should().ContainInOrder(NodeActivityKind.Started, NodeActivityKind.RuleMatched, NodeActivityKind.Stopped);
    }

    [Fact]
    public void Context_SendFailed_Becomes_Error_Activity()
    {
        var host = CreateHost();
        var activities = new List<NodeActivity>();
        host.Activity += activities.Add;
        host.AddNode(Config("n1"));
        host.StartNode("n1");

        _contexts[0].SendFailed?.Invoke(new InvalidOperationException("boom"));

        activities.Should().Contain(a => a.Kind == NodeActivityKind.Error && a.Detail.Contains("boom"));
    }

    [Fact]
    public void Unknown_Name_Fails()
    {
        var host = CreateHost();

        host.StartNode("nope").IsSuccess.Should().BeFalse();
        host.StopNode("nope").IsSuccess.Should().BeFalse();
    }

    private static NodeConfig Config(string name, byte sa = 0x56, string? tag = null) => new()
    {
        Name = name, Tag = tag, Identity = new J1939NodeIdentity(sa),
    };

    // CA1816：Dispose 需 SuppressFinalize（替身/宿主无终结器，此处仅为 xunit 生命周期挂钩）。
    public void Dispose() => GC.SuppressFinalize(this);
}
