using PeakCan.HIL.Core;
using PeakCan.Host.Core.Xcp.Abstractions;
using PeakCan.Host.Core.Xcp.Protocol;
using PeakCan.Host.Core.Xcp.Scheduling;

namespace PeakCan.Host.Core.Tests.Xcp.Scheduling;

/// <summary>
/// S2-T12 BlockModeReader（spec §1：从机块模式 BLOCK SLAVE 无 MAX_BS/MIN_ST 声明，
/// 参数只能台架实测 A-10，<b>实测前不可启用</b>）：
/// 默认禁用（显式 <c>enabled: true</c> 才可开）、启用缺实测参数即构造失败（fail-loud）、
/// 禁用态任何操作即抛。S2 只交付参数化骨架：块模式协议逻辑（多帧应答窗口/
/// MIN_ST 节流）等 A-10 实测数据落地后另行实现。
/// </summary>
public class BlockModeReaderTests
{
    private static readonly CanId MasterCanId = new(0x18FFF667, FrameFormat.Extended);

    private static XcpMaster NewMaster() => new(new NullXcpTransport(), new XcpMasterOptions(MasterCanId));

    // ---- 默认构造 = 禁用，且无需实测参数 ----
    [Fact]
    public void Ctor_defaults_to_disabled_without_measured_parameters()
    {
        using var master = NewMaster();
        var reader = new BlockModeReader(master);

        Assert.False(reader.Enabled);
        Assert.Null(reader.Parameters);
    }

    // ---- 禁用语义：任何操作调用即抛 ----
    [Fact]
    public async Task Disabled_reader_throws_on_any_operation()
    {
        using var master = NewMaster();
        var reader = new BlockModeReader(master);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync("SomeObject"));
        Assert.Contains("A-10", ex.Message);
    }

    // ---- 启用缺实测参数：构造即失败（台架 A-10 前 BlockModeReader 不可启用）----
    [Fact]
    public void Enabled_ctor_requires_measured_max_bs_and_min_st()
    {
        using var master = NewMaster();

        var ex = Assert.Throws<InvalidOperationException>(
            () => new BlockModeReader(master, parameters: null, enabled: true));
        Assert.Contains("A-10", ex.Message);
    }

    // ---- 启用 + 实测参数：构造成功（A-10 实测后的唯一合法启用路径）----
    [Fact]
    public void Enabled_ctor_accepts_measured_parameters()
    {
        using var master = NewMaster();
        var reader = new BlockModeReader(
            master, parameters: new BlockModeParameters(MaxBs: 2, MinSt: 10), enabled: true);

        Assert.True(reader.Enabled);
        Assert.Equal(new BlockModeParameters(2, 10), reader.Parameters);
    }

    // ---- S2 边界：骨架不实现协议路径（显式 NotSupportedException，区别于禁用守卫）----
    [Fact]
    public async Task Enabled_reader_skeleton_is_explicitly_not_implemented()
    {
        using var master = NewMaster();
        var reader = new BlockModeReader(
            master, parameters: new BlockModeParameters(MaxBs: 2, MinSt: 10), enabled: true);

        await Assert.ThrowsAsync<NotSupportedException>(() => reader.ReadAsync("SomeObject"));
    }

    /// <summary>构造测试专用：不接线的空 transport（BlockModeReader 禁用/骨架态零线上流量）。</summary>
    private sealed class NullXcpTransport : IXcpTransport
    {
        public event Action<CanFrame>? FrameReceived { add { } remove { } }

        public long FramesDropped => 0;

        public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default) =>
            ValueTask.FromResult(Result<Unit>.Ok(default));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}