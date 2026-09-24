using System.Text.Json;
using PeakCan.Host.Cli;
using PeakCan.Host.Core.Tests.Xcp.TestKit;
using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Cli.Tests.Xcp;

/// <summary>
/// xcp-probe 子命令（S2-T8，spec §4 / 决策 D3）：对模拟从机全流程——
/// CONNECT → 能力查询全链 → CapabilityReconciler 对账 → 事实清单（A-1/2/3/4/5）。
/// 退出码语义：对账拒绝 = 非零且事实清单仍输出；CAN ID 必显式给出（无默认兜底）。
/// </summary>
public class XcpProbeCommandTests
{
    /// <summary>与 Core.Tests 共用的真机 A2L 冒烟夹具（同一文件，SHA256 基线一致）。</summary>
    private static readonly string RealA2LPath =
        Path.Combine(AppContext.BaseDirectory, "TestData", "App_merge_INCA.a2l");

    private static string TempJsonPath() =>
        Path.Combine(Path.GetTempPath(), $"xcp-probe-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task Probe_full_chain_against_virtual_slave_exits_zero_and_outputs_fact_list()
    {
        await using var slave = new XcpVirtualSlave();
        var outputPath = TempJsonPath();

        var options = XcpProbeCommand.ParseArgs(
        [
            "--a2l", RealA2LPath,
            "--xcp-master-id", "0x18FFF667",
            "--xcp-slave-id", "0x18FFF666",
            "--output", outputPath,
        ]);
        var exit = await XcpProbeCommand.RunAsync(options, slave);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(outputPath), "fact-list JSON must be written to --output.");

        var doc = JsonDocument.Parse(File.ReadAllText(outputPath));

        // A-1 能力：全部来自 CONNECT/GET_DAQ_*_INFO 解码（黄金样本从机 = spec §1 基线）。
        var measured = doc.RootElement.GetProperty("measured");
        Assert.Equal(1u, measured.GetProperty("maxDaq").GetUInt32());
        Assert.Equal(1u, measured.GetProperty("maxEventChannel").GetUInt32());
        Assert.Equal(0u, measured.GetProperty("minDaq").GetUInt32());
        Assert.Equal(0x0Fu, measured.GetProperty("maxOdt").GetUInt32());
        Assert.Equal(8u, measured.GetProperty("maxCto").GetUInt32());
        Assert.Equal(8u, measured.GetProperty("maxDto").GetUInt32());
        Assert.Equal(4u, measured.GetProperty("maxOdtEntrySizeDaq").GetUInt32());
        Assert.Equal(10000u, measured.GetProperty("eventPeriodMicroseconds").GetUInt32());

        // A-2 事件节拍：线上 (0x0A, 0x06) 经 XcpWireTimeUnit → 10000µs，与 A2L 声明一致。
        var eventPeriod = doc.RootElement.GetProperty("eventPeriod");
        Assert.Equal(0x0A, eventPeriod.GetProperty("measuredEventCycle").GetByte());
        Assert.Equal(0x06, eventPeriod.GetProperty("measuredWireTimeUnit").GetByte());
        Assert.Equal(10000u, eventPeriod.GetProperty("measuredPeriodMicroseconds").GetUInt32());
        Assert.Equal(10000u, eventPeriod.GetProperty("declaredPeriodMicroseconds").GetUInt32());

        // A-3 间隔抖动：S2 无时钟同步 → 占位（不得伪造实测值）。
        Assert.Equal("placeholder", doc.RootElement.GetProperty("dtoIntervalJitter").GetProperty("status").GetString());

        // A-4 CAN 号合规：声明 0x98FFF666/67 vs 实际使用 0x18FFF667/66（A-4 台架核实前占位）。
        var canIdCompliance = doc.RootElement.GetProperty("canIdCompliance");
        Assert.Equal(0x98FFF666u, canIdCompliance.GetProperty("declaredMasterCanIdRaw").GetUInt32());
        Assert.Equal(0x98FFF667u, canIdCompliance.GetProperty("declaredSlaveCanIdRaw").GetUInt32());
        Assert.Equal(0x18FFF667u, canIdCompliance.GetProperty("usedMasterCanIdRaw").GetUInt32());
        Assert.Equal(0x18FFF666u, canIdCompliance.GetProperty("usedSlaveCanIdRaw").GetUInt32());
        Assert.Equal("pending-bench-verification", canIdCompliance.GetProperty("status").GetString());

        // A-5 ODT 打包上限：DTO 数据场 7B（8B−PID），条目 ≤4B → 每 ODT 至多 1 个完整 4B 条目。
        var odtPacking = doc.RootElement.GetProperty("odtPacking");
        Assert.Equal(7, odtPacking.GetProperty("dtoPayloadCapBytes").GetInt32());
        Assert.Equal(1, odtPacking.GetProperty("maxEntriesPerOdt").GetInt32());

        // 对账结论：真机声明 vs 模拟从机黄金样本一致 → 允许启动。
        Assert.False(doc.RootElement.GetProperty("reconciliation").GetProperty("rejectedStart").GetBoolean());

        // 实测命令集：逐命令探测（含 DOWNLOAD——0 字节探测帧）。
        var measuredCommands = doc.RootElement.GetProperty("measuredCommands").EnumerateArray()
            .Select(e => e.GetString()).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("DOWNLOAD", measuredCommands);
        Assert.Contains("START_STOP_DAQ_LIST", measuredCommands);
        Assert.Contains("GET_DAQ_EVENT_INFO", measuredCommands);

        File.Delete(outputPath);
    }

