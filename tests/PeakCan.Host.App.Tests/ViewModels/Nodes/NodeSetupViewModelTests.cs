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
    // 删除节点命令（审计补点 1）：停止节点从 host + 列表移除并刷新；运行中节点拒绝且
    // 经 Activity Error 呈现（Task 18 绑定注 4 同款契约——命令丢弃 Result，活动日志是
    // 唯一可见面，不得静默）。SelectedNode 为 null（列表空）时 no-op。
    [Fact]
    public void Delete_Selected_Node_Removes_Row()
    {
        var host = CreateHost();
        var vm = CreateVm(host);
        host.AddNode(new NodeConfig { Name = "n", Identity = new J1939NodeIdentity(0x11) });
        vm.RefreshFromHost();

        vm.DeleteSelectedCommand.Execute(null);

        vm.Nodes.Should().BeEmpty();
        host.Nodes.Should().BeEmpty();
    }

    // review MEDIUM 钉：成功删除后选中必须显式收敛（空列表 → null；非空 → 首行），
    // 不得悬垂指向已删行实例（该实例 ConcurrentDictionary 已移除，后续 Save/Delete 会幽灵操作）。
    [Fact]
    public void Delete_Success_Converges_Selection_To_Null_When_List_Empty()
    {
        var host = CreateHost();
        var vm = CreateVm(host);
        host.AddNode(new NodeConfig { Name = "n", Identity = new J1939NodeIdentity(0x11) });
        vm.RefreshFromHost();

        vm.DeleteSelectedCommand.Execute(null);

        vm.SelectedNode.Should().BeNull();
        vm.Editor.EditorEnabled.Should().BeFalse();   // 编辑区随选择收敛清空
    }

    [Fact]
    public void Delete_Success_Converges_Selection_To_First_Remaining()
    {
        var host = CreateHost();
        var vm = CreateVm(host);
        host.AddNode(new NodeConfig { Name = "a", Identity = new J1939NodeIdentity(0x11) });
        host.AddNode(new NodeConfig { Name = "b", Identity = new J1939NodeIdentity(0x12) });
        vm.RefreshFromHost();   // 自动选中首行 "a"
        vm.SelectedNode!.Name.Should().Be("a");

        vm.DeleteSelectedCommand.Execute(null);   // 删除 "a"

        vm.Nodes.Single().Name.Should().Be("b");
        vm.SelectedNode.Should().BeSameAs(vm.Nodes.Single());   // 收敛到剩余首行
        vm.Editor.EditorEnabled.Should().BeTrue();
    }

    [Fact]
    public void Delete_Running_Node_Is_Rejected_With_Error_Activity()
    {
        var host = CreateHost();
        var vm = CreateVm(host);
        host.AddNode(new NodeConfig { Name = "n", Identity = new J1939NodeIdentity(0x11) });
        vm.RefreshFromHost();
        var row = vm.Nodes.Single();
        row.StartStopCommand.Execute(null);   // 启动 → 运行中
        vm.SelectedNode.Should().BeSameAs(row);

        vm.DeleteSelectedCommand.Execute(null);

        vm.Nodes.Should().ContainSingle();    // 仍在列表
        vm.Activities.Should().Contain(a =>
            a.Kind == NodeActivityKind.Error && a.NodeName == "n" && a.Detail.Contains("正在运行"));
    }

    [Fact]
    public void Delete_With_Empty_Selection_Is_Noop()
    {
        var host = CreateHost();
        var vm = CreateVm(host);
        host.AddNode(new NodeConfig { Name = "n", Identity = new J1939NodeIdentity(0x11) });
        vm.RefreshFromHost();
        vm.SelectedNode = null;

        vm.DeleteSelectedCommand.Execute(null);

        vm.Nodes.Should().ContainSingle();    // 无选中 → 不删任何东西
        vm.Activities.Should().BeEmpty();     // 也不产生错误活动
    }

    // 编辑提交（ConfigApplied 事件）→ 节点列表刷新 + 重选新名行（引用陷阱收敛钉：
    // 行 VM/SelectedNode 持旧 record 引用，必须经 OnConfigApplied 重建）。
    [Fact]
    public void ApplyConfig_Rename_Refreshes_Node_List_And_Selection()
    {
        var host = CreateHost();
        var vm = CreateVm(host);
        host.AddNode(new NodeConfig { Name = "n", Identity = new J1939NodeIdentity(0x11) });
        vm.RefreshFromHost();

        vm.Editor.NodeName = "renamed";
        vm.Editor.ApplyConfigCommand.Execute(null);

        vm.Nodes.Single().Name.Should().Be("renamed");
        vm.SelectedNode.Should().BeSameAs(vm.Nodes.Single());   // 重选新名行（旧行已失效）
        vm.Editor.Config.Should().BeSameAs(vm.Nodes.Single().Config);   // 编辑区随重选指向新配置
    }

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
