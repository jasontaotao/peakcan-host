using System.Text;
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
}
