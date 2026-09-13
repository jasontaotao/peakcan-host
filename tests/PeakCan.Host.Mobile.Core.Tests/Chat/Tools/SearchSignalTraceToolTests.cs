using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.Mobile.Core.Chat.Tools;
using PeakCan.Host.Mobile.Core.Services;
using static PeakCan.Host.Mobile.Core.Tests.Chat.Tools.ChatToolTestData;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.Chat.Tools;

public class SearchSignalTraceToolTests
{
    private const string BaseArgs = """{"signals":[{"message":"EngineData","signal":"EngineSpeed"}]}""";

    private static FakeContext NewContext() => new()
    {
        Dbc = CreateCatalog(),
        TraceId = 1,
        FramesForCanIdPage = new FramePage(
        [
            Frame(0, 1.0, 0x100, [0x90, 0x01, 0, 0, 0, 0, 0, 0]),   // raw 400 → 100 rpm
            Frame(1, 2.0, 0x100, [0x20, 0x03, 0, 0, 0, 0, 0, 0]),   // raw 800 → 200 rpm
            Frame(2, 3.0, 0x100, [0xF4, 0x01, 0, 0, 0, 0, 0, 0]),   // raw 500 → 125 rpm
        ], HasMore: false),
    };

    [Fact]
    public void NoDbc_ReturnsError()
    {
        var tool = new SearchSignalTraceTool(new FakeContext(), NullLogger.Instance);
        var root = Execute(tool, BaseArgs);
        root["error"]!.GetValue<string>().Should().Be("未加载 DBC");
    }

    [Fact]
    public void NoCache_ReturnsError()
    {
        var ctx = new FakeContext { Dbc = CreateCatalog(), TraceId = null };
        var tool = new SearchSignalTraceTool(ctx, NullLogger.Instance);
        var root = Execute(tool, BaseArgs);
        root["error"]!.GetValue<string>().Should().Be("no cache");
    }

    [Fact]
    public void Missing_Signals_ReturnsError()
    {
        var tool = new SearchSignalTraceTool(NewContext(), NullLogger.Instance);
        var root = Execute(tool, "{}");
        root["error"]!.GetValue<string>().Should().Be("missing 'signals'");
    }

    [Fact]
    public void Decodes_PhysicalValues_NotFormattedString()
    {
        // EngineSpeed factor=0.25：物理值必须来自 SignalDecoder（double 原值），
        // 而非 DbcCatalog.Decode 的 0.### 格式化字符串（spec §2.8 锁）。
        var tool = new SearchSignalTraceTool(NewContext(), NullLogger.Instance);
        var root = Execute(tool, """{"signals":[{"message":"enginedata","signal":"ENGINESPEED"}],"t_start":0,"t_end":10}""");

        var sig = (JsonObject)root["signals"]!.AsArray()[0]!;
        sig["sample_count"]!.GetValue<int>().Should().Be(3);
        sig["unit"]!.GetValue<string>().Should().Be("rpm");
        var stats = (JsonObject)sig["stats"]!;
        stats["min"]!.GetValue<double>().Should().Be(100);
        stats["max"]!.GetValue<double>().Should().Be(200);
        stats["mean"]!.GetValue<double>().Should().Be(141.6667);
        stats["first"]!.GetValue<double>().Should().Be(100);
        stats["last"]!.GetValue<double>().Should().Be(125);

        var samples = sig["samples"]!.AsArray();
        samples.Should().HaveCount(3);   // 点数 ≤ max_points → LTTB 原样返回
        ((JsonObject)samples[0]!)["t"]!.GetValue<double>().Should().Be(1.0);
        ((JsonObject)samples[0]!)["t_label"]!.GetValue<string>().Should().Be("1.0000");
        ((JsonObject)samples[0]!)["v"]!.GetValue<double>().Should().Be(100);
        ((JsonObject)sig["t_range"]!)["start"]!.GetValue<double>().Should().Be(1.0);
        ((JsonObject)sig["t_range"]!)["end"]!.GetValue<double>().Should().Be(3.0);

        var info = (JsonObject)root["backend_info"]!;
        info["truncated"]!.GetValue<bool>().Should().BeFalse();
        info["downsample_method"]!.GetValue<string>().Should().Be("LTTB");
        info["raw_frame_count"]!.GetValue<int>().Should().Be(3);
        root["warning"].Should().BeNull();
    }

    [Fact]
    public void MessageNotFound_PerEntryError_GoodEntryContinues()
    {
        var tool = new SearchSignalTraceTool(NewContext(), NullLogger.Instance);
        var root = Execute(tool, """
            {"signals":[{"message":"Nope","signal":"EngineSpeed"},
                        {"message":"EngineData","signal":"EngineSpeed"}],
             "t_start":0,"t_end":10}
            """);

        var signals = root["signals"]!.AsArray();
        signals.Should().HaveCount(2);
        ((JsonObject)signals[0]!)["error"]!.GetValue<string>().Should().Be("message not found");
        ((JsonObject)signals[1]!)["sample_count"]!.GetValue<int>().Should().Be(3);
    }