    [Fact]
    public async Task Probe_reconciliation_reject_exits_nonzero_and_still_outputs_fact_list()
    {
        await using var slave = new XcpVirtualSlave();
        // 篡改从机实测 MAX_DAQ：1 → 5（A2L 声明 1）→ 对账拒绝。
        slave.OverrideResponse(XcpPid.GetDaqProcessorInfo, 0xFF, 0x05, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00);
        var outputPath = TempJsonPath();

        var options = XcpProbeCommand.ParseArgs(
        [
            "--a2l", RealA2LPath,
            "--xcp-master-id", "0x18FFF667",
            "--xcp-slave-id", "0x18FFF666",
            "--output", outputPath,
        ]);
        var exit = await XcpProbeCommand.RunAsync(options, slave);

        Assert.Equal(1, exit);
        Assert.True(File.Exists(outputPath), "对账拒绝时事实清单仍必须输出。");

        var doc = JsonDocument.Parse(File.ReadAllText(outputPath));
        Assert.True(doc.RootElement.GetProperty("reconciliation").GetProperty("rejectedStart").GetBoolean());
        Assert.Contains(
            doc.RootElement.GetProperty("reconciliation").GetProperty("findings").EnumerateArray(),
            f => f.GetProperty("code").GetString() == "MAX_DAQ_MISMATCH"
                 && f.GetProperty("severity").GetString() == "Reject");

        File.Delete(outputPath);
    }

    [Fact]
    public void ParseArgs_requires_explicit_can_ids_no_default_fallback()
    {
        Assert.Throws<ArgumentException>(
            () => XcpProbeCommand.ParseArgs(["--a2l", RealA2LPath]));
        Assert.Throws<ArgumentException>(
            () => XcpProbeCommand.ParseArgs(["--a2l", RealA2LPath, "--xcp-slave-id", "0x18FFF666"]));
        Assert.Throws<ArgumentException>(
            () => XcpProbeCommand.ParseArgs(["--a2l", RealA2LPath, "--xcp-master-id", "0x18FFF667"]));
    }

    [Fact]
    public void ParseArgs_parses_explicit_can_ids_and_output_path()
    {
        var outputPath = TempJsonPath();
        var options = XcpProbeCommand.ParseArgs(
        [
            "--a2l", RealA2LPath,
            "--xcp-master-id", "0x18FFF667",
            "--xcp-slave-id", "0x18FFF666",
            "--output", outputPath,
        ]);

        Assert.Equal(RealA2LPath, options.A2LPath);
        Assert.Equal(0x18FFF667u, options.MasterCanIdRaw);
        Assert.Equal(0x18FFF666u, options.SlaveCanIdRaw);
        Assert.Equal(outputPath, options.OutputPath);
    }
}
