using System.Text;
using FluentAssertions;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Mobile.Core.Services;
using Xunit;
using PeakCan.HIL.Core;

namespace PeakCan.Host.Mobile.Core.Tests.Services;

public class BlfDurationScannerTests
{
    [Fact]
    public async Task ScanAsync_ReturnsDurationAndFrameCount()
    {
        var ms = new MemoryStream();
        WriteFileHeader(ms);
        WriteCanMessage(ms, 0);
        WriteCanMessage(ms, 1_000_000_000L);
        WriteCanMessage(ms, 2_000_000_000L);
        ms.Position = 0;

        var progressReports = new List<double>();
        var progress = new Progress<double>(progressReports.Add);
        var result = await BlfDurationScanner.ScanAsync(ms, progress);

        result.DurationSeconds.Should().Be(2);
        result.FrameCount.Should().Be(3);
    }

    private static void WriteFileHeader(MemoryStream ms)
    {
        ms.Write(Encoding.ASCII.GetBytes(BlfFormat.FileSignature));
        ms.Write(new byte[BlfFormat.FileHeaderSize - 4]);
    }

    private static void WriteCanMessage(MemoryStream ms, long timestampTicks)
    {
        ms.Write(Encoding.ASCII.GetBytes(BlfFormat.ObjSignature));
        ms.Write(BitConverter.GetBytes((ushort)BlfFormat.ObjectHeaderSize));
        ms.Write(BitConverter.GetBytes((ushort)1));
        ms.Write(BitConverter.GetBytes((uint)(BlfFormat.ObjectHeaderSize + BlfFormat.CanMessageDataSize)));
        ms.Write(BitConverter.GetBytes(BlfFormat.ObjTypeCanMessage));
        ms.Write(BitConverter.GetBytes(0u));
        ms.Write(BitConverter.GetBytes((ushort)0));
        ms.Write(BitConverter.GetBytes((ushort)0));
        ms.Write(BitConverter.GetBytes(timestampTicks));
        using var writer = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        writer.Write((ushort)1);
        writer.Write((byte)0);
        writer.Write((byte)8);
        writer.Write((uint)0x123);
        writer.Write(new byte[8]);
    }
}
