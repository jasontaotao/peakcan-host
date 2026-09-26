using A2lEditor.Core;
using A2lEditor.Core.IfData;
using A2lEditor.Core.Layout;
using Microsoft.Extensions.Time.Testing;
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Tests.Xcp.TestKit;
using PeakCan.Host.Core.Xcp.Calibration;
using PeakCan.Host.Core.Xcp.Protocol;
using Xunit;

namespace PeakCan.Host.Core.Tests.Xcp.Calibration;

/// <summary>
/// S5-T3：CalibrationReconciler（spec D3/D4）——差异对账（按原始字节比对）→
/// 批量下发只写差异项 → 结果单；重跑幂等；单项失败不中断批量；拒绝面零流量。
/// </summary>
public sealed class CalibrationReconcilerTests
{
    private static readonly Lazy<(ContractSet Contracts, List<(string Name, uint Addr, ValueContract C)> Cals)> Fixture =
        new(() =>
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", "App_merge_INCA.a2l");
            var parsed = Asap2PackageApi.ParseFile(path);
            Assert.NotNull(parsed.Value);
            var contracts = Asap2PackageApi.Contracts(parsed.Value!);
            var cals = new List<(string, uint, ValueContract)>();
            foreach (var c in contracts.All)
            {
                if (c.Category != A2lObjectCategory.Characteristic || c.DataType is null)
                    continue;
                if (c.Segments.Count != 1 || c.TotalByteLength != 1)
                    continue;
                if (!XcpAddressMap.TryTranslate(contracts.Document, c.Segments[0].Address, out var physical))
                    continue;
                cals.Add((c.ObjectName, (uint)physical, c));
                if (cals.Count == 3)
                    break;
            }
            Assert.True(cals.Count >= 2, "fixture 至少要有两个可写的 1B 标定对象");
            return (contracts, cals);
        });

    private static (CalibrationReconciler Reconciler, MemorySlaveTransport Slave, XcpTransportSpy Spy, List<(string Name, uint Addr, ValueContract C)> Cals) Make()
    {
        var (contracts, cals) = Fixture.Value;
        var slave = new MemorySlaveTransport();
        var spy = new XcpTransportSpy(slave);
        var master = new XcpMaster(spy, new XcpMasterOptions(
            new CanId(0x18FFF667, FrameFormat.Extended), TimeSpan.FromMilliseconds(100), 0));
        var writer = new XcpCalibrationWriter(master, new XcpCalibrationWriterOptions
        {
            BusyRetryDelay = TimeSpan.Zero,
            TimeProvider = new FakeTimeProvider(),
        });
        var reconciler = new CalibrationReconciler(writer, master);
        return (reconciler, slave, spy, cals);
    }

    private static CalibrationParameterSet SetOf(params (string Name, double Value)[] items)
    {
        var entries = items.Select(i => new CalibrationEntry(i.Name, i.Value, null, null)).ToList();
        return CalibrationParameterSet.Export(entries, "a" + new string('0', 63), "test");
    }

    private static byte[] Encoded(ValueContract c, double physical)
    {
        var raw = new byte[c.TotalByteLength];
        c.Encode(physical, raw);
        return raw;
    }

    private static int Masked(uint addr) => (int)(addr & 0xFFFF);

    [Fact]
    public async Task Reconcile_reports_no_difference_when_memory_already_matches()
    {
        var (r, slave, spy, cals) = Make();
        var target = 123.0;
        Encoded(cals[0].C, target).CopyTo(slave.Memory, Masked(cals[0].Addr));
        var set = SetOf((cals[0].Name, target));

        var report = await r.ReconcileAsync(set, Fixture.Value.Contracts);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CalibrationEntryStatus.NoDifference, entry.Status);
        Assert.Equal(target, entry.CurrentPhysical!.Value, 6);
    }

    [Fact]
    public async Task Reconcile_detects_difference_with_current_value()
    {
        var (r, slave, _, cals) = Make();
        var set = SetOf((cals[0].Name, 123.0)); // 内存为 0 → 必有差异

        var report = await r.ReconcileAsync(set, Fixture.Value.Contracts);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CalibrationEntryStatus.DiffFound, entry.Status);
        Assert.Equal(cals[0].C.Decode(new byte[1]), entry.CurrentPhysical);
    }

    [Fact]
    public async Task Apply_writes_only_changed_items()
    {
        var (r, slave, _, cals) = Make();
        // 对象 0 已是目标值（跳过），对象 1 有差异（写入）。
        Encoded(cals[0].C, 111.0).CopyTo(slave.Memory, Masked(cals[0].Addr));
        var set = SetOf((cals[0].Name, 111.0), (cals[1].Name, 222.0));

        var report = await r.ApplyAsync(set, Fixture.Value.Contracts, onlyChanged: true);

        Assert.Equal(1, report.WrittenCount);
        Assert.Equal(1, report.NoDifferenceCount);
        Assert.Equal(Encoded(cals[1].C, 222.0)[0], slave.Memory[Masked(cals[1].Addr)]);
    }

    [Fact]
    public async Task Apply_is_idempotent_on_rerun()
    {
        var (r, slave, spy, cals) = Make();
        var set = SetOf((cals[0].Name, 55.0), (cals[1].Name, 66.0));

        await r.ApplyAsync(set, Fixture.Value.Contracts, onlyChanged: true);
        var downloadsAfterFirst = slave.DownloadCount;

        var second = await r.ApplyAsync(set, Fixture.Value.Contracts, onlyChanged: true);

        Assert.Equal(2, second.NoDifferenceCount);
        Assert.Equal(0, second.WrittenCount);
        // 幂等 = 零新【写】流量（对账读流量必然发生：读当前值需 SET_MTA+UPLOAD）。
        Assert.Equal(downloadsAfterFirst, slave.DownloadCount);
    }

    [Fact]
    public async Task Missing_object_is_reported_with_zero_traffic()
    {
        var (r, _, spy, _) = Make();
        var set = SetOf(("NoSuchObject_XYZ", 1.0));

        var report = await r.ApplyAsync(set, Fixture.Value.Contracts, onlyChanged: true);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(CalibrationEntryStatus.ObjectMissing, entry.Status);
        Assert.Equal(0, spy.WriteCount);
    }

    [Fact]
    public async Task Batch_continues_after_single_write_failure()
    {
        var (r, slave, _, cals) = Make();
        slave.WriteProtectedOnce = true; // 第一个差异项写失败
        var set = SetOf((cals[0].Name, 11.0), (cals[1].Name, 22.0));

        var report = await r.ApplyAsync(set, Fixture.Value.Contracts, onlyChanged: true);

        Assert.Equal(1, report.WriteFailedCount);
        Assert.Equal(1, report.WrittenCount); // 第二项继续写成功
        Assert.Equal(Encoded(cals[1].C, 22.0)[0], slave.Memory[Masked(cals[1].Addr)]);
    }
}
