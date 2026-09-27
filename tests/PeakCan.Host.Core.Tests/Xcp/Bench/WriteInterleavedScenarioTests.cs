using A2lEditor.Core;
using PeakCan.HIL.Core;
using Microsoft.Extensions.Time.Testing;
using PeakCan.Host.Core.Tests.Xcp.TestKit;
using PeakCan.Host.Core.Xcp.Bench;
using PeakCan.Host.Core.Xcp.Calibration;
using PeakCan.Host.Core.Xcp.Protocol;
using Xunit;

namespace PeakCan.Host.Core.Tests.Xcp.Bench;

/// <summary>
/// S7-T4：B 系场景（模拟从机口径）——写前后轮询读流不中断（B-1 采集继续的
/// host 侧事实）+ 写步耗时（B-2）。真机 DAQ 表行为 = 批次运行时人工观察（spec 补记）。
/// </summary>
public sealed class WriteInterleavedScenarioTests
{
    [Fact]
    public async Task Reads_succeed_around_write_and_elapsed_is_recorded()
    {
        var (doc, contracts, slave, writer, _, master) = MakeFixture();
        BitConverter.GetBytes(1.5f).CopyTo(slave.Memory, 0x1500);

        var result = await WriteInterleavedScenario.RunAsync(
            writer, master, contracts.All[0], doc, 9.0f, "b1_b2_demo",
            beats: 2);

        // B-1 host 侧事实：写前 2 拍读成功、写后 2 拍读成功（读流不中断）。
        Assert.Equal(2, result.ReadsBefore);
        Assert.Equal(0, result.ReadsBeforeFailed);
        Assert.Equal(2, result.ReadsAfter);
        Assert.Equal(0, result.ReadsAfterFailed);

        // 写前读到原值，写后（已还原）仍读到原值。
        Assert.All(result.ValuesBefore, v => Assert.Equal(1.5f, (float)v, 3));
        Assert.All(result.ValuesAfter, v => Assert.Equal(1.5f, (float)v, 3));

        // B-2 事实：写步耗时被记录（FakeTimeProvider 推进 → 恒 > 0）。
        Assert.True(result.WriteElapsed > TimeSpan.Zero);

        // RMR 完整走通。
        Assert.True(result.Rmr.WriteVerified);
        Assert.True(result.Rmr.Restored);
    }

    [Fact]
    public async Task Poll_read_failure_is_recorded_not_fatal()
    {
        // 静默从机：读应答超时 → 拍失败归因出站，场景不中断（批次宁全不全）。
        var (doc, contracts, slave, writer, _, master) = MakeFixture();
        slave.Silent = true;

        var result = await WriteInterleavedScenario.RunAsync(
            writer, master, contracts.All[0], doc, 9.0f, "silent_slave",
            beats: 1);

        Assert.Equal(1, result.ReadsBefore);
        Assert.Equal(1, result.ReadsBeforeFailed);
    }

    private static (A2lEditor.Core.Model.A2lDocument Doc, A2lEditor.Core.Layout.ContractSet Contracts,
        MemorySlaveTransport Slave, XcpCalibrationWriter Writer, XcpTransportSpy Spy, XcpMaster Master) MakeFixture()
    {
        var slave = new MemorySlaveTransport();
        var spy = new XcpTransportSpy(slave);
        var master = new XcpMaster(spy, new XcpMasterOptions(
            new CanId(0x18FFF667, FrameFormat.Extended), TimeSpan.FromMilliseconds(100), 0));
        var writer = new XcpCalibrationWriter(master, new XcpCalibrationWriterOptions
        {
            BusyRetryCount = 1,
            BusyRetryDelay = TimeSpan.Zero,
            TimeProvider = new FakeTimeProvider(),
        });
        var (doc, contracts) = BenchDocs.MakeScalarDoc(0x1500);
        return (doc, contracts, slave, writer, spy, master);
    }
}
