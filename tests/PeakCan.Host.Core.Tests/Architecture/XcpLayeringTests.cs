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
}
