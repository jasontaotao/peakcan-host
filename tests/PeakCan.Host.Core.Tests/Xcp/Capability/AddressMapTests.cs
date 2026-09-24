using A2lEditor.Core;
using A2lEditor.Core.IfData;
using A2lEditor.Core.Layout;
using A2lEditor.Core.Model;
using PeakCan.Host.Core.Xcp.Scheduling;

namespace PeakCan.Host.Core.Tests.Xcp.Capability;

/// <summary>
/// S2-T10：XcpAddressMap.TryTranslate 段映射唯一入口（spec §3 Segment 映射 / [H1]）。
/// 只钉住 PeakCan.ASAP2 0.1.1 包侧行为，不在 host 重建实现：
/// <list type="bullet">
/// <item>(a) 逻辑地址经 MEMORY_SEGMENT 内嵌份 SEGMENT 的 ADDRESS_MAPPING → 物理地址；</item>
/// <item>(b) 段外地址 → false（返回值形态，不抛异常——按包 0.1.1 实际行为钉住）；</item>
/// <item>(c) 只认内嵌份 SEGMENT——模块级 IF_DATA XCP 的 SEGMENT 仅声明层、
/// MEMORY_SEGMENT 自身 OFFSET 也不参与，均不进入翻译；</item>
/// <item>(d) [H1] SourceOffset 是对象数据 blob 内偏移（单段恒 0），不是 ECU 地址——
/// 翻译输入是 ValueSegment.Address，TryTranslate 的 API 形状上没有 SourceOffset 入参；</item>
/// <item>(e) planner 不自建第二套换算的架构守卫在 Architecture/XcpLayeringTests（NetArchTest）。</item>
/// </list>
/// 最小 A2L 模型构造与 AcquisitionPlannerTests 同构；真机样本走 TestData/App_merge_INCA.a2l。
/// </summary>
public class AddressMapTests
{
    // ------------------------------------------------------------------
    // (a) 内嵌份 SEGMENT：逻辑 → 物理换算（最小夹具 + 真机样本双覆盖）
    // ------------------------------------------------------------------

    [Fact]
    public void Embedded_Segment_Mapping_Translates_Logical_To_Physical()
    {
        // 映射 logical 0x1000..0x1FFF → physical 0x9000..0x9FFF（非恒等，
        // 排除"恒等映射巧合"造成的假阳性）。首字节 / 中段 / 末字节（半开区间
        // [logical, logical+length) 的最后一个合法地址）逐一核对。
        var doc = Doc([SegmentWithMapping(0x1000, 0x9000, 0x1000)]);

        Assert.True(XcpAddressMap.TryTranslate(doc, 0x1000, out var basePhys));
        Assert.Equal(0x9000UL, basePhys);
        Assert.True(XcpAddressMap.TryTranslate(doc, 0x1234, out var midPhys));
        Assert.Equal(0x9234UL, midPhys);
        Assert.True(XcpAddressMap.TryTranslate(doc, 0x1FFF, out var lastPhys));
        Assert.Equal(0x9FFFUL, lastPhys);
    }

    [Fact]
    public void RealSample_Calrom_Mapping_Translates()
    {
        // 真机样本 CALROM：logical==physical==0x20015F00、长度 0x8000（恒等映射，
        // 与 a2l-editor 侧 XcpSegmentMappingTests 同口径，数据出处 App_merge_INCA.a2l）。
        var doc = ParseRealSample();
        var calrom = doc.Modules.SelectMany(m => m.MemorySegmentList)
            .Single(s => s.Name == "CALROM");
        var map = calrom.IfDataXcp!.Segments.Single().Mappings.Single();
        Assert.Equal((0x20015F00UL, 0x20015F00UL, 0x8000UL),
            (map.LogicalAddress, map.PhysicalAddress, map.Length));

        Assert.True(XcpAddressMap.TryTranslate(doc, 0x20019CB2, out var phys));
        Assert.Equal(0x20019CB2UL, phys);
    }

    // ------------------------------------------------------------------
    // (b) 段外地址 → false（覆盖不到要说不知道，不静默直传，§4.8 同口径）
    // ------------------------------------------------------------------

    [Fact]
    public void Address_Outside_All_Segments_Returns_False()
    {
        var doc = Doc([SegmentWithMapping(0x1000, 0x9000, 0x1000)]);

        // 恰在段尾之外（半开区间上界）与远端地址都返回 false。
        Assert.False(XcpAddressMap.TryTranslate(doc, 0x2000, out _));
        Assert.False(XcpAddressMap.TryTranslate(doc, 0x7FFF_FFFF, out _));

        // 空文档（无任何内嵌份 SEGMENT）同样 false。
        Assert.False(XcpAddressMap.TryTranslate(Doc([]), 0x1000, out _));

        // 真机样本：CALROM（0x20015F00..0x2001DEFF）之外。
        Assert.False(XcpAddressMap.TryTranslate(ParseRealSample(), 0x7FFF_FFFF, out _));
    }

    // ------------------------------------------------------------------
    // (c) 只认 MEMORY_SEGMENT 内嵌份 SEGMENT
    // ------------------------------------------------------------------

