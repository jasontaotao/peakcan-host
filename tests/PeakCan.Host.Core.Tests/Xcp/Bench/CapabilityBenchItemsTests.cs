using PeakCan.Host.Core.Tests.Xcp.TestKit;
using PeakCan.Host.Core.Xcp.Bench;
using PeakCan.Host.Core.Xcp.Capability;
using PeakCan.Host.Core.Xcp.Protocol;
using Xunit;

namespace PeakCan.Host.Core.Tests.Xcp.Bench;

/// <summary>S7-T2：能力探针结果 → A 系验证矩阵项（A-1/2/3/4/5/10；A-3 指向 B-1 并发场景计时）。</summary>
public class CapabilityBenchItemsTests
{
    private const uint MasterCanIdRaw = 0x18FFF667;
    private const uint SlaveCanIdRaw = 0x18FFF666;

    private static async Task<XcpCapabilityProbeResult> ProbeAgainstAsync()
    {
        var slave = new XcpVirtualSlave();
        var spy = new XcpTransportSpy(slave);
        await using var _ = spy;
        using var master = new XcpMaster(spy, new XcpMasterOptions(XcpVirtualSlave.DefaultMasterCanId));
        return await XcpCapabilityProber.ProbeAsync(master, MasterCanIdRaw, SlaveCanIdRaw);
    }

    [Fact]
    public async Task Mapping_covers_all_a_items_with_measured_facts()
    {
        var probe = await ProbeAgainstAsync();
        var items = CapabilityBenchItems.FromProbeResult(probe, MasterCanIdRaw, SlaveCanIdRaw);

        var byId = items.ToDictionary(i => i.ItemId);

        // A-1 能力：资源位图 + 实测命令数。
        Assert.Equal(BenchItemStatus.Measured, byId["A-1"].Status);
        Assert.True(byId["A-1"].Facts!.ContainsKey("resourceBitmap"));
        Assert.True(byId["A-1"].Facts!.ContainsKey("measuredCommandCount"));

        // A-2 事件节拍。
        Assert.Equal(BenchItemStatus.Measured, byId["A-2"].Status);
        Assert.True(byId["A-2"].Facts!.ContainsKey("eventChannelCount"));

        // A-3 抖动：不在能力链——标注指向 B-1 并发场景（宁可不采不冒充）。
        Assert.Equal(BenchItemStatus.NotCollected, byId["A-3"].Status);
        Assert.Contains("B-1", byId["A-3"].Summary);

        // A-4 CAN 号：双 ID 记录 + 29 位合规判定。
        Assert.Equal(BenchItemStatus.Measured, byId["A-4"].Status);
        Assert.Equal("0x18FFF667", byId["A-4"].Facts!["masterCanId"]);
        Assert.Equal("0x18FFF666", byId["A-4"].Facts!["slaveCanId"]);
        Assert.Equal("true", byId["A-4"].Facts!["canId29BitCompliant"]);

        // A-5 ODT 打包上限。
        Assert.Equal(BenchItemStatus.Measured, byId["A-5"].Status);
        Assert.True(byId["A-5"].Facts!.ContainsKey("maxOdt"));

        // A-10 块模式位图（GET_COMM_MODE_INFO 实测为准——S5 附录 C-2 钉）。
        Assert.Equal(BenchItemStatus.Measured, byId["A-10"].Status);
        Assert.True(byId["A-10"].Facts!.ContainsKey("masterBlockMode"));

        // A-11 由 T5 静态扫描交付，不在能力映射内。
        Assert.DoesNotContain("A-11", byId.Keys);
    }

    [Fact]
    public async Task Mapping_reflects_probe_can_ids_not_globals()
    {
        // 纯函数口径：A-4 事实来自传入参数，不依赖装配侧全局（真机 ID 换对不影响映射正确性）。
        var probe = await ProbeAgainstAsync();
        var items = CapabilityBenchItems.FromProbeResult(probe, 0x1FFFFFFF, 0x000001);
        var a4 = items.Single(i => i.ItemId == "A-4");

        Assert.Equal("0x1FFFFFFF", a4.Facts!["masterCanId"]);
        Assert.Equal("0x1", a4.Facts!["slaveCanId"]);
        Assert.Equal("true", a4.Facts!["canId29BitCompliant"]);
    }
}
