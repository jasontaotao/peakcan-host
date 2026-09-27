using System.Text.Json;
using A2lEditor.Core;
using A2lEditor.Core.Layout;
using PeakCan.Host.Cli;
using PeakCan.Host.Core.Tests.Xcp.TestKit;
using Xunit;

namespace PeakCan.Host.Cli.Tests.Xcp;

/// <summary>
/// xcp-bench 子命令（S7-T6）：13 项挂账矩阵批次——探针 A 系 / 静态扫描 C-2 /
/// 写场景 B 系 C-1（需安全态旗标）/ MAP 上传 C-3。退出码：0 完成 / 1 连接级失败（报告仍落盘）。
/// </summary>
public class XcpBenchCommandTests
{
    private static readonly string RealA2LPath =
        Path.Combine(AppContext.BaseDirectory, "TestData", "App_merge_INCA.a2l");

    private static string TempJsonPath() =>
        Path.Combine(Path.GetTempPath(), $"xcp-bench-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task ReadOnly_batch_against_virtual_slave_exits_zero_and_writes_report()
    {
        await using var slave = new XcpVirtualSlave();
        var outputPath = TempJsonPath();

        var options = XcpBenchCommand.ParseArgs(
        [
            "--a2l", RealA2LPath,
            "--xcp-master-id", "0x18FFF667",
            "--xcp-slave-id", "0x18FFF666",
            "--output", outputPath,
        ]);
        var result = await XcpBenchCommand.RunAsync(options, slave);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(outputPath));

        using var doc = JsonDocument.Parse(File.ReadAllText(outputPath));
        Assert.Equal("ReadOnly", doc.RootElement.GetProperty("mode").GetString());

        var itemIds = doc.RootElement.GetProperty("items")
            .EnumerateArray().Select(i => i.GetProperty("itemId").GetString()).ToList();

        // 13 项挂账全部出栏（含 NotCollected——"未采"也是结论，宁全不全）。
        foreach (var id in new[] { "A-1", "A-2", "A-3", "A-4", "A-5", "A-10", "A-11", "B-1", "B-2", "B-3", "B-4", "C-1", "C-2", "C-3" })
            Assert.Contains(id, itemIds);

        // P1-1 钉：14 项每项恰一行（探针与 CLI 汇总层不得重复出栏）。
        Assert.Single(itemIds, id => id == "A-3");

        // C-2 静态扫描必然完成（离线，无传输依赖）。
        var c2 = doc.RootElement.GetProperty("items")
            .EnumerateArray().First(i => i.GetProperty("itemId").GetString() == "C-2");
        Assert.Equal("Measured", c2.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Full_batch_on_memory_slave_runs_write_scenarios_and_flags_connection_failure()
    {
        // MemorySlaveTransport（有内存语义）+ 真机 fixture 合同：连接级探针失败
        // （无 CONNECT 应答）→ exit 1 fail-loud；写场景仍全跑并记录（宁全不全）。
        var slave = new MemorySlaveTransport();
        var outputPath = TempJsonPath();
        var objectName = PickBenchObject();

        var options = XcpBenchCommand.ParseArgs(
        [
            "--a2l", RealA2LPath,
            "--xcp-master-id", "0x18FFF667",
            "--xcp-slave-id", "0x18FFF666",
            "--object", objectName,
            "--value", "9.0",
            "--i-have-verified-safe-state",
            "--output", outputPath,
        ]);
        var result = await XcpBenchCommand.RunAsync(options, slave);

        Assert.Equal(1, result.ExitCode);
        Assert.True(File.Exists(outputPath));

        using var doc = JsonDocument.Parse(File.ReadAllText(outputPath));
        Assert.Equal("Full", doc.RootElement.GetProperty("mode").GetString());

        // A-1 记录探针失败（不冒充实测）。
        var items = doc.RootElement.GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("itemId").GetString()!, i => i);
        Assert.Equal("NotCollected", items["A-1"].GetProperty("status").GetString());

        // 写场景全跑（内存从机支持 SET_MTA/DOWNLOAD/UPLOAD 口径）。
        Assert.True(items["B-1"].GetProperty("status").GetString() == "Measured",
            $"B-1 summary: {items["B-1"].GetProperty("summary").GetString()}");
        Assert.Equal("Measured", items["B-2"].GetProperty("status").GetString());
        Assert.Equal("Measured", items["B-4"].GetProperty("status").GetString());

        // 还原记录存在（零遗留证据）。
        Assert.True(doc.RootElement.GetProperty("restores").GetArrayLength() >= 2);
    }

    /// <summary>动态选一个可写多元素对象（C-1 广播语义可测；S5 写回测试同 fixture 口径）。</summary>
    private static string PickBenchObject()
    {
        var parsed = Asap2PackageApi.ParseFile(RealA2LPath);
        var contracts = Asap2PackageApi.Contracts(parsed!.Value!);
        var doc = contracts.Document;
        foreach (var ch in doc.Modules.SelectMany(m => m.Characteristics))
        {
            if (!contracts.TryGet(ch.Name, out var c) || c.DataType is null || !c.IsWritable)
                continue;
            if (c.Segments.Count == 0 || c.TotalByteLength == 0)
                continue;
            var elementBytes = ByteLayout.SizeOf(c.DataType.Value);
            if (elementBytes > 0 && c.TotalByteLength % elementBytes == 0
                && c.TotalByteLength / elementBytes > 1)
                return ch.Name;
        }
        throw new InvalidOperationException("fixture 找不到可写的多元素对象");
    }
}
