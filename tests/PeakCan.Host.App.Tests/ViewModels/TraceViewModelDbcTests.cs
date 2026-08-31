using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// 2026-08-31 P2：<see cref="TraceViewModel.BindDbc"/> 注入与 DBC 名解析降级
/// （spec §5.10 / §10.2）。DBC 消息名经 <c>BindDbc</c> 安装的 <see cref="DbcService"/>
/// 解析；未绑定/未加载时降级（报"DBC 未加载"），其余功能不受影响。
/// </summary>
public class TraceViewModelDbcTests
{
    private static DbcDocument Doc(params Message[] msgs) => new(
        "v1",
        Array.Empty<Node>(),
        msgs,
        msgs.ToDictionary(m => m.Id, m => m),
        new Dictionary<string, ValueTable>());

    private static Message ExtendedMsg(uint id, string name)
        => new(id | 0x8000_0000u, name, 8, "ECU", Array.Empty<Signal>(), false, null);

    private static CanFrame Frame(uint id = 0x100, FrameFormat format = FrameFormat.Standard)
        => new(new CanId(id, format), new byte[1], FrameFlags.None,
            ChannelId.None, Timestamp.FromMicroseconds(1_000_000UL));

    // —— BindDbc 未绑降级：DBC 消息名解析报"DBC 未加载" ——

    [Fact]
    public void DbcName_Without_Bind_Reports_Not_Loaded()
    {
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame() });

        vm.DbcMessageName = "EEC1";

        vm.FilterErrorText.Should().NotBeNullOrEmpty();
        vm.FilterErrorText.Should().Contain("DBC 未加载");
        // 未绑降级：除 DBC 名符号解析外其余过滤不受影响（沿用 Empty spec = 全显）。
        vm.EntriesView.Count.Should().Be(1);
    }

    // —— BindDbc 绑定 + 罐装文档后，DBC 消息名解析成功并入 allow-list ——

    [Fact]
    public void DbcName_After_Bind_And_Load_Resolves()
    {
        var vm = new TraceViewModel();
        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        dbc.SetCurrentForTests(Doc(ExtendedMsg(0x18EAFF00, "EEC1")));
        vm.BindDbc(dbc);

        vm.AppendBatchCore(new[]
        {
            Frame(0x18EAFF00, FrameFormat.Extended),
            Frame(0x200),
        });

        vm.DbcMessageName = "eec1"; // case-insensitive
        vm.FilterErrorText.Should().BeNull();
        vm.EntriesView.Count.Should().Be(1);
        // DBC 名掩码掉 IDE 位 → 与裸 Raw 匹配。
        vm.EntriesView.GetItemAt(0).Should().Be(vm.Entries[0]);
    }

    // —— DbcMessageNames 投影 ——

    [Fact]
    public void Bind_Populates_DbcMessageNames_Projection()
    {
        var vm = new TraceViewModel();
        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        dbc.SetCurrentForTests(Doc(ExtendedMsg(0x18EAFF00, "EEC1"), ExtendedMsg(0x18FEF100, "CCVS")));
        vm.BindDbc(dbc);

        vm.DbcMessageNames.Should().Contain("EEC1");
        vm.DbcMessageNames.Should().Contain("CCVS");
    }

    // —— DbcLoaded 处理器：无 Application（MTA 测试上下文）直接处理不崩 ——

    [Fact]
    public void DbcLoaded_Handler_Works_In_Mta_Without_Application()
    {
        var vm = new TraceViewModel();
        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        vm.BindDbc(dbc);

        // 初始无 DBC → 空投影。
        vm.DbcMessageNames.Should().BeEmpty();

        // 模拟 DbcService.LoadAsync 成功流程：先设 Current 再触发 DbcLoaded。
        // 反射触发（MTA 无 Application → OnDbcLoaded 走直接分支，不崩且更新投影）。
        var lateDoc = Doc(ExtendedMsg(0x18EAFF00, "EEC1"));
        dbc.SetCurrentForTests(lateDoc);
        dbc.GetType().GetEvent(nameof(DbcService.DbcLoaded))!
            .RaiseMethod(dbc, lateDoc);

        // 不崩，且投影更新（DBC 名字段为空时重解析为 no-op，不影响其他）。
        vm.DbcMessageNames.Should().Contain("EEC1");
    }
}
