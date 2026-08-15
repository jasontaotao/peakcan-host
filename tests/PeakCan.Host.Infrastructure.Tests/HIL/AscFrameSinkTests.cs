using PeakCan.HIL.Core;
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

public class AscFrameSinkTests
{
    private static CanFrame F(ulong us, params byte[] data) =>
        new CanFrame(new CanId(0x123, FrameFormat.Standard),
            new ReadOnlyMemory<byte>(data), FrameFlags.None, ChannelId.None, new Timestamp(us));

    [Fact]
    public void Write_ProducesNFrameLines_FirstOffsetZero()
    {
        using var ms = new MemoryStream();
        using (var sink = new AscFrameSink(ms))
        {
            sink.Write(F(1000000, 0x01, 0x02));
            sink.Write(F(2000000, 0x03, 0x04));
        }
        var content = new System.Text.UTF8Encoding(true).GetString(ms.ToArray());
        // Golden literals match AscFileFormat.WriteFrameLine byte-exact:
        // {seconds,12:F6} -> 4 leading spaces; {idStr,-12} -> 7 trailing spaces.
        Assert.Contains("    0.000000 1  0x123       x       Rx d 2 01 02", content);
        Assert.Contains("    1.000000 1  0x123       x       Rx d 2 03 04", content);
    }

    [Fact]
    public void Dispose_FlushesBufferedFrames()
    {
        using var ms = new MemoryStream();
        var sink = new AscFrameSink(ms);
        sink.Write(F(1000000, 0x01));
        sink.Dispose();
        Assert.Contains("Rx d 1 01", new System.Text.UTF8Encoding(true).GetString(ms.ToArray()));
    }

    [Fact]
    public void Dispose_IsIdempotent() => Assert.Null(Record.Exception(() =>
    {
        using var ms = new MemoryStream();
        var sink = new AscFrameSink(ms);
        sink.Dispose();
        sink.Dispose();
    }));

    [Fact]
    public void Write_AfterDispose_SilentlyDrops()
    {
        using var ms = new MemoryStream();
        var sink = new AscFrameSink(ms);
        sink.Write(F(1000000, 0x01));
        sink.Dispose();
        var before = ms.ToArray().Length;
        Assert.Null(Record.Exception(() => sink.Write(F(2000000, 0x02))));
        Assert.Equal(before, ms.ToArray().Length);
    }

    [Fact]
    public void Empty_ProducesHeaderOnly()
    {
        using var ms = new MemoryStream();
        using (var sink = new AscFrameSink(ms)) { }
        var lines = new System.Text.UTF8Encoding(true).GetString(ms.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.Contains(lines, l => l.Contains("// version 8.5.0"));
    }

    [Fact]
    public void File_StartsWithUtf8Bom()
    {
        using var ms = new MemoryStream();
        using (var sink = new AscFrameSink(ms)) { }
        var bytes = ms.ToArray();
        Assert.True(bytes.Length >= 3);
        Assert.Equal(0xEF, bytes[0]); Assert.Equal(0xBB, bytes[1]); Assert.Equal(0xBF, bytes[2]);
    }

    [Fact]
    public void Write_ThrowingStream_DoesNotPropagate()
    {
        using var sink = new AscFrameSink(new ThrowingStream());
        Assert.Null(Record.Exception(() => sink.Write(F(1000000, 0x01))));
    }

    private sealed class ThrowingStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("disk full");
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            throw new IOException("disk full");
    }
}
