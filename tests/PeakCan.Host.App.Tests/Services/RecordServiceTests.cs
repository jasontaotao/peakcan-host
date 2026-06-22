using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.Core;
using Xunit;

namespace PeakCan.Host.App.Tests.Services;

/// <summary>
/// Verifies <see cref="RecordService"/> start/stop lifecycle, frame
/// writing, and format output.
/// </summary>
public class RecordServiceTests : IDisposable
{
    private readonly RecordService _svc;
    private readonly string _tempDir;

    public RecordServiceTests()
    {
        _svc = new RecordService(NullLogger<RecordService>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), $"peakcan-record-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _svc.Dispose();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
        GC.SuppressFinalize(this);
    }

    private string TempFile(string name) => Path.Combine(_tempDir, name);

    private static CanFrame MakeFrame(uint id = 0x123, byte[]? data = null)
        => new(new CanId(id, FrameFormat.Standard),
               data ?? new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
               FrameFlags.None,
               new ChannelId(0x51),
               Timestamp.FromMicroseconds(1_000_000UL));

    [Fact]
    public void IsRecording_False_By_Default()
    {
        _svc.IsRecording.Should().BeFalse();
    }

    [Fact]
    public void StartRecording_Sets_IsRecording_True()
    {
        _svc.StartRecording(TempFile("test.csv"), RecordService.RecordFormat.Csv);
        _svc.IsRecording.Should().BeTrue();
    }

    [Fact]
    public void StopRecording_Sets_IsRecording_False()
    {
        _svc.StartRecording(TempFile("test.csv"), RecordService.RecordFormat.Csv);
        _svc.StopRecording();
        _svc.IsRecording.Should().BeFalse();
    }

    [Fact]
    public void StopRecording_Is_Idempotent()
    {
        _svc.StartRecording(TempFile("test.csv"), RecordService.RecordFormat.Csv);
        _svc.StopRecording();
        _svc.StopRecording(); // must not throw
        _svc.IsRecording.Should().BeFalse();
    }

    [Fact]
    public void OnFrame_Increments_FrameCount()
    {
        _svc.StartRecording(TempFile("test.csv"), RecordService.RecordFormat.Csv);
        _svc.OnFrame(MakeFrame());
        _svc.OnFrame(MakeFrame());
        _svc.FrameCount.Should().Be(2);
    }

    [Fact]
    public void Csv_Header_Is_Written()
    {
        var path = TempFile("test.csv");
        _svc.StartRecording(path, RecordService.RecordFormat.Csv);
        _svc.StopRecording();

        var lines = File.ReadAllLines(path);
        lines[0].Should().Be("timestamp,channel,id,dlc,data,flags");
    }

    [Fact]
    public void Asc_Header_Is_Written()
    {
        var path = TempFile("test.asc");
        _svc.StartRecording(path, RecordService.RecordFormat.Asc);
        _svc.StopRecording();

        var lines = File.ReadAllLines(path);
        lines[0].Should().StartWith("date ");
        lines[1].Should().Be("base hex  timestamps absolute");
    }

    [Fact]
    public void Csv_Frame_Is_Written()
    {
        var path = TempFile("test.csv");
        _svc.StartRecording(path, RecordService.RecordFormat.Csv);
        _svc.OnFrame(MakeFrame(0x100, new byte[] { 0x01, 0x02 }));
        _svc.StopRecording();

        var lines = File.ReadAllLines(path);
        lines.Length.Should().Be(2); // header + 1 frame
        lines[1].Should().Contain(",51,0x100,2,0102,");
    }

    [Fact]
    public void Asc_Frame_Is_Written()
    {
        var path = TempFile("test.asc");
        _svc.StartRecording(path, RecordService.RecordFormat.Asc);
        _svc.OnFrame(MakeFrame(0x100, new byte[] { 0x01, 0x02 }));
        _svc.StopRecording();

        var lines = File.ReadAllLines(path);
        // header (3 lines) + 1 frame + footer (2 lines)
        lines.Should().Contain(l => l.Contains("100") && l.Contains("0102"));
    }

    [Fact]
    public void StartRecording_Stops_Previous_Recording()
    {
        var path1 = TempFile("first.csv");
        var path2 = TempFile("second.csv");
        _svc.StartRecording(path1, RecordService.RecordFormat.Csv);
        _svc.OnFrame(MakeFrame());
        _svc.StartRecording(path2, RecordService.RecordFormat.Csv);
        _svc.OnFrame(MakeFrame());
        _svc.StopRecording();

        // First file should have 1 frame, second should have 1 frame.
        File.ReadAllLines(path1).Length.Should().Be(2); // header + 1
        File.ReadAllLines(path2).Length.Should().Be(2); // header + 1
    }

    [Fact]
    public void OnFrame_When_Not_Recording_Is_NoOp()
    {
        _svc.OnFrame(MakeFrame()); // must not throw
        _svc.FrameCount.Should().Be(0);
    }
}
