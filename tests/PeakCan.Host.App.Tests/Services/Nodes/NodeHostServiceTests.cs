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
    public void Activity_Subscriber_Exception_Is_Isolated()
    {
        // 评审修复回归钉：Raise 逐订阅者隔离（J1939TpLayer.RaiseMessageReceived 同款）——
        // 坏订阅者不得让 Start/Stop 抛出、不得殃及同批其他订阅者、不得杀死 consumer 分发。
        var host = CreateHost();
        var received = new List<NodeActivity>();
        host.Activity += _ => throw new InvalidOperationException("bad handler");   // 先订阅坏处理器
        host.Activity += received.Add;                                              // 好处理器仍须收到

        host.AddNode(Config("n1"));
        host.StartNode("n1").IsSuccess.Should().BeTrue();
        received.Should().Contain(a => a.Kind == NodeActivityKind.Started);

        _contexts[0].Report(NodeActivityKind.RuleMatched, "test");   // 同一坏订阅者重复抛出
        received.Should().Contain(a => a.Kind == NodeActivityKind.RuleMatched);

        host.StopNode("n1");
        received.Should().Contain(a => a.Kind == NodeActivityKind.Stopped);
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

    // 节点配置编辑器（NodeEditorViewModel 升级）的 host 契约钉：
    // UpdateNode 替换配置并重建行为；运行中拒绝（RemoveNode 同款 "请先停止"）；改名需唯一。
    [Fact]
    public void UpdateNode_Success_Replaces_Config_And_Rebuilds_Behavior()
    {
        var factoryCalls = 0;
        var host = new NodeHostService(
            (config, runtime) => new FakeNodeContext(runtime),
            behaviorFactory: _ => { factoryCalls++; return _behavior; });
        host.AddNode(Config("n"));

        var updated = new NodeConfig
        {
            Name = "n2",
            Identity = new J1939NodeIdentity(0x22),
            Messages = [new NodeMessage(new J1939MessageRef(0x001200, 6, TpMode.Single, null, 0x22), 50, new FixedHexSource("AA BB"))],
            Rules = [new ResponseRule(new J1939MessageRef(0x000900, 6, null, null), null, new StopMessageAction(new J1939MessageRef(0x001200, 6, TpMode.Single, null, 0x22)), 0)],
        };
        host.UpdateNode("n", updated).IsSuccess.Should().BeTrue();

        factoryCalls.Should().Be(2);   // AddNode 1 次 + UpdateNode 重建 1 次（快照新配置）
        var node = host.Nodes.Single();
        node.Config.Name.Should().Be("n2");          // 改名生效
        node.Config.Identity.Should().Be(new J1939NodeIdentity(0x22));
        node.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void UpdateNode_While_Running_Fails()
    {
        var host = CreateHost();
        host.AddNode(Config("n"));
        host.StartNode("n");

        var r = host.UpdateNode("n", Config("n2", sa: 0x22));

        r.IsSuccess.Should().BeFalse();
        r.Error!.Message.Should().Contain("请先停止");   // RemoveNode 同款语义
        host.Nodes.Single().Config.Name.Should().Be("n");   // 运行中不生效
    }

    [Fact]
    public void UpdateNode_Rename_Collision_Fails()
    {
        var host = CreateHost();
        host.AddNode(Config("a"));
        host.AddNode(Config("b"));

        var r = host.UpdateNode("a", Config("b"));

        r.IsSuccess.Should().BeFalse();
        r.Error!.Message.Should().Contain("已存在");
        host.Nodes.Select(n => n.Config.Name).Should().Equal("a", "b");   // 无变更
    }

    [Fact]
    public void UpdateNode_Unknown_Name_Fails()
    {
        var host = CreateHost();

        var r = host.UpdateNode("nope", Config("n"));

        r.IsSuccess.Should().BeFalse();
        r.Error!.Code.Should().Be(ErrorCode.NotFound);
    }

    // 生效语义钉：停止节点更新（含改名）后以新名登记，可再次启动；Runtime（跨启停信号表）保留。
    [Fact]
    public void UpdateNode_After_Stop_Applies_And_Preserves_Runtime()
    {
        var host = CreateHost();
        host.AddNode(Config("n"));
        host.StartNode("n");
        host.Nodes.Single().Runtime.SetSignalValue("CCS", "voltage", 42.0);
        host.StopNode("n");

        host.UpdateNode("n", Config("n2", sa: 0x22)).IsSuccess.Should().BeTrue();

        host.Nodes.Single().Config.Name.Should().Be("n2");
        host.Nodes.Single().Runtime.TryGetSignalValue("CCS", "voltage", out var v).Should().BeTrue();
        v.Should().Be(42.0);                       // 节点实例未换，Runtime 不丢（Remove+Add 会丢）
        host.StartNode("n2").IsSuccess.Should().BeTrue();
        host.StopNode("n2").IsSuccess.Should().BeTrue();
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