    [Fact]
    public void Module_Level_Segment_Declaration_And_Segment_Offset_Do_Not_Participate()
    {
        // 模块级 IF_DATA XCP 里声明 SEGMENT/ADDRESS_MAPPING（0x5000→0x6000）；
        // 无内嵌份的 MEMORY_SEGMENT 自身声明 OFFSET 0x2000。两者都只是声明层：
        // 0x5000（模块级映射覆盖）与 0x2000（OFFSET 本身）都必须 false。
        // 同文档再放一个真内嵌份（0x1000→0x9000）照常翻译——排除
        // "映射根本没被加载" 的假阳性。
        var moduleIfData = new XcpIfData(XcpIfDataScope.ModuleLevel, null, null, null, null,
            [],
            [new XcpSegment(0, 1, 0, 0, 0,
                [new XcpAddressMapping(0x5000, 0x6000, 0x1000, [], "")], 1, [], "")],
            [], [], "");
        var plainSegment = new A2lMemorySegment("RAM", "ram", "DATA", "RAM", "INTERN",
            0x2000, 0x1000, [], new LineRange(1, 1));

        var doc = Doc([plainSegment, SegmentWithMapping(0x1000, 0x9000, 0x1000)], moduleIfData);

        Assert.False(XcpAddressMap.TryTranslate(doc, 0x5000, out _));
        Assert.False(XcpAddressMap.TryTranslate(doc, 0x2000, out _));
        Assert.True(XcpAddressMap.TryTranslate(doc, 0x1000, out var embeddedPhys));
        Assert.Equal(0x9000UL, embeddedPhys);
    }

    // ------------------------------------------------------------------
    // (d) [H1] SourceOffset 不参与翻译
    // ------------------------------------------------------------------

    [Fact]
    public void SourceOffset_Does_Not_Participate_In_Translation()
    {
        // 合同侧：真机样本全部合同段 SourceOffset == 0（单段恒 0——它是对象数据
        // blob 内偏移，不是 ECU 地址），Address 才是进翻译的逻辑地址。
        // 时代钉：SourceOffset 由包侧 ValueContractFactory 生成，包侧拆条目落地后
        // 会非零——届时此断言失效属预期（[H1] 语义由下方 API 形状断言独立把守），
        // 按包侧演进同步更新，不是翻译语义回归。
        var contracts = Asap2PackageApi.Contracts(ParseRealSample());
        var segments = contracts.All.SelectMany(c => c.Segments).ToList();
        Assert.NotEmpty(segments);
        Assert.All(segments, s => Assert.Equal(0, s.SourceOffset));

        // API 形状：TryTranslate 唯一重载只收 (A2lDocument, ulong, out ulong)——
        // 翻译入口在结构上收不到 SourceOffset，不可能参与任何地址算术。
        var method = Assert.Single(typeof(XcpAddressMap).GetMethods(), m => m.Name == "TryTranslate");
        var parameters = method.GetParameters();
        Assert.Equal(3, parameters.Length);
        Assert.Equal(typeof(A2lDocument), parameters[0].ParameterType);
        Assert.Equal(typeof(ulong), parameters[1].ParameterType);
        Assert.Equal(typeof(ulong).MakeByRefType(), parameters[2].ParameterType);
        Assert.True(parameters[2].IsOut);
        Assert.DoesNotContain(parameters,
            p => p.Name!.Contains("offset", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------
    // 测试脚手架（构造包侧 A2L 模型，与 AcquisitionPlannerTests 同构）
    // ------------------------------------------------------------------

    private static A2lDocument ParseRealSample() =>
        Asap2PackageApi.ParseFile(System.IO.Path.Combine(
            AppContext.BaseDirectory, "TestData", "App_merge_INCA.a2l")).Value!;

    private static A2lDocument Doc(A2lMemorySegment[] segments, XcpIfData? moduleIfData = null)
    {
        var module = new A2lModule("M", "m",
            Array.Empty<A2lMeasurement>(), Array.Empty<A2lCharacteristic>(),
            Array.Empty<A2lAxisPts>(), Array.Empty<A2lCompuMethod>(),
            Array.Empty<A2lRecordLayout>(), Array.Empty<A2lGroup>(), null,
            Array.Empty<A2lAxisDescr>(), Array.Empty<A2lUserRights>(),
            Array.Empty<A2lVersionInfo>(), Array.Empty<A2lAxisPtsX>(),
            new LineRange(1, 1), MemorySegments: segments, IfDataXcp: moduleIfData);
        return new A2lDocument(A2lVersion.V1_6x, "P", "", "",
            new A2lModCommon("", A2lByteOrder.MSB_LAST, null, null, null, new LineRange(1, 1)),
            new[] { module }, "", 1);
    }

    private static A2lMemorySegment SegmentWithMapping(ulong logical, ulong physical, ulong length)
    {
        var ifData = new XcpIfData(XcpIfDataScope.MemorySegmentLevel, null, null, null, null,
            [],
            [new XcpSegment(0, 1, 0, 0, 0,
                [new XcpAddressMapping(logical, physical, length, [], "")], 1, [], "")],
            [], [], "");
        return new A2lMemorySegment("CAL", "cal", "DATA", "FLASH", "INTERN",
            logical, length, [], new LineRange(1, 1), IfDataXcp: ifData);
    }
}
