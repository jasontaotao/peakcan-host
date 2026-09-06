using Xunit;

namespace PeakCan.Host.Core.Tests.Architecture;

public class HILLayeringTests
{
    [Fact]
    public void HIL_assembly_does_not_reference_Infrastructure_or_App()
    {
        // P0-1 修复（2026-09-06）：用本地 Core 程序集类型定位守卫目标。
        // 旧写法 typeof(PeakCan.HIL.Core.HIL.TestCase).Assembly 定位到外部
        // PeakCan.HIL.Core.dll（TestCase 在 sibling 包），守卫形同虚设。
        var assembly = typeof(PeakCan.Host.Core.HIL.TestSuiteEngine).Assembly;
        var refs = assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain("PeakCan.Host.Infrastructure", refs);
        Assert.DoesNotContain("PeakCan.Host.App", refs);
    }
}
