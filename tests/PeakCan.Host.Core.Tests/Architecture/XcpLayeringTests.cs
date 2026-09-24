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
        // Scheduling 命名空间（AcquisitionPlanner / PlannedAcquisitionMap）禁止直接
        // 引用包侧三类 segment 地址载体——XcpAddressMapping（Logical/Physical/Length）、
        // XcpSegment（内嵌份容器）、A2lMemorySegment（OFFSET 基址）。谁引用了谁，
        // 谁就能重建第二套换算。ValueSegment.Address 是 [H1] 钦定的翻译输入，
        // 合法引用，不在此列。Receive 侧守卫随 T14 建命名空间时补入。
        var core = typeof(AcquisitionPlanner).Assembly;

        // 非空守卫：过滤器必须真的选中 Scheduling 类型（防 vacuous pass）。
        Assert.Contains(core.GetTypes(), t => t.Namespace == "PeakCan.Host.Core.Xcp.Scheduling");

        foreach (var banned in new[]
                 {
                     "A2lEditor.Core.IfData.XcpAddressMapping",
                     "A2lEditor.Core.IfData.XcpSegment",
                     "A2lEditor.Core.Model.A2lMemorySegment",
                 })
        {
            var result = Types.InAssembly(core)
                .That().ResideInNamespace("PeakCan.Host.Core.Xcp.Scheduling")
                .ShouldNot().HaveDependencyOn(banned)
                .GetResult();
            Assert.True(result.IsSuccessful,
                $"{banned}: {string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
        }
    }
}