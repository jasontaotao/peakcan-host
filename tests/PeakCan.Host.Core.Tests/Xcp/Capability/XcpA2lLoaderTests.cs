using A2lEditor.Core;
using A2lEditor.Core.IfData;
using FluentAssertions;
using PeakCan.HIL.Core;
// 本测试项目有同名 namespace PeakCan.Host.Core.Tests.Path，显式指回 System.IO.Path
using PeakCan.Host.Core.Xcp.Capability;
using Xunit;

namespace PeakCan.Host.Core.Tests.Xcp.Capability;

/// <summary>
/// S3-T2 红测：XcpA2lLoader 下沉（spec D4）。
/// <para>
/// 失败形状决定（D4 红测 (c)，定案）：解析失败 / 无 XCP IF_DATA 不抛异常，
/// 返回显式结果 <see cref="XcpA2lLoadResult.Failed"/>（带失败种类 + 消息）——
/// 调用方是 App VM 层（T3 XcpConnectionPanelViewModel），失败要进 ValidationNote
/// 状态区展示，而不是把裸 InvalidDataException 抛穿 UI；
/// 波特率映射失败仍抛 <see cref="NotSupportedException"/>——"按声明值走不猜"
/// 是硬停条件（宁可不采不错采），映射不出就拒绝启动，不是可恢复状态。
/// </para>
/// </summary>
public class XcpA2lLoaderTests
{
    /// <summary>与 CLI 测试共用的真机 A2L 冒烟夹具（S2 已入库，SHA256 基线一致）。</summary>
    private static readonly string RealA2LPath =
        System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", "App_merge_INCA.a2l");

    // ------------------------------------------------------------------
    // (a) 真机样本全链路：parse + CollectCrossChecks + IF_DATA 提取 + Contracts
    // ------------------------------------------------------------------

    [Fact]
    public void Load_real_sample_returns_loaded_with_ifdata_crosschecks_and_contracts()
    {
        var result = XcpA2lLoader.Load(RealA2LPath);

        var loaded = result.Should().BeOfType<XcpA2lLoadResult.Loaded>().Subject;
        loaded.Document.Modules.Should().NotBeEmpty();
        loaded.IfData.Should().NotBeNull("第一个带 XCP IF_DATA 的模块即声明侧来源");
        loaded.IfData.OnCan.Should().ContainSingle();
        loaded.IfData.OnCan[0].Baudrate.Should().Be(500_000, "真机 XCP_ON_CAN.BAUDRATE = 0x07A120");
        loaded.IfData.OnCan[0].MasterCanIdRaw.Should().Be(0x98FFF666);
        loaded.IfData.OnCan[0].SlaveCanIdRaw.Should().Be(0x98FFF667);
        loaded.ValidationNotes.Should().BeEquivalentTo(
            Asap2PackageApi.CollectCrossChecks(loaded.Document),
            "跨块检查与包 API 同源，不得另起炉灶");
        loaded.Contracts.All.Should().HaveCount(2391,
            "A-11 口径：965 MEASUREMENT + 1377 CHARACTERISTIC + 49 AXIS_PTS");
    }

    // ------------------------------------------------------------------
    // (c) 显式失败结果：无 IF_DATA / 解析失败 → Result，不抛裸异常
    // ------------------------------------------------------------------

    [Fact]
    public void Load_a2l_without_xcp_if_data_returns_explicit_failure_not_exception()
    {
        var path = TempA2l("ASAP2_VERSION 1 40\n/begin PROJECT P \"c\"\n" +
            "/begin MODULE M \"m\"\n/end MODULE\n/end PROJECT\n");

        var result = XcpA2lLoader.Load(path);

        var failed = result.Should().BeOfType<XcpA2lLoadResult.Failed>().Subject;
        failed.Kind.Should().Be(XcpA2lLoadFailureKind.NoXcpIfData);
        failed.Message.Should().Contain("no XCP IF_DATA").And.Contain(path);
    }

    [Fact]
    public void Load_unparseable_file_returns_explicit_parse_failed_failure()
    {
        // Fatal 级错误（不支持的 ASAP2 版本）才会让 ParseResult.Value = null——
        // 普通语法噪音只会降级出空文档，那走的是 NoXcpIfData 分支（上面那条测）。
        var path = TempA2l("ASAP2_VERSION 2 70\n/begin PROJECT P \"c\"\n" +
            "/begin MODULE M \"m\"\n/end MODULE\n/end PROJECT\n");

        var result = XcpA2lLoader.Load(path);

        var failed = result.Should().BeOfType<XcpA2lLoadResult.Failed>().Subject;
        failed.Kind.Should().Be(XcpA2lLoadFailureKind.ParseFailed);
        failed.Message.Should().Contain("parse failed").And.Contain(path);
    }

    // ------------------------------------------------------------------
    // (b) 波特率映射：按声明值走不猜，照抄现有 switch，禁止新增预设
    // ------------------------------------------------------------------

    [Fact]
    public void ResolveBaudRate_maps_all_four_declared_classic_presets()
    {
        ResolveBaud(125_000).Should().Be(BaudRate.Can125kbps);
        ResolveBaud(250_000).Should().Be(BaudRate.Can250kbps);
        ResolveBaud(500_000).Should().Be(BaudRate.Can500kbps);
        ResolveBaud(1_000_000).Should().Be(BaudRate.Can1Mbps);
    }

    [Fact]
    public void ResolveBaudRate_without_xcp_on_can_block_throws_not_supported()
    {
        var ifData = BuildIfData(onCan: []);

        var act = () => XcpA2lLoader.ResolveBaudRate(ifData);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*no XCP_ON_CAN block*refusing to guess*");
    }

    [Fact]
    public void ResolveBaudRate_with_unmapped_declared_baudrate_throws_not_supported()
    {
        var ifData = BuildIfData(onCan: [BuildOnCan(baudrate: 200_000)]);

        var act = () => XcpA2lLoader.ResolveBaudRate(ifData);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*200000*no classic CAN preset*refusing to guess*");
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private static BaudRate ResolveBaud(uint baudrate)
        => XcpA2lLoader.ResolveBaudRate(BuildIfData(onCan: [BuildOnCan(baudrate)]));

    private static XcpIfData BuildIfData(XcpOnCan[] onCan) => new(
        Scope: XcpIfDataScope.ModuleLevel,
        ProtocolLayer: null, Daq: null, Pag: null, Pgm: null,
        OnCan: onCan,
        Segments: [],
        Unmodelled: [],
        Missing: [],
        SourceText: string.Empty);

    private static XcpOnCan BuildOnCan(uint baudrate) => new(
        CanVersion: 0x0100, MasterCanIdRaw: 0x98FFF666, SlaveCanIdRaw: 0x98FFF667,
        Baudrate: baudrate, SamplePoint: 0x4B, SampleRate: "SINGLE",
        BtlCycles: null, Sjw: null, SyncEdge: "SINGLE", MaxDlcRequired: false,
        MasterCanIdLine: 0, SlaveCanIdLine: 0,
        Missing: Array.Empty<XcpMissingField>(), SourceText: string.Empty);

    /// <summary>写临时 A2L 文件（Asap2PackageApi.ParseFile 只收路径），用完即删。</summary>
    private static string TempA2l(string content)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"xcp-a2l-loader-{Guid.NewGuid():N}.a2l");
        File.WriteAllText(path, content);
        return path;
    }
}