    [Fact]
    public void SignalNotFound_PerEntryError()
    {
        var tool = new SearchSignalTraceTool(NewContext(), NullLogger.Instance);
        var root = Execute(tool, """{"signals":[{"message":"EngineData","signal":"Nope"}],"t_start":0,"t_end":10}""");
        ((JsonObject)root["signals"]!.AsArray()[0]!)["error"]!.GetValue<string>().Should().Be("signal not found");
    }

    [Fact]
    public void WindowEmpty_ReturnsError()
    {
        var tool = new SearchSignalTraceTool(NewContext(), NullLogger.Instance);
        var root = Execute(tool, """{"signals":[{"message":"EngineData","signal":"EngineSpeed"}],"t_start":5,"t_end":6}""");
        ((JsonObject)root["signals"]!.AsArray()[0]!)["error"]!.GetValue<string>().Should().Be("no frames in window");
    }

    [Fact]
    public void AnchorRef_WithoutAnchor_ReturnsErrorWithHint()
    {
        var tool = new SearchSignalTraceTool(NewContext(), NullLogger.Instance);
        var root = Execute(tool, """{"signals":[{"message":"EngineData","signal":"EngineSpeed"}],"window_ref":"anchor"}""");
        root["error"]!.GetValue<string>().Should().Be("no anchor set");
        root["hint"]!.GetValue<string>().Should().Contain("锚点");
    }

