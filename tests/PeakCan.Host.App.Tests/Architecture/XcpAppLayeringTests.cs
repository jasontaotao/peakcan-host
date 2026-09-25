using NetArchTest.Rules;
using PeakCan.Host.App.Services.Xcp;
using PeakCan.Host.App.ViewModels.Xcp;
using PeakCan.Host.Core.Xcp.Protocol;
using PeakCan.Host.Core.Xcp.Receive;
using PeakCan.Host.Core.Xcp.Scheduling;

namespace PeakCan.Host.App.Tests.Architecture;

/// <summary>
/// S3-T10 架构守卫扩展（spec §1 DOWNLOAD 零入口 v0.3 精确化、D7 守卫口径）。
/// <para>
/// 手法选择（repo 内 S2 T18 双先例：反射 GetReferencedAssemblies / NetArchTest）：
/// 选 <b>NetArchTest</b> 为主 + 一条程序集直接引用面检查作补充。理由：
/// 1) 本文件禁令都是"类型级真实使用"封禁（Peak.Can.* 类型、XcpCommandEncoder），
/// NetArchTest 能看见对已引用程序集内类型的实际使用，而 GetReferencedAssemblies
/// 只看得见 csproj 直接引用；2) S2 T18 的成员级 IL 扫描是为"const 内联逃逸
/// NetArchTest"（0xF0 字面量）设计的，本文件禁令不涉及 const 内联形态，不复制该重机器。
/// </para>
/// <para>
/// 计划 (d)「App XCP VM/Services 禁引用 Infrastructure」<b>判断过严，降为注释记录</b>：
/// XcpAcquisitionPanelViewModel 有意使用 Infrastructure.Xcp.XcpCanTransport 包装
/// ICanChannel（D6 定案的采集链路，代码注释明示"App 允许依赖 Infrastructure 先例"），
/// App 组合根本就注册 Infrastructure 服务——该禁令与 S3 spec D6 直接冲突，不强凑。
/// </para>
/// </summary>
public class XcpAppLayeringTests
{
    // P0-1 同款教训：锚点必须取本地 App 程序集类型，不许 typeof 外部包类型。
    private static readonly System.Reflection.Assembly AppAssembly =
        typeof(XcpConnectionPanelViewModel).Assembly;

    [Fact]
    public void App_assembly_does_not_directly_reference_peak_can_driver_packages()
    {
        // 直接引用面检查（补充线）：csproj 里"只引用不用"的脏引用也要拦——
        // Peak.Can.* 是 PCAN 驱动包，App 只准经 Infrastructure 摸硬件。
        var refs = AppAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain(refs, n => n.StartsWith("Peak.Can", StringComparison.Ordinal));
    }

    [Fact]
    public void App_types_do_not_depend_on_peak_can_family()
    {
        // (a) 全部 App 类型禁引用 Peak.Can.*（spec §1：包与内核零 API 变更的
        // App 侧投影——驱动访问唯一入口是 Infrastructure）。"Peak.Can" 前缀族
        // 覆盖 Basic 之外的 Peak.Can.* 包族（S2 T18 同款宾语面）。
        var result = Types.InAssembly(AppAssembly)
            .That().ResideInNamespace("PeakCan.Host.App")
            .ShouldNot().HaveDependencyOn("Peak.Can")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Peak.Can.* leaked into App: " +
            string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>()));
    }

    [Fact]
    public void App_types_do_not_depend_on_xcp_command_encoder()
    {
        // (b) App 层禁引用 XcpCommandEncoder（符号级防回归钉，spec §1 DOWNLOAD
        // 零入口 v0.3 + D7：实测链下沉 Core 后 App 层零 Encoder 引用是事实约束）。
        // 注意只封类型本体——App 合法使用 Protocol 命名空间的其他类型
        //（XcpPid/对账报告等），不能按命名空间封。
        var encoder = "PeakCan.Host.Core.Xcp.Protocol.XcpCommandEncoder";

        // 宾语锚点：Encoder 在 Protocol 命名空间且公开存在，禁令宾语未漂移。
        Assert.Equal("PeakCan.Host.Core.Xcp.Protocol", typeof(XcpPid).Namespace);

        // 机制锚点（非空守卫）：App XCP 采集面板确实依赖 Core.Xcp.Scheduling
        //（XcpAcquisitionSession 是当前事实）——依赖检测机器坏了这里先炸，
        // 下面的封禁不会对空集 vacuous pass。
        var anchor = Types.InAssembly(AppAssembly)
            .That().HaveName(nameof(XcpAcquisitionPanelViewModel))
            .Should().HaveDependencyOn("PeakCan.Host.Core.Xcp.Scheduling")
            .GetResult();
        Assert.True(anchor.IsSuccessful,
            "NetArchTest dependency detection is broken (AcquisitionPanel must read " +
            "XcpAcquisitionSession): " + string.Join(", ", anchor.FailingTypeNames ?? Array.Empty<string>()));

        var ban = Types.InAssembly(AppAssembly)
            .That().ResideInNamespace("PeakCan.Host.App")
            .ShouldNot().HaveDependencyOn(encoder)
            .GetResult();

        Assert.True(ban.IsSuccessful,
            "XcpCommandEncoder leaked into App: " +
            string.Join(", ", ban.FailingTypeNames ?? Array.Empty<string>()));
    }

    [Fact]
    public void XcpCardPanelSink_is_app_layer_and_implements_the_receive_sink_interface()
    {
        // (c) 正向面：sink 实现于 App 层，且只消费 Core.Xcp.Receive 的 sink 接口面。
        var sinkType = typeof(XcpCardPanelSink);

        Assert.Equal("PeakCan.Host.App.Services.Xcp", sinkType.Namespace);
        Assert.Contains(typeof(IXcpAcquisitionSink), sinkType.GetInterfaces());

        var dependsOnReceive = Types.InAssembly(AppAssembly)
            .That().HaveName(nameof(XcpCardPanelSink))
            .Should().HaveDependencyOn("PeakCan.Host.Core.Xcp.Receive")
            .GetResult();
        Assert.True(dependsOnReceive.IsSuccessful,
            "XcpCardPanelSink no longer touches Core.Xcp.Receive: " +
            string.Join(", ", dependsOnReceive.FailingTypeNames ?? Array.Empty<string>()));
    }

    [Fact]
    public void XcpCardPanelSink_does_not_reach_beyond_the_receive_surface()
    {
        // (c) 负向面：sink 依赖面 = Core.Xcp.Receive 接口 + BCL——协议/调度/能力/
        // 传输/驱动/UI 一概不碰。未来 sink 若需要更多 Core 面，先回 spec 改 D3 管线
        // 口径，再改这里的禁令清单（改测试不许绕过评审）。
        foreach (var banned in new[]
                 {
                     "Peak.Can",
                     "PeakCan.Host.Core.Xcp.Protocol",
                     "PeakCan.Host.Core.Xcp.Scheduling",
                     "PeakCan.Host.Core.Xcp.Capability",
                     "PeakCan.Host.Core.Xcp.Abstractions",
                     "PeakCan.Host.Infrastructure",
                     "System.Windows",
                     "A2lEditor",
                 })
        {
            var result = Types.InAssembly(AppAssembly)
                .That().HaveName(nameof(XcpCardPanelSink))
                .ShouldNot().HaveDependencyOn(banned)
                .GetResult();

            Assert.True(result.IsSuccessful,
                $"{banned}: {string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
        }
    }
}
