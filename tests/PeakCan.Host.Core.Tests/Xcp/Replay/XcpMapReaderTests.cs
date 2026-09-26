using A2lEditor.Core;
using A2lEditor.Core.Layout;
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Tests.Xcp.TestKit;
using PeakCan.Host.Core.Xcp.Protocol;
using PeakCan.Host.Core.Xcp.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Xcp.Replay;

/// <summary>
/// S6-T4：MAP 只读读取器（spec D4 定案：在线 UPLOAD 拉全元素 + 两轴，只读零 DOWNLOAD）。
/// 真机 A2L fixture（App_merge_INCA.a2l，15 个 MAP）+ 内存从机播种（Encode→字节→Memory）。
/// </summary>
public sealed class XcpMapReaderTests
{
    private static readonly Lazy<(ContractSet Contracts, string MapName)> Fixture = new(() =>
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", "App_merge_INCA.a2l");
        var parsed = Asap2PackageApi.ParseFile(path);
        Assert.NotNull(parsed.Value);
        var contracts = Asap2PackageApi.Contracts(parsed.Value!);
        var doc = contracts.Document;

        foreach (var map in doc.Modules.SelectMany(m => m.Characteristics))
        {
            if (map.Type != "MAP" || map.EcuAddress is null || map.AxisDescrs.Count != 2)
                continue;
            if (map.AxisDescrs.Any(d => d.AxisPtsRef is null))
                continue;
            if (!contracts.TryGet(map.Name, out var mc) || mc.DataType is null)
                continue;

            var axes = map.AxisDescrs
                .Select(d => doc.Modules.SelectMany(m => m.AxisPts).FirstOrDefault(a => a.Name == d.AxisPtsRef))
                .ToArray();
            if (axes.Any(a => a is null || a.EcuAddress is null || a.NumberOfAxisPts is null))
                continue;

            // 低 16 位窗口不得重叠（内存从机寻址口径），否则换下一个对象。
            var addrs = new[] { map.EcuAddress.Value, axes[0]!.EcuAddress!.Value, axes[1]!.EcuAddress!.Value }
                .Select(a => a & 0xFFFF).ToArray();
            if (addrs.Distinct().Count() != 3)
                continue;
            var maxLen = (long)mc.TotalByteLength +
                axes.Max(a => (long)a!.NumberOfAxisPts!.Value * 8);
            if ((long)addrs.Min() + maxLen > 0x10000)
                continue;

            return (contracts, map.Name);
        }
        throw new InvalidOperationException("fixture 找不到可测的真机 MAP 对象");
    });

    private static (MemorySlaveTransport Slave, XcpTransportSpy Spy, XcpMaster Master) MakeMaster()
    {
        var slave = new MemorySlaveTransport();
        var spy = new XcpTransportSpy(slave);
        var master = new XcpMaster(spy, new XcpMasterOptions(
            new CanId(0x18FFF667, FrameFormat.Extended), TimeSpan.FromMilliseconds(100), 0));
        return (slave, spy, master);
    }

    /// <summary>播种 MAP + 两轴：物理值经 Encode 产字节（与换算无关的 round-trip 口径）。</summary>
    private static double[,] SeedMap(MemorySlaveTransport slave, ContractSet contracts, string name)
    {
        var doc = contracts.Document;
        var map = doc.Modules.SelectMany(m => m.Characteristics).First(c => c.Name == name);
        contracts.TryGet(name, out var mc);
        var refs = map.AxisDescrs.Select(d => d.AxisPtsRef!).ToArray();
        var axisPts = refs.Select(r => doc.Modules.SelectMany(m => m.AxisPts).First(a => a.Name == r)).ToArray();
        var axisContracts = refs.Select(r => contracts.TryGet(r, out var ac) ? ac : null).ToArray();

        var xCount = (int)map.AxisDescrs[0].MaxNumberOfAxisPoints!.Value;
        var yCount = (int)map.AxisDescrs[1].MaxNumberOfAxisPoints!.Value;
        var mapAddr = (int)(map.EcuAddress!.Value & 0xFFFF);
        var xAddr = (int)(axisPts[0].EcuAddress!.Value & 0xFFFF);
        var yAddr = (int)(axisPts[1].EcuAddress!.Value & 0xFFFF);

        var grid = new double[yCount, xCount];
        for (var y = 0; y < yCount; y++)
        for (var x = 0; x < xCount; x++)
            grid[y, x] = 100 + x + 10 * y;

        var elem = ByteLayout.SizeOf(mc!.DataType!.Value);
        var rawMap = new byte[mc.TotalByteLength];
        for (var y = 0; y < yCount; y++)
        for (var x = 0; x < xCount; x++)
        {
            var buf = new byte[elem];
            mc.Encode(grid[y, x], buf);
            buf.CopyTo(rawMap, (y * xCount + x) * elem);
        }
        rawMap.CopyTo(slave.Memory.AsSpan(mapAddr));

        for (var axis = 0; axis < 2; axis++)
        {
            var ac = axisContracts[axis]!;
            var ae = ByteLayout.SizeOf(ac.DataType!.Value);
            var count = ac.TotalByteLength / ae;
            var rawAxis = new byte[ac.TotalByteLength];
            for (var i = 0; i < count; i++)
            {
                var buf = new byte[ae];
                ac.Encode(1.5 * (i + 1) * (axis + 1), buf);
                buf.CopyTo(rawAxis, i * ae);
            }
            rawAxis.CopyTo(slave.Memory.AsSpan(axis == 0 ? xAddr : yAddr));
        }
        return grid;
    }

    [Fact]
    public async Task Online_reads_grid_and_axes_without_download()
    {
        var (contracts, name) = Fixture.Value;
        var (slave, spy, master) = MakeMaster();
        var expected = SeedMap(slave, contracts, name);

        var data = await XcpMapReader.ReadOnlineAsync(master, contracts, name);

        Assert.False(data.IsOffline);
        Assert.Equal(name, data.ObjectName);
        Assert.Equal(expected.GetLength(0), data.Grid.GetLength(0));
        Assert.Equal(expected.GetLength(1), data.Grid.GetLength(1));
        for (var y = 0; y < expected.GetLength(0); y++)
        for (var x = 0; x < expected.GetLength(1); x++)
            Assert.Equal(expected[y, x], data.Grid[y, x]);
        Assert.All(data.XValues, v => Assert.True(double.IsFinite(v)));
        Assert.All(data.YValues, v => Assert.True(double.IsFinite(v)));

        // 只读红线：线上零 DOWNLOAD（0xF0）帧。
        Assert.DoesNotContain(spy.Sent, f => f.Data.Length > 0 && f.Data.Span[0] == 0xF0);
    }

    [Fact]
    public void Offline_returns_structure_with_nan_grid()
    {
        var (contracts, name) = Fixture.Value;

        var data = XcpMapReader.ReadOffline(contracts, name);

        Assert.True(data.IsOffline);
        Assert.Equal(name, data.ObjectName);
        Assert.All(data.Grid.Cast<double>(), v => Assert.True(double.IsNaN(v)));
        Assert.NotEmpty(data.Detail);
    }

    [Fact]
    public void Non_map_object_rejected()
    {
        var (contracts, _) = Fixture.Value;
        var valueObject = contracts.Document.Modules
            .SelectMany(m => m.Characteristics).First(c => c.Type == "VALUE").Name;

        var ex = Assert.Throws<InvalidOperationException>(
            () => XcpMapReader.ReadOffline(contracts, valueObject));
        Assert.Contains("MAP", ex.Message);
    }
}
