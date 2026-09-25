using PeakCan.Host.Core.Tests.Xcp.TestKit;
using PeakCan.Host.Core.Xcp.Capability;
using PeakCan.Host.Core.Xcp.Protocol;
using Xunit;

namespace PeakCan.Host.Core.Tests.Xcp.Capability;

/// <summary>
/// XcpCapabilityProber 能力实测链（S3-T7b / D7 裁决：CLI probe 与 App VM 单源探测器）。
/// <para>
/// 钉死口径：(a) 全命令链含 0 字节 DOWNLOAD 良性探测（恰 1 次、BYTE_COUNT=0）；
/// (b) 非 CmdUnknown 负响应仍算命令存在（M1）；CmdUnknown 才排除；(c) 单项查询失败
/// 归因出站不终止链（L2）；(d) 实测值来自黄金样本解码（MaxCto 帧长观察派生）。
/// </para>
/// </summary>
public class XcpCapabilityProberTests
{
    private const uint MasterCanIdRaw = 0x18FFF667;
    private const uint SlaveCanIdRaw = 0x18FFF666;

    /// <summary>实测支持命令全集（5 信息类 + 9 良性探测，D7：含 DOWNLOAD）。</summary>
    private static readonly string[] AllMeasuredCommands =
    [
        "GET_COMM_MODE_INFO", "GET_DAQ_PROCESSOR_INFO", "GET_DAQ_RESOLUTION_INFO",
        "GET_DAQ_LIST_INFO", "GET_DAQ_EVENT_INFO",
        "SET_MTA", "UPLOAD", "SHORT_UPLOAD", "DOWNLOAD",
        "SET_DAQ_PTR", "WRITE_DAQ", "CLEAR_DAQ_LIST",
        "START_STOP_DAQ_LIST", "START_STOP_SYNCH",
    ];

    private static async Task<(XcpCapabilityProbeResult Probe, XcpTransportSpy Spy)> ProbeAgainstAsync(
        Action<XcpVirtualSlave>? script = null)
    {
        var slave = new XcpVirtualSlave();
        script?.Invoke(slave);
        var spy = new XcpTransportSpy(slave);
        await using var _ = spy;
        using var master = new XcpMaster(spy, new XcpMasterOptions(XcpVirtualSlave.DefaultMasterCanId));
        var probe = await XcpCapabilityProber.ProbeAsync(master, MasterCanIdRaw, SlaveCanIdRaw);
        return (probe, spy);
    }

    [Fact]
    public async Task Probe_full_chain_sends_exactly_one_zero_byte_download_and_measures_all_commands()
    {
        var (probe, spy) = await ProbeAgainstAsync();

        // D7：DOWNLOAD 良性探测恰 1 次，BYTE_COUNT=0（无数据可写，从机零效应）。
        var downloads = spy.Sent.Where(f => f.Data.Span[0] == XcpPid.Download).ToList();
        Assert.Single(downloads);
        Assert.Equal(0x00, downloads[0].Data.Span[1]);
        Assert.Equal(8, downloads[0].Data.Length);

        // 14 命令全部入实测集（黄金样本从机全支持）。
        Assert.Equal(
            AllMeasuredCommands.OrderBy(n => n, StringComparer.Ordinal),
            probe.Measured.OptionalCommands.OrderBy(n => n, StringComparer.Ordinal));

        // 实测值来自黄金样本解码：MaxCto = 线上最大响应帧长 8B。
        Assert.Empty(probe.QueryFailures);
        Assert.Equal(8, probe.Measured.MaxCto);
        Assert.Equal(1, (int)probe.Measured.MaxDaq);
        Assert.Equal(10000u, probe.Measured.EventPeriodMicroseconds);
    }

    [Fact]
    public async Task Probe_cmd_unknown_excludes_command_and_other_negative_response_counts_it()
    {
        // M1：SET_MTA 回 ERR_CMD_UNKNOWN（不支持），UPLOAD 回 ERR_OUT_OF_RANGE
        //（参数被拒 ≠ 不支持，仍算命令存在）。
        var (probe, _) = await ProbeAgainstAsync(slave =>
        {
            slave.OverrideNegative(XcpPid.SetMta, XcpError.CmdUnknown);
            slave.OverrideNegative(XcpPid.Upload, XcpError.OutOfRange);
        });

        Assert.DoesNotContain("SET_MTA", probe.Measured.OptionalCommands);
        Assert.Contains("UPLOAD", probe.Measured.OptionalCommands);
    }

    [Fact]
    public async Task Probe_query_failure_is_attributed_and_does_not_kill_chain()
    {
        // 真机偏差 #1（S2-T8 评审钉死）：GET_DAQ_EVENT_INFO 正响应仅 7B → 解码失败。
        var (probe, _) = await ProbeAgainstAsync(slave =>
            slave.OverrideResponse(XcpPid.GetDaqEventInfo, 0xFF, 0x01, 0x02, 0x03, 0x0A, 0x06));

        // L2：归因出站（命令 + 异常类型 + 预期偏差标记），链不终止。
        var failure = Assert.Single(probe.QueryFailures);
        Assert.Equal("GET_DAQ_EVENT_INFO", failure.Command);
        Assert.Equal("ArgumentException", failure.ExceptionType);
        Assert.Equal("expected-deviation", failure.Marker);

        // 失败项不冒充实测支持命令；后续探测（含 DOWNLOAD）照常。
        Assert.DoesNotContain("GET_DAQ_EVENT_INFO", probe.Measured.OptionalCommands);
        Assert.Contains("DOWNLOAD", probe.Measured.OptionalCommands);
        Assert.Null(probe.Measured.EventPeriodMicroseconds);
    }
}