    [Fact]
    public void AnchorRef_ShiftsWindowByAnchor()
    {
        var ctx = NewContext();
        ctx.AnchorTimestamp = 100.0;
        ctx.FramesForCanIdPage = new FramePage(
        [
            Frame(0, 101.0, 0x100, [0x90, 0x01, 0, 0, 0, 0, 0, 0]),
            Frame(1, 103.0, 0x100, [0x20, 0x03, 0, 0, 0, 0, 0, 0]),
        ], HasMore: false);
        var tool = new SearchSignalTraceTool(ctx, NullLogger.Instance);

        var root = Execute(tool, """{"signals":[{"message":"EngineData","signal":"EngineSpeed"}],"t_start":0,"t_end":10,"window_ref":"anchor"}""");

        // 窗口整体平移：absolute 0..10 → anchor 100..110
        ctx.WindowQueries.Should().ContainSingle(q => q.CanId == 0x100 && q.TStart == 100.0 && q.TEnd == 110.0);
        ((JsonObject)root["signals"]!.AsArray()[0]!)["sample_count"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public void Truncation_PropagatesToBackendInfo()
    {
        var ctx = NewContext();
        ctx.FramesForCanIdPage = ctx.FramesForCanIdPage! with { HasMore = true };
        var tool = new SearchSignalTraceTool(ctx, NullLogger.Instance);

        var root = Execute(tool, """{"signals":[{"message":"EngineData","signal":"EngineSpeed"}],"t_start":0,"t_end":10}""");

        ((JsonObject)root["backend_info"]!)["truncated"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void CacheIncomplete_AddsWarning_OnlyWhenWindowExceedsCachedDuration()
    {
        var ctx = NewContext();
        ctx.CacheSummary = new TraceCacheSummary(1, "a.asc", 1, DateTimeOffset.MinValue, 3, 10.0, Complete: false, 0);
        var tool = new SearchSignalTraceTool(ctx, NullLogger.Instance);

        var beyond = Execute(tool, """{"signals":[{"message":"EngineData","signal":"EngineSpeed"}],"t_end":15}""");
        beyond["warning"]!.GetValue<string>().Should().Be("cache incomplete");

        var within = Execute(tool, """{"signals":[{"message":"EngineData","signal":"EngineSpeed"}],"t_end":8}""");
        within["warning"].Should().BeNull();

        ctx.CacheSummary = ctx.CacheSummary! with { Complete = true };
        var complete = Execute(tool, """{"signals":[{"message":"EngineData","signal":"EngineSpeed"}],"t_end":15}""");
        complete["warning"].Should().BeNull();
    }

    [Fact]
    public void MaxPoints_Clamped_And_LttbCountCorrect()
    {
        var ctx = new FakeContext
        {
            Dbc = CreateCatalog(),
            FramesForCanIdPage = new FramePage(
                Enumerable.Range(0, 50)
                    .Select(i => Frame(i, 1.0 + i, 0x100, [(byte)(i + 1), 0, 0, 0, 0, 0, 0, 0]))
                    .ToList(),
                HasMore: false),
        };
        var tool = new SearchSignalTraceTool(ctx, NullLogger.Instance);

        // max_points=20：50 点 LTTB 降采样到 20 点
        var down = Execute(tool, """{"signals":[{"message":"EngineData","signal":"EngineSpeed"}],"t_start":0,"t_end":100,"max_points":20}""");
        ((JsonObject)down["signals"]!.AsArray()[0]!)["sample_count"]!.GetValue<int>().Should().Be(20);

        // max_points=5000 → clamp 到 1000；点数 50 ≤ 1000 → 原样
        var up = Execute(tool, """{"signals":[{"message":"EngineData","signal":"EngineSpeed"}],"t_start":0,"t_end":100,"max_points":5000}""");
        ((JsonObject)up["signals"]!.AsArray()[0]!)["sample_count"]!.GetValue<int>().Should().Be(50);

        // max_points=2 → clamp 到下限 10 → 50 点降采样到 10 点
        var low = Execute(tool, """{"signals":[{"message":"EngineData","signal":"EngineSpeed"}],"t_start":0,"t_end":100,"max_points":2}""");
        ((JsonObject)low["signals"]!.AsArray()[0]!)["sample_count"]!.GetValue<int>().Should().Be(10);
    }

    [Fact]
    public void MoreThanEightSignals_TruncatedToEight()
    {
        var tool = new SearchSignalTraceTool(NewContext(), NullLogger.Instance);
        var entries = string.Join(",", Enumerable.Repeat("""{"message":"EngineData","signal":"EngineSpeed"}""", 10));
        var root = Execute(tool, $$"""{"signals":[{{entries}}],"t_start":0,"t_end":10}""");
        root["signals"]!.AsArray().Should().HaveCount(8);
    }

    [Fact]
    public void MultiplexedSignal_SkipsNonMatchingSelector()
    {
        var ctx = NewContext();
        ctx.Dbc = CreateMuxCatalog();
        ctx.FramesForCanIdPage = new FramePage(
        [
            // selector=1 → RealSignal 命中：raw 250 × 0.1 = 25.0
            Frame(0, 1.0, 0x400, [1, 0xFA, 0x00, 0, 0, 0, 0, 0]),
            // selector=2 → 选择子不匹配 → 跳过
            Frame(1, 2.0, 0x400, [2, 0x64, 0x00, 0, 0, 0, 0, 0]),
        ], HasMore: false);
        var tool = new SearchSignalTraceTool(ctx, NullLogger.Instance);

        var root = Execute(tool, """{"signals":[{"message":"MuxData","signal":"RealSignal"}],"t_start":0,"t_end":10}""");

        var sig = (JsonObject)root["signals"]!.AsArray()[0]!;
        sig["sample_count"]!.GetValue<int>().Should().Be(1);
        ((JsonObject)sig["samples"]!.AsArray()[0]!)["v"]!.GetValue<double>().Should().Be(25.0);
    }

    [Fact]
    public void Bit31_MessageId_Resolution_QueriesBare29BitId()
    {
        // DBC 惯例：扩展帧 BO_ id 带 bit31（PCAN IDE 约定位）；缓存存 29 位裸值
        var ctx = NewContext();
        ctx.Dbc = CreateExtendedCatalog();
        ctx.FramesForCanIdPage = new FramePage(
        [
            Frame(0, 1.0, 0x100, [0x90, 0x01, 0, 0, 0, 0, 0, 0]),
        ], HasMore: false);
        var tool = new SearchSignalTraceTool(ctx, NullLogger.Instance);

        var root = Execute(tool, """{"signals":[{"message":"ExtData","signal":"ExtSpeed"}],"t_start":0,"t_end":10}""");

        ctx.WindowQueries.Should().ContainSingle(q => q.CanId == 0x100);
        ((JsonObject)root["signals"]!.AsArray()[0]!)["sample_count"]!.GetValue<int>().Should().Be(1);
    }

    private static DbcCatalog CreateMuxCatalog() => DbcCatalog.Parse("""
        VERSION ""

        NS_ :

        BS_:

        BU_: ECM

        BO_ 1024 MuxData: 8 ECM
         SG_ MuxSelector M : 0|8@1+ (1,0) [0|255] "" Vector__XXX
         SG_ RealSignal m1 : 8|16@1+ (0.1,0) [0|100] "" Vector__XXX
        """, "mux.dbc").Catalog!;

    private static DbcCatalog CreateExtendedCatalog() => DbcCatalog.Parse("""
        VERSION ""

        NS_ :

        BS_:

        BU_: ECM

        BO_ 2147483904 ExtData: 8 ECM
         SG_ ExtSpeed : 0|16@1+ (0.25,0) [0|16000] "rpm" Vector__XXX
        """, "ext.dbc").Catalog!;
}
