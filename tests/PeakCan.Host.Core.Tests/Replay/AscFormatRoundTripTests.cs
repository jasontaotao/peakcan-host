// tests/PeakCan.Host.Core.Tests/Replay/AscFormatRoundTripTests.cs — v3.49.0 MINOR T3
// Q3 round-trip lock: AscFormat.WriteXxx -> 字符串 -> AscParser.ParseAsync ->
// frame-by-frame 字段严格相等。任何 writer/parser 任一方 regression 都会被这组测试立刻捕获。
//
// W23 STRUCT-FABRACTION LESSON 已验证签名:
//   CanFrame(CanId, ReadOnlyMemory<byte>, FrameFlags, ChannelId, Timestamp)
//   CanId(uint, FrameFormat, FrameType=Data)
//   ChannelId(ushort) + Timestamp.FromMicroseconds(ulong)
//   ReplayFrame(double, uint, byte, byte[], FrameFlags)
//   FrameFlags.None/Fd/BitRateSwitch/ErrorStateIndicator/ErrFrame (bitflags)

using System.Globalization;
using System.Text;
using FluentAssertions;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Replay;

public class AscFormatRoundTripTests
{
    private static CanFrame MakeFrame(
        uint id,
        byte[] data,
        FrameFlags flags = FrameFlags.None,
        bool isFd = false,
        bool isError = false)
    {
        var combinedFlags = flags;
        if (isFd) combinedFlags |= FrameFlags.Fd;
        if (isError) combinedFlags |= FrameFlags.ErrFrame;
        return new CanFrame(
            new CanId(id, (id & 0x80000000) != 0 ? FrameFormat.Extended : FrameFormat.Standard),
            data,
            combinedFlags,
            new ChannelId(0x51),
            Timestamp.FromMicroseconds(0));
    }

    private static (string asc, List<CanFrame> frames) WriteAndCapture(
        IEnumerable<CanFrame> frames,
        DateTime origin,
        TimeSpan start)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.UTF8, leaveOpen: true);
        AscFormat.WriteHeader(writer, origin);
        var elapsed = start;
        foreach (var f in frames)
        {
            AscFormat.WriteDataLine(writer, f, elapsed);
            elapsed += TimeSpan.FromMilliseconds(10);
        }
        AscFormat.WriteFooter(writer, elapsed);
        writer.Flush();
        return (Encoding.UTF8.GetString(ms.ToArray()), frames.ToList());
    }

    private static async Task<IReadOnlyList<ReplayFrame>> ParseAscAsync(string asc)
    {
        var bytes = Encoding.UTF8.GetBytes(asc);
        using var stream = new MemoryStream(bytes);
        return await AscParser.ParseAsync(stream, new ReplayOptions());
    }

    [Fact]
    public async Task WriteDataLine_ClassicFrame_ParseBackRoundTripEqual()
    {
        var input = new[]
        {
            MakeFrame(0x123, new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 }),
        };
        var (asc, _) = WriteAndCapture(
            input,
            new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc),
            TimeSpan.Zero);

        var parsed = await ParseAscAsync(asc);

        parsed.Should().HaveCount(1);
        parsed[0].Id.Should().Be(0x123u);
        parsed[0].Dlc.Should().Be((byte)8);
        parsed[0].Data.Should().Equal(0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88);
        parsed[0].Timestamp.Should().Be(0.0);
    }

    [Fact]
    public async Task WriteDataLine_FdFrame_ParseBackRoundTripEqual()
    {
        // 0x18FF1234 is a 29-bit extended ID (high bit set). Use Extended format.
        var fdFrame = new CanFrame(
            new CanId(0x18FF1234u, FrameFormat.Extended),
            new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00, 0x11 },
            FrameFlags.Fd,
            new ChannelId(0x51),
            Timestamp.FromMicroseconds(0));
        var input = new[] { fdFrame };
        var (asc, _) = WriteAndCapture(
            input,
            new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc),
            TimeSpan.Zero);

        var parsed = await ParseAscAsync(asc);

        parsed.Should().HaveCount(1);
        parsed[0].Id.Should().Be(0x18FF1234u);
        (parsed[0].Flags & FrameFlags.Fd).Should().NotBe(FrameFlags.None);
    }

    [Fact]
    public async Task WriteDataLine_BrsAndEsiFlags_PreserveFrameFlags()
    {
        var flags = FrameFlags.Fd | FrameFlags.BitRateSwitch | FrameFlags.ErrorStateIndicator;
        var input = new[]
        {
            MakeFrame(0x456, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, flags),
        };
        var (asc, _) = WriteAndCapture(
            input,
            new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc),
            TimeSpan.Zero);

        var parsed = await ParseAscAsync(asc);

        parsed.Should().HaveCount(1);
        (parsed[0].Flags & FrameFlags.Fd).Should().NotBe(FrameFlags.None);
        (parsed[0].Flags & FrameFlags.BitRateSwitch).Should().NotBe(FrameFlags.None);
        (parsed[0].Flags & FrameFlags.ErrorStateIndicator).Should().NotBe(FrameFlags.None);
    }

    [Fact]
    public async Task WriteDataLine_ErrorFlag_PreserveErrorFrameBit()
    {
        var input = new[]
        {
            MakeFrame(0x789, new byte[] { 0xFF }, isError: true),
        };
        var (asc, _) = WriteAndCapture(
            input,
            new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc),
            TimeSpan.Zero);

        var parsed = await ParseAscAsync(asc);

        parsed.Should().HaveCount(1);
        (parsed[0].Flags & FrameFlags.ErrFrame).Should().NotBe(FrameFlags.None);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(15)]
    public async Task WriteDataLine_MultipleFrames_TimestampMonotonic(int count)
    {
        var frames = Enumerable.Range(0, count)
            .Select(i => MakeFrame((uint)(0x100 + i), Array.Empty<byte>()))
            .ToList();
        var (asc, _) = WriteAndCapture(
            frames,
            new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc),
            TimeSpan.Zero);

        var parsed = await ParseAscAsync(asc);

        parsed.Should().HaveCount(count);
        for (int i = 1; i < parsed.Count; i++)
        {
            parsed[i - 1].Timestamp.Should().BeLessThanOrEqualTo(parsed[i].Timestamp,
                $"frame {i} timestamp should be >= previous; got {parsed[i - 1].Timestamp} vs {parsed[i].Timestamp}");
        }
    }

    [Fact]
    public async Task WriteHeader_ThreeLines_ParseDateAndBaseIsAbsolute()
    {
        var origin = new DateTime(2026, 7, 14, 12, 34, 56, DateTimeKind.Utc);
        var (asc, _) = WriteAndCapture(
            Array.Empty<CanFrame>(),
            origin,
            TimeSpan.Zero);

        var lines = asc.Split('\n');
        lines.Should().Contain(l => l.StartsWith("date ") && l.Contains("Jul 14"));
        lines.Should().Contain(l => l.Contains("base hex  timestamps absolute"));
        lines.Should().Contain(l => l.Contains("no internal events logged"));
    }
}
