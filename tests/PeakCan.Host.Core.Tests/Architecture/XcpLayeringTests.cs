using NetArchTest.Rules;
using PeakCan.Host.Core.Xcp.Scheduling;

namespace PeakCan.Host.Core.Tests.Architecture;

/// <summary>
/// S2-T7 临时守卫（T18 落地后并入正式 NetArchTest 规则）：Core.Xcp 新增公开类型
/// 必须维持 Core 层零驱动/零 UI 依赖——程序集引用面守住 Peak.Can.Basic、
/// System.Windows、Infrastructure、App 四条红线（先例 HILLayeringTests）。
/// 全程序集级 NetArchTest 规则见 Infrastructure.Tests LayeringRulesTests。
/// </summary>
public class XcpLayeringTests
{
    [Fact]
    public void Core_assembly_does_not_reference_drivers_ui_infrastructure_or_app()
    {
        var assembly = typeof(PeakCan.Host.Core.Xcp.Capability.XcpCapabilityReconciler).Assembly;
        var refs = assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain("Peak.Can.Basic", refs);
        Assert.DoesNotContain("System.Windows", refs);
        Assert.DoesNotContain("PeakCan.Host.Infrastructure", refs);
        Assert.DoesNotContain("PeakCan.Host.App", refs);
    }

    [Fact]
    public void Xcp_Scheduling_does_not_touch_package_segment_address_types()
    {
        // S2-T10 (e)：地址换算唯一入口是包侧 XcpAddressMap.TryTranslate（spec [H1]）。
        // Scheduling 命名空间禁止引用 XcpAddressMapping（Logical/Physical/Length——
        // 地址算术载体，全量封禁）；谁引用了它，谁就能重建第二套换算。
        // ValueSegment.Address 是 [H1] 钦定的翻译输入，合法引用，不在此列。
        //
        // T12 review F1 修订（窄幅收窄，评审定案）：EXTENSION≠0 段 fail-loud 守卫
        // （AcquisitionPlanner.GuardAddressExtension）需要读包侧声明元数据
        // XcpSegment.AddressExtension 与段名 A2lMemorySegment.Name——这是只读声明
        // 检查、零地址算术，覆盖判定仍全权走包侧 SegmentsCovering。故
        // XcpSegment / A2lMemorySegment 从"全量封禁"收窄为"仅 AcquisitionPlanner
        // 一个类型可依赖"（其余 Scheduling 类型维持封禁，防扩散）。
        var core = typeof(AcquisitionPlanner).Assembly;

        // 非空守卫：过滤器必须真的选中 Scheduling 类型（防 vacuous pass）。
        Assert.Contains(core.GetTypes(), t => t.Namespace == "PeakCan.Host.Core.Xcp.Scheduling");

        // 宾语侧守卫：封禁串与包侧类型全名逐一比对——包侧重命名时这里先炸，
        // 而不是 HaveDependencyOn 对不存在的名字永远空通过。
        // XcpAddressMapping：地址算术载体，Scheduling 全量封禁（T10 原语义不变）。
        Assert.Equal("A2lEditor.Core.IfData.XcpAddressMapping", typeof(A2lEditor.Core.IfData.XcpAddressMapping).FullName);
        var mappingBan = Types.InAssembly(core)
            .That().ResideInNamespace("PeakCan.Host.Core.Xcp.Scheduling")
            .ShouldNot().HaveDependencyOn("A2lEditor.Core.IfData.XcpAddressMapping")
            .GetResult();
        Assert.True(mappingBan.IsSuccessful,
            "XcpAddressMapping: " + string.Join(", ", mappingBan.FailingTypeNames ?? Array.Empty<string>()));

        // XcpSegment / A2lMemorySegment：声明元数据，仅 AcquisitionPlanner 可依赖（T12 review F1）。
        // DoNotHaveName 排除守卫本体后维持全量封禁（防依赖面扩散）。
        foreach (var metadataType in new[]
                 {
                     "A2lEditor.Core.IfData.XcpSegment",
                     "A2lEditor.Core.Model.A2lMemorySegment",
                 })
        {
            var metadataBan = Types.InAssembly(core)
                .That().ResideInNamespace("PeakCan.Host.Core.Xcp.Scheduling")
                .And().DoNotHaveName("AcquisitionPlanner")
                .ShouldNot().HaveDependencyOn(metadataType)
                .GetResult();
            Assert.True(metadataBan.IsSuccessful,
                $"{metadataType}: {string.Join(", ", metadataBan.FailingTypeNames ?? Array.Empty<string>())}");
        }

        // 非空守卫：F1 守卫本体必须真实依赖声明元数据（守卫被移走/改名时这里先炸，
        // 防 carve-out 退化成 vacuous pass）。
        var plannerUsesMetadata = Types.InAssembly(core)
            .That().HaveName("AcquisitionPlanner")
            .Should().HaveDependencyOn("A2lEditor.Core.IfData.XcpSegment")
            .GetResult();
        Assert.True(plannerUsesMetadata.IsSuccessful,
            "AcquisitionPlanner no longer reads XcpSegment.AddressExtension — " +
            "the T12 review F1 EXTENSION guard is gone or renamed; revisit this carve-out.");
    }
    [Fact]
    public void Xcp_Receive_does_not_touch_package_segment_address_types()
    {
        // S2-T14-review M1：Receive 命名空间兑现 T14 到期承诺——反查只准走
        // planner 自产 PlannedAcquisitionMap（spec [H2]），地址算术载体
        // XcpAddressMapping 全量封禁（与 Scheduling 守卫同一宾语）。
        var core = typeof(PeakCan.Host.Core.Xcp.Receive.XcpReceiveLoop).Assembly;

        // 非空守卫：过滤器必须真的选中 Receive 类型（防 vacuous pass；
        // XcpReceiveLoop 为非空锚）。
        Assert.Contains(core.GetTypes(), t => t.Namespace == "PeakCan.Host.Core.Xcp.Receive");

        // 宾语侧守卫：包侧重命名时这里先炸，而不是 HaveDependencyOn 对
        // 不存在的名字永远空通过（与 Scheduling 守卫同型）。
        Assert.Equal("A2lEditor.Core.IfData.XcpAddressMapping", typeof(A2lEditor.Core.IfData.XcpAddressMapping).FullName);
        var mappingBan = Types.InAssembly(core)
            .That().ResideInNamespace("PeakCan.Host.Core.Xcp.Receive")
            .ShouldNot().HaveDependencyOn("A2lEditor.Core.IfData.XcpAddressMapping")
            .GetResult();
        Assert.True(mappingBan.IsSuccessful,
            "XcpAddressMapping: " + string.Join(", ", mappingBan.FailingTypeNames ?? Array.Empty<string>()));
    }
}

