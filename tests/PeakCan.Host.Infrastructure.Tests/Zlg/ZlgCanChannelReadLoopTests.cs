using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.Infrastructure.Zlg;

namespace PeakCan.Host.Infrastructure.Tests.Zlg;

/// <summary>
/// 2026-09-04 审查 🟠 HIGH：ZLG 读循环放弃（bus-dead heuristic）后
/// <see cref="ZlgCanChannel.IsConnected"/> 仍为 true —— UI 显示
/// "已连接"但总线已死，仍允许发送/录制。对齐 PEAK 通道
/// （commit c9fbaaa 的 _gate.MarkFailed 修复）。
/// </summary>
public class ZlgCanChannelReadLoopTests
{
    private const uint DevType = 21; // USBCANFD-200U（值仅用于句柄解码，不触硬件）
    private const uint DevIdx = 0;
    private const uint CanIdx = 0;

    [Fact]
    public async Task ReadLoop_Gives_Up_Marks_Channel_Disconnected()
    {
        var channel = new ZlgCanChannel(
            new ChannelId(HandleFrom(DevType, DevIdx, CanIdx)),
            new ZlgDeviceManager(),
            logger: null,
            reader: new ThrowingZlgReader());

        // 读循环由 ConnectAsync 启动；测试直接用反射置位 _connected 模拟
        // 已连接状态（与 PEAK 通道 give-up 测试同款手法，不触硬件）。
        var connectedField = typeof(ZlgCanChannel).GetField("_connected",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        connectedField!.SetValue(channel, true);
        channel.IsConnected.Should().BeTrue();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await channel.ReadLoopAsync(cts.Token);

        channel.IsConnected.Should().BeFalse(
            "读循环放弃后通道必须标记为断开，否则 UI 显示'已连接但总线已死'");
    }

    // ChannelId.Handle 编码：高 1 位固定 1, 7 位 devType, 4 位 devIdx, 4 位 canIdx
    private static ushort HandleFrom(uint devType, uint devIdx, uint canIdx)
        => (ushort)(0x8000 | ((devType & 0x7F) << 8) | ((devIdx & 0x0F) << 4) | (canIdx & 0x0F));

    private sealed class ThrowingZlgReader : IZlgReader
    {
        public uint ReadClassic(uint devType, uint devIdx, uint canIdx, out ZlgCanMsg msg)
        {
            msg = default;
            throw new InvalidOperationException("Simulated bus-off: reader always throws");
        }

        public uint ReadFd(uint devType, uint devIdx, uint canIdx, out ZlgCanFdMsg msg)
        {
            msg = default;
            throw new InvalidOperationException("Simulated bus-off: reader always throws");
        }
    }
}
