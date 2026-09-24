using A2lEditor.Core;

namespace PeakCan.Host.Core.Tests.Xcp;

/// <summary>
/// S2-T5 冒烟测试：PeakCan.Host.Core 双 pin 接线 PeakCan.ASAP2（sibling 存在走
/// ProjectReference，否则走本地 feed PackageReference 0.1.1）。测试侧通过传递引用
/// 消费包门面 Asap2PackageApi，证明 Core → A2lEditor.Core 引用链已建立。
/// </summary>
public class Asap2PackageWiringTests
{
    [Fact]
    public void Asap2Package_is_pinned_to_0_1_1()
    {
        // Assembly version is 0.1.1.0 (three-segment pin 0.1.1 + build/revision), prefix match.
        Assert.StartsWith("0.1.1", Asap2PackageApi.PackageVersion, StringComparison.Ordinal);
    }
}
