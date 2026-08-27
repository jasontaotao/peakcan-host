using System.Text;
using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Replay;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.Infrastructure.Tests;

public class TraceDrivenChannelBlfTests
{
    /// <summary>
    /// 构建最小合成 BLF 文件（参照 ReplayServiceBlfLoadTests.BuildSyntheticBlf）。
    /// </summary>
    private static void BuildSyntheticBlf(string path, uint canId = 0x123, byte[]? data = null)
    {
        using var ms = new MemoryStream();
        // 24-byte file header
        ms.Write(Encoding.ASCII.GetBytes(BlfFormat.FileSignature));
        ms.Write(new byte[BlfFormat.FileHeaderSize - 4]);
        // ObjectHeader (32 bytes)
        ms.Write(Encoding.ASCII.GetBytes(BlfFormat.ObjSignature));
        ms.Write(BitConverter.GetBytes((ushort)BlfFormat.ObjectHeaderSize));
        ms.Write(BitConverter.GetBytes((ushort)1));
        var frameData = data ?? new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04 };
        var totalObjBytes = (uint)BlfFormat.ObjectHeaderSize + (uint)BlfFormat.CanMessageDataSize;
        ms.Write(BitConverter.GetBytes(totalObjBytes));
        ms.Write(BitConverter.GetBytes(BlfFormat.ObjTypeCanMessage));
        ms.Write(BitConverter.GetBytes(0u));
        ms.Write(BitConverter.GetBytes((ushort)0));
        ms.Write(BitConverter.GetBytes((ushort)0));
        ms.Write(BitConverter.GetBytes(5_000_000L));
        // CanMessage frame data
        ms.Write(BitConverter.GetBytes((ushort)1));
        ms.WriteByte(0);
        ms.WriteByte((byte)frameData.Length);
        ms.Write(BitConverter.GetBytes(canId));
        ms.Write(frameData);
        File.WriteAllBytes(path, ms.ToArray());
    }

    [Fact]
    public async Task LoadBlf_ValidFile_LoadsFrames()
    {
        // Arrange
        var blfPath = Path.Combine(Path.GetTempPath(), $"hil-blf-{Guid.NewGuid():N}.blf");
        try
        {
            BuildSyntheticBlf(blfPath, canId: 0x123);
            var channel = new TraceDrivenChannel(new ChannelId(1));

            // Act
            channel.LoadBlf(blfPath);

            // Assert — 验证帧已加载（通过 ConnectAsync 间接验证）
            // LoadBlf 不直接暴露帧数量，但 ConnectAsync 要求 _playStartTimestamp >= 0
            // 如果帧未加载，会抛 "No trace loaded"
            await channel.ConnectAsync(BaudRate.CanFd1Mbps, false);
            await channel.DisconnectAsync();
        }
        finally
        {
            if (File.Exists(blfPath)) File.Delete(blfPath);
        }
    }

    [Fact]
    public void LoadBlf_ExceedsMaxTraceFrames_Throws()
    {
        // Arrange
        var blfPath = Path.Combine(Path.GetTempPath(), $"hil-blf-{Guid.NewGuid():N}.blf");
        try
        {
            BuildSyntheticBlf(blfPath);
            // maxTraceFrames=0 → 任何帧都超限
            var channel = new TraceDrivenChannel(new ChannelId(1), maxTraceFrames: 0);

            // Act & Assert
            var ex = Assert.Throws<InvalidOperationException>(() => channel.LoadBlf(blfPath));
            Assert.Contains("exceeds MaxTraceFrames", ex.Message);
        }
        finally
        {
            if (File.Exists(blfPath)) File.Delete(blfPath);
        }
    }

    [Fact]
    public void LoadBlf_FileNotFound_Throws()
    {
        // Arrange
        var channel = new TraceDrivenChannel(new ChannelId(1));

        // Act & Assert
        Assert.Throws<FileNotFoundException>(() => channel.LoadBlf("nonexistent.blf"));
    }

    /// <summary>
    /// HIL BLF 扩展帧守卫：真实 BLF 扩展帧的 frame_id 带 bit31（Vector 扩展标记位）。
    /// 重构前 TraceDrivenChannel.ToCanFrame 用 > 0x7FF 判 format 不掩码 bit31，
    /// 对扩展帧抛 ArgumentOutOfRangeException × 1000/s（1ms timer）→ CPU 空转 +
    /// 回放静默失败。重构后 parser 掩码 bit31 + 填 IsExtended，consumer 读标记。
    /// </summary>
    [Fact]
    public async Task LoadBlf_ExtendedFrame_EmitsWithExtendedFormat()
    {
        // 合成 BLF：canId = 0x18FFC23A | 0x80000000（bit31 置位，模拟真实 BLF 扩展帧）
        var blfPath = Path.Combine(Path.GetTempPath(), $"hil-blf-ext-{Guid.NewGuid():N}.blf");
        try
        {
            BuildSyntheticBlf(blfPath, canId: 0x18FFC23Au | 0x80000000u);
            var ch = new TraceDrivenChannel(new ChannelId(1));
            ch.LoadBlf(blfPath);

            var frames = new List<CanFrame>();
            ch.FrameReceived += f => frames.Add(f);

            await ch.ConnectAsync(BaudRate.Can500kbps, false);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (frames.Count < 1 && sw.ElapsedMilliseconds < 5000)
                await Task.Delay(50);

            frames.Should().NotBeEmpty("扩展帧必须被发射，不抛 ArgumentOutOfRangeException");
            frames[0].Id.IsExtended.Should().BeTrue("bit31 置位的 BLF frame_id 是扩展帧");
            frames[0].Id.Raw.Should().Be(0x18FFC23Au,
                "parser 掩码 bit31 后 consumer 拿到裸 29 位值");
        }
        finally
        {
            if (File.Exists(blfPath)) File.Delete(blfPath);
        }
    }
}
