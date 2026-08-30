using System.IO;   // UseWPF 的隐式 using 集不含 System.IO
using FluentAssertions;
using NSubstitute;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.J1939;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Nodes;
using PeakCan.Host.App.Tests.Collections;
using PeakCan.Host.App.Tests.Services.Nodes;
using PeakCan.Host.App.ViewModels.Nodes;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Nodes;

/// <summary>
/// Task 18：Nodes tab 宿主 VM 契约钉（后端无关列、活动环形缓冲 1000、启停命令、行摘要解释）。
/// <para>构造器先 <see cref="LeakedApplicationReset.CleanupLeakedApplication"/>（仓库既有约定，
/// 见 SignalViewModelTests / StatsViewModelTests）：Application.Current 为 null 时
/// <c>NodeSetupViewModel.OnActivity</c> 直连同线程追加——这是
/// <c>Activity_Flows_Into_Buffer_With_Cap</c> 的元素序前置条件（leaked STA Application 会让
/// BeginInvoke 进死调度器，追加永不执行，brief 注 121 依赖的直连前提即被破坏）。</para>
/// </summary>
public class NodeSetupViewModelTests
{
    public NodeSetupViewModelTests() => LeakedApplicationReset.CleanupLeakedApplication();

    // 计划原文 CreateHost 带 out List<FakeNodeContext> contexts，但其自身全部调用点均为
    // CreateHost() 无实参（out 不可省略，原稿无法编译），且无任何测试消费 contexts——
    // 最小修订：去掉 out 参数，保留 (config, runtime) → FakeNodeContext 闭包形态。
    private static NodeHostService CreateHost()
        => new((config, runtime) => new FakeNodeContext(runtime));

    private static NodeSetupViewModel CreateVm(NodeHostService host) => new(
        host,
        new NodeConfigLibrary(Path.Combine(Path.GetTempPath(), $"nodes-{Guid.NewGuid():N}"), null),
        new DbcService(Microsoft.Extensions.Logging.Abstractions.NullLogger<DbcService>.Instance),
        Substitute.For<PeakCan.HIL.Core.IFileDialogService>());

    [Fact]
    public void Identity_Display_Is_Backend_Agnostic()
    {
        var host = CreateHost();
        var vm = CreateVm(host);
        host.AddNode(new NodeConfig { Name = "chg", Identity = new J1939NodeIdentity(0x56) });
        vm.RefreshFromHost();

        vm.Nodes.Single().IdentityDisplay.Should().Be("SA 0x56");   // 后端解释收在行 VM（决策 4）
    }

    [Fact]
    public void Message_Row_Summary_Interprets_J1939_Ref()
    {
        var summary = NodeEditorViewModel.DescribeRef(new J1939MessageRef(0x000200, 6, TpMode.Bam, null, 0xFF));
        summary.Should().Be("PGN 0x0200 BAM");
    }

    [Fact]
    public void Activity_Flows_Into_Buffer_With_Cap()
    {
        var host = CreateHost();
        var vm = CreateVm(host);
        host.AddNode(new NodeConfig { Name = "n", Identity = new J1939NodeIdentity(0x11) });

        // Start/Stop 交替产生 1200+ 条活动，验证环形缓冲上限
        for (int i = 0; i < 600; i++) { host.StartNode("n"); host.StopNode("n"); }

        vm.Activities.Count.Should().BeLessThanOrEqualTo(1000);
        vm.Activities.Last().Kind.Should().Be(NodeActivityKind.Stopped);
    }

    [Fact]
    public void StartStop_Command_Toggles_Node()
    {
        var host = CreateHost();
        var vm = CreateVm(host);
        host.AddNode(new NodeConfig { Name = "n", Identity = new J1939NodeIdentity(0x11) });
        vm.RefreshFromHost();
        var row = vm.Nodes.Single();

        row.StartStopCommand.Execute(null);
        row.IsRunning.Should().BeTrue();
        row.StartStopCommand.Execute(null);
        row.IsRunning.Should().BeFalse();
    }

    // 评审修复钉（修订 12 的实时半边）：EditorEnabled 不得只在选择变更时计算——
    // 选中节点经行 ▶ 启停后，编辑门必须随 Started/Stopped 活动实时翻转。
    [Fact]
    public void Editor_Enabled_Gate_Follows_Selected_Row_StartStop()
    {
        var host = CreateHost();
        var vm = CreateVm(host);
        host.AddNode(new NodeConfig { Name = "n", Identity = new J1939NodeIdentity(0x11) });
        vm.RefreshFromHost();
        var row = vm.Nodes.Single();
        vm.SelectedNode.Should().BeSameAs(row);            // RefreshFromHost 自动选中
        vm.Editor.EditorEnabled.Should().BeTrue();

        row.StartStopCommand.Execute(null);                // Started → 编辑门实时关闭
        vm.Editor.EditorEnabled.Should().BeFalse();
        row.StartStopCommand.Execute(null);                // Stopped → 实时恢复
        vm.Editor.EditorEnabled.Should().BeTrue();
    }

    // Task 18 绑定注 4 的钉：SA 冲突拒绝必须走 Activity/Error 路径进入活动日志
    // （StartAll/行 VM Toggle 丢弃 Result——活动日志是唯一可见面，不得静默）。
    [Fact]
    public void Sa_Conflict_Start_Failure_Is_Surfaced_As_Error_Activity()
    {
        var host = CreateHost();
        var vm = CreateVm(host);
        host.AddNode(new NodeConfig { Name = "a", Identity = new J1939NodeIdentity(0x11) });
        host.AddNode(new NodeConfig { Name = "b", Identity = new J1939NodeIdentity(0x11) });
        host.StartNode("a");

        host.StartNode("b").IsSuccess.Should().BeFalse();

        vm.Activities.Should().Contain(a =>
            a.Kind == NodeActivityKind.Error && a.NodeName == "b" && a.Detail.Contains("0x11"));
    }
}
