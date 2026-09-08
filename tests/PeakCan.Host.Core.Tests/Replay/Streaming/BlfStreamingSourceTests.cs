using System.IO;
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using PeakCan.Host.Core.Replay;
using Xunit;
using PeakCan.HIL.Core;

namespace PeakCan.Host.Core.Tests.Replay.Streaming;

public class BlfStreamingSourceTests
{
    [Fact]
    public async Task OpenAsync_StreamsWithDirectCanObjects()
    {
        var ms = new MemoryStream();
        WriteFileHeader(ms);
        WriteCanMessage(ms, timestampTicks: 0);
        WriteCanMessage(ms, timestampTicks: 500_000_000L);
        ms.Position = 0;

        var source = new BlfStreamingSource(() => new MemoryStream(ms.ToArray()));
        await using var open = await source.OpenAsync();
        var frames = await ToListAsync(open.Frames);

        frames.Should().HaveCount(2);
        frames[0].Id.Should().Be(0x123u);
        frames[1].Timestamp.Should().Be(0.5);
    }

    [Fact]
    public async Task OpenAsync_SkipsUntilTargetTimestamp()
    {
        var ms = new MemoryStream();
        WriteFileHeader(ms);
        WriteCanMessage(ms, 0);
        WriteCanMessage(ms, 500_000_000L);
        WriteCanMessage(ms, 1_000_000_000L);
        ms.Position = 0;

        var source = new BlfStreamingSource(() => new MemoryStream(ms.ToArray()));
        await using var open = await source.OpenAsync(0.5);
        var frames = await ToListAsync(open.Frames);

        frames.Select(f => f.Timestamp).Should().Equal(0.5, 1.0);
    }

    [Fact]
    public async Task OpenAsync_UnknownObjectType_IsSkipped()
    {
        var ms = new MemoryStream();
        WriteFileHeader(ms);
        WriteObject(ms, 999, 8, w => w.Write(new byte[8]), 0);
        WriteCanMessage(ms, 100_000_000L);
        ms.Position = 0;

        var source = new BlfStreamingSource(() => new MemoryStream(ms.ToArray()));
        await using var open = await source.OpenAsync();
        var frames = await ToListAsync(open.Frames);

        frames.Should().ContainSingle().Which.Id.Should().Be(0x123u);
    }

    [Fact]
    public async Task OpenAsync_LogContainer_IsUnpackedLazily()
    {
        var inner = new MemoryStream();
        WriteObject(inner, BlfFormat.ObjTypeCanMessage, BlfFormat.CanMessageDataSize, w =>
        {
            w.Write((ushort)1);
            w.Write((byte)0);
            w.Write((byte)8);
            w.Write((uint)0x456);
            w.Write(new byte[8]);
        }, 0);
        var compressed = CompressZlib(inner.ToArray());

        var ms = new MemoryStream();
        WriteFileHeader(ms);
        WriteObject(ms, BlfFormat.ObjTypeLogContainer, compressed.Length, w => w.Write(compressed), 0);
        ms.Position = 0;

        var source = new BlfStreamingSource(() => new MemoryStream(ms.ToArray()));
        await using var open = await source.OpenAsync();
        var frames = await ToListAsync(open.Frames);

        frames.Should().ContainSingle().Which.Id.Should().Be(0x456u);
    }

    [Fact]
    public async Task OpenAsync_BadMagic_Throws()
    {
        var ms = new MemoryStream("BAD!"u8.ToArray());
        var source = new BlfStreamingSource(() => ms);
        await FluentActions.Awaiting(() => source.OpenAsync()).Should().ThrowAsync<ReplayFormatException>();
    }

    private static void WriteFileHeader(MemoryStream ms)
    {
        ms.Write(Encoding.ASCII.GetBytes(BlfFormat.FileSignature));
        ms.Write(new byte[BlfFormat.FileHeaderSize - 4]);
    }

    private static void WriteCanMessage(MemoryStream ms, long timestampTicks, uint id = 0x123)
    {
        WriteObject(ms, BlfFormat.ObjTypeCanMessage, BlfFormat.CanMessageDataSize, w =>
        {
            w.Write((ushort)1);
            w.Write((byte)0);
            w.Write((byte)8);
            w.Write(id);
            w.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        }, timestampTicks);
    }

    private static void WriteObject(
        MemoryStream ms,
        uint objType,
        int objectDataSize,
        Action<BinaryWriter> writeFrameData,
        long timestamp)
    {
        ms.Write(Encoding.ASCII.GetBytes(BlfFormat.ObjSignature));
        ms.Write(BitConverter.GetBytes((ushort)BlfFormat.ObjectHeaderSize));
        ms.Write(BitConverter.GetBytes((ushort)1));
        ms.Write(BitConverter.GetBytes((uint)(BlfFormat.ObjectHeaderSize + objectDataSize)));
        ms.Write(BitConverter.GetBytes(objType));
        ms.Write(BitConverter.GetBytes(0u));
        ms.Write(BitConverter.GetBytes((ushort)0));
        ms.Write(BitConverter.GetBytes((ushort)0));
        ms.Write(BitConverter.GetBytes(timestamp));

        var position = ms.Position;
        using var writer = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        writeFrameData(writer);
        (ms.Position - position).Should().Be(objectDataSize);
    }

    private static void WriteLogContainer(MemoryStream ms, byte[] innerObjects)
    {
        var compressed = CompressZlib(innerObjects);
        WriteObject(ms, BlfFormat.ObjTypeLogContainer, compressed.Length, w => w.Write(compressed), 0);
    }
    private static byte[] CompressZlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }
        return output.ToArray();
    }

    private static async Task<List<ReplayFrame>> ToListAsync(IAsyncEnumerable<ReplayFrame> frames)
    {
        var result = new List<ReplayFrame>();
        await foreach (var frame in frames) result.Add(frame);
        return result;
    }
}








