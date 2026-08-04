using System.Globalization;
using System.Text;
using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.Infrastructure.Channel;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests;

/// <summary>
/// TDD tests for TraceDrivenChannel (Sprint 2 Inc 1).
/// RED phase: all tests fail initially (implementation not yet written).
/// </summary>
public class TraceDrivenChannelTests
{
    /// <summary>
    /// Helper: write ASC content to a temp file, return the path.
    /// </summary>
    private static string WriteTempAsc(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hil_test_{Guid.NewGuid():N}.asc");
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    /// <summary>Minimal valid ASC with 3 frames.</summary>
    private const string SimpleAsc = @"
date Wed Jun 28 10:00:00.000 2026
base hex  timestamps absolute
internal events logged

 0.000000 1  100  8  11 22 33 44 55 66 77 88
 0.500000 1  200  4  AA BB CC DD
 1.000000 1  100  2  01 02
";

    /// <summary>ASC with an extended frame (ID > 0x7FF).</summary>
    private const string ExtendedFrameAsc = @"
date Wed Jun 28 10:00:00.000 2026
base hex  timestamps absolute

 0.000000 1  18FEF100  8  01 02 03 04 05 06 07 08
 0.100000 1  123  4  AA BB CC DD
";

    [Fact]
    public void LoadAscii_valid_file_populates_frames()
    {
        var path = WriteTempAsc(SimpleAsc);
        try
        {
            var ch = new TraceDrivenChannel(new ChannelId(1));
            ch.LoadAscii(path);
            ch.IsConnected.Should().BeFalse("channel should not be connected after LoadAscii");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAscii_empty_file_sets_playStartTimestamp_negative()
    {
        var path = WriteTempAsc("date Wed Jun 28 10:00:00.000 2026\nbase hex\n");
        try
        {
            var ch = new TraceDrivenChannel(new ChannelId(1));
            ch.LoadAscii(path);
            // ConnectAsync on empty file should throw InvalidOperationException
            await ch.Invoking(async c => await c.ConnectAsync(BaudRate.Can500kbps, false))
                .Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadAscii_nonexistent_file_throws_FileNotFoundException()
    {
        var ch = new TraceDrivenChannel(new ChannelId(1));
        Action act = () => ch.LoadAscii(@"Z:\nonexistent\path\file.asc");
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void LoadAscii_exceeds_MaxTraceFrames_throws()
    {
        var path = WriteTempAsc(SimpleAsc);
        try
        {
            var ch = new TraceDrivenChannel(new ChannelId(1), maxTraceFrames: 2);
            Action act = () => ch.LoadAscii(path);
            act.Should().Throw<InvalidOperationException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAscii_on_Playing_throws_InvalidOperationException()
    {
        var path = WriteTempAsc(SimpleAsc);
        TraceDrivenChannel? ch = null;
        try
        {
            ch = new TraceDrivenChannel(new ChannelId(1));
            ch.LoadAscii(path);
            await ch.ConnectAsync(BaudRate.Can500kbps, false);

            Action act = () => ch.LoadAscii(path);
            act.Should().Throw<InvalidOperationException>();
        }
        finally
        {
            File.Delete(path);
            if (ch is not null) await ch.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConnectAsync_on_Unloaded_throws_InvalidOperationException()
    {
        var ch = new TraceDrivenChannel(new ChannelId(1));
        await ch.Invoking(async c => await c.ConnectAsync(BaudRate.Can500kbps, false))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ConnectAsync_starts_frame_emission()
    {
        var path = WriteTempAsc(SimpleAsc);
        TraceDrivenChannel? ch = null;
        try
        {
            ch = new TraceDrivenChannel(new ChannelId(1));
            ch.LoadAscii(path);

            var frames = new List<CanFrame>();
            ch.FrameReceived += f => frames.Add(f);

            await ch.ConnectAsync(BaudRate.Can500kbps, false);

            // Wait for all 3 frames to be emitted (timeout 5s)
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (frames.Count < 3 && sw.ElapsedMilliseconds < 5000)
                await Task.Delay(50);

            frames.Should().HaveCount(3, "should emit all 3 frames");
        }
        finally
        {
            File.Delete(path);
            if (ch is not null) await ch.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConnectAsync_ignores_baud_and_fd_parameters()
    {
        var path = WriteTempAsc(SimpleAsc);
        TraceDrivenChannel? ch = null;
        try
        {
            ch = new TraceDrivenChannel(new ChannelId(1));
            ch.LoadAscii(path);

            // Should not throw regardless of baud/fd
            await ch.ConnectAsync(BaudRate.Can125kbps, false);
            await ch.DisconnectAsync();

            await ch.ConnectAsync(BaudRate.CanFd5Mbps, true);
        }
        finally
        {
            File.Delete(path);
            if (ch is not null) await ch.DisposeAsync();
        }
    }

    [Fact]
    public async Task FrameReceived_correct_CanFrame_conversion()
    {
        var path = WriteTempAsc(SimpleAsc);
        TraceDrivenChannel? ch = null;
        try
        {
            ch = new TraceDrivenChannel(new ChannelId(1));
            ch.LoadAscii(path);

            var frames = new List<CanFrame>();
            ch.FrameReceived += f => frames.Add(f);

            await ch.ConnectAsync(BaudRate.Can500kbps, false);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (frames.Count < 3 && sw.ElapsedMilliseconds < 5000)
                await Task.Delay(50);

            frames[0].Id.Raw.Should().Be(0x100u);
            frames[0].Data.Span[0].Should().Be(0x11);
            frames[0].Data.Span[1].Should().Be(0x22);
            frames[1].Id.Raw.Should().Be(0x200u);
            frames[1].Data.Span[0].Should().Be(0xAA);
        }
        finally
        {
            File.Delete(path);
            if (ch is not null) await ch.DisposeAsync();
        }
    }

    [Fact]
    public async Task FrameReceived_extended_frame_sets_Format_Extended()
    {
        var path = WriteTempAsc(ExtendedFrameAsc);
        TraceDrivenChannel? ch = null;
        try
        {
            ch = new TraceDrivenChannel(new ChannelId(1));
            ch.LoadAscii(path);

            var frames = new List<CanFrame>();
            ch.FrameReceived += f => frames.Add(f);

            await ch.ConnectAsync(BaudRate.Can500kbps, false);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (frames.Count < 2 && sw.ElapsedMilliseconds < 5000)
                await Task.Delay(50);

            frames[0].Id.IsExtended.Should().BeTrue("ID 0x18FEF100 > 0x7FF should be extended");
            frames[0].Id.Raw.Should().Be(0x18FEF100u);

            frames[1].Id.IsExtended.Should().BeFalse("ID 0x123 should be standard");
        }
        finally
        {
            File.Delete(path);
            if (ch is not null) await ch.DisposeAsync();
        }
    }

    [Fact]
    public async Task FrameReceived_timestamp_converted_to_microseconds()
    {
        var path = WriteTempAsc(SimpleAsc);
        TraceDrivenChannel? ch = null;
        try
        {
            ch = new TraceDrivenChannel(new ChannelId(1));
            ch.LoadAscii(path);

            var frames = new List<CanFrame>();
            ch.FrameReceived += f => frames.Add(f);

            await ch.ConnectAsync(BaudRate.Can500kbps, false);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (frames.Count < 2 && sw.ElapsedMilliseconds < 5000)
                await Task.Delay(50);

            // Frame 0 at t=0.0s -> 0 us
            frames[0].Timestamp.TotalMicroseconds.Should().Be(0UL);
            // Frame 1 at t=0.5s -> 500000 us
            frames[1].Timestamp.TotalMicroseconds.Should().Be(500_000UL);
        }
        finally
        {
            File.Delete(path);
            if (ch is not null) await ch.DisposeAsync();
        }
    }

    [Fact]
    public async Task OnTick_respects_MaxFramesPerTick_batch_limit()
    {
        // Build ASC with 200 frames at same timestamp
        // Use string.Format with InvariantCulture to satisfy CA1305
        var sb = new StringBuilder();
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "date Wed Jun 28 10:00:00.000 2026"));
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "base hex  timestamps absolute"));
        for (int i = 0; i < 200; i++)
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, " 0.000000 1  {0:X}  1  {1:X2}", 0x100 + i, (byte)i));
        var path = WriteTempAsc(sb.ToString());

        TraceDrivenChannel? ch = null;
        try
        {
            ch = new TraceDrivenChannel(new ChannelId(1), maxFramesPerTick: 50);
            ch.LoadAscii(path);

            var frames = new List<CanFrame>();
            ch.FrameReceived += f => frames.Add(f);

            await ch.ConnectAsync(BaudRate.Can500kbps, false);

            // Wait for all 200 frames to be emitted
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (frames.Count < 200 && sw.ElapsedMilliseconds < 10000)
                await Task.Delay(50);

            frames.Count.Should().Be(200, "all 200 frames should eventually be emitted");

            // Verify MaxFramesPerTick was respected: no single tick emitted more than 50
            ch.MaxEmittedPerTick.Should().BeLessThanOrEqualTo(50,
                "no single tick should emit more than MaxFramesPerTick frames");
            ch.MaxEmittedPerTick.Should().BeGreaterThan(0,
                "at least one tick should have emitted frames");
        }
        finally
        {
            File.Delete(path);
            if (ch is not null) await ch.DisposeAsync();
        }
    }

    [Fact]
    public async Task OnTick_stops_timer_when_all_frames_emitted()
    {
        var path = WriteTempAsc(SimpleAsc);
        TraceDrivenChannel? ch = null;
        try
        {
            ch = new TraceDrivenChannel(new ChannelId(1));
            ch.LoadAscii(path);

            var frames = new List<CanFrame>();
            ch.FrameReceived += f => frames.Add(f);

            await ch.ConnectAsync(BaudRate.Can500kbps, false);

            // Wait for all 3 frames
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (frames.Count < 3 && sw.ElapsedMilliseconds < 5000)
                await Task.Delay(50);

            frames.Should().HaveCount(3);

            // Wait a bit more to confirm no extra frames
            await Task.Delay(500);
            frames.Count.Should().Be(3, "should not emit more frames after replay complete");
        }
        finally
        {
            File.Delete(path);
            if (ch is not null) await ch.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_stops_timer_and_prevents_new_callbacks()
    {
        var path = WriteTempAsc(SimpleAsc);
        TraceDrivenChannel? ch = null;
        try
        {
            ch = new TraceDrivenChannel(new ChannelId(1));
            ch.LoadAscii(path);

            var frames = new List<CanFrame>();
            ch.FrameReceived += f => frames.Add(f);

            await ch.ConnectAsync(BaudRate.Can500kbps, false);
            await Task.Delay(100);

            await ch.DisposeAsync();

            var countAfterDispose = frames.Count;
            await Task.Delay(500);
            frames.Count.Should().Be(countAfterDispose, "no frames after Dispose");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DisposeAsync_idempotent()
    {
        var path = WriteTempAsc(SimpleAsc);
        TraceDrivenChannel? ch = null;
        try
        {
            ch = new TraceDrivenChannel(new ChannelId(1));
            ch.LoadAscii(path);

            await ch.DisposeAsync();
            await ch.DisposeAsync(); // Should not throw

            // Channel is disposed; calling DisposeAsync a third time should still be safe
            Assert.True(true, "multiple DisposeAsync calls did not throw");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DisconnectAsync_after_Connect_stops_playback()
    {
        var path = WriteTempAsc(SimpleAsc);
        TraceDrivenChannel? ch = null;
        try
        {
            ch = new TraceDrivenChannel(new ChannelId(1));
            ch.LoadAscii(path);

            var frames = new List<CanFrame>();
            ch.FrameReceived += f => frames.Add(f);

            await ch.ConnectAsync(BaudRate.Can500kbps, false);
            await Task.Delay(100);

            await ch.DisconnectAsync();
            var countAfterDisconnect = frames.Count;

            await Task.Delay(500);
            frames.Count.Should().Be(countAfterDisconnect, "no new frames after DisconnectAsync");
        }
        finally
        {
            File.Delete(path);
            if (ch is not null) await ch.DisposeAsync();
        }
    }

    [Fact]
    public async Task WriteAsync_is_no_op_returns_success()
    {
        var path = WriteTempAsc(SimpleAsc);
        try
        {
            var ch = new TraceDrivenChannel(new ChannelId(1));
            ch.LoadAscii(path);

            var frame = new CanFrame(new CanId(0x100, FrameFormat.Standard),
                new byte[] { 0x01, 0x02 }, FrameFlags.None, new ChannelId(1), new Timestamp(0));

            var result = await ch.WriteAsync(frame);
            result.IsSuccess.Should().BeTrue("WriteAsync should be a no-op returning success");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
