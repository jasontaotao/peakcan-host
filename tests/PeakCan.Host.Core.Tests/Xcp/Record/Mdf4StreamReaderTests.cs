using System.Text;
using PeakCan.Host.Core.Xcp.Record;

namespace PeakCan.Host.Core.Tests.Xcp.Record;

/// <summary>
/// S6-T1：MDF4 读取器（spec D1 定案——自研最小 reader，只覆盖自家 writer 写面）。
/// round-trip 钉：S4 writer 产文件 → reader → 通道/样本/空窗/附件与写入面一致。
/// </summary>
public sealed class Mdf4StreamReaderTests
{
    private static readonly MdfChannelSpec Rpm = new("EngineSpeed", "rpm");
    private static readonly MdfChannelSpec Speed = new("VehicleSpeed", "km/h");

    private static string TempPath() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"s6r_{Guid.NewGuid():N}.mf4");

    private static readonly DateTimeOffset Start = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Round_trip_two_channels_matches_writer_surface()
    {
        var path = TempPath();
        try
        {
            var writer = Mdf4StreamWriter.Create(path, [Rpm, Speed], Start);
            await writer.WriteRecordAsync(0, 0.0, 100.0);
            await writer.WriteRecordAsync(0, 0.1, 200.0);
            await writer.WriteRecordAsync(1, 0.0, 50.0);
            await writer.WriteRecordAsync(1, 0.05, 60.0);
            await writer.FinalizeAsync();
            await writer.DisposeAsync();

            var file = Mdf4StreamReader.Read(path);

            Assert.True(file.IsFinalized);
            Assert.Equal(Start, file.StartTimeUtc);
            Assert.Equal(2, file.Channels.Count);

            var rpm = file.Channels[0];
            Assert.Equal("EngineSpeed", rpm.Name);
            Assert.Equal("rpm", rpm.Unit);
            Assert.Equal(2, rpm.Samples.Count);
            Assert.Equal((0.0, 100.0, false), (rpm.Samples[0].Time, rpm.Samples[0].Value, rpm.Samples[0].Invalid));
            Assert.Equal((0.1, 200.0, false), (rpm.Samples[1].Time, rpm.Samples[1].Value, rpm.Samples[1].Invalid));

            var speed = file.Channels[1];
            Assert.Equal("VehicleSpeed", speed.Name);
            Assert.Equal("km/h", speed.Unit);
            Assert.Equal(2, speed.Samples.Count);
            Assert.Equal(60.0, speed.Samples[1].Value);
            Assert.Empty(file.GapEvents);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task Invalid_rows_keep_raw_value_and_flag()
    {
        var path = TempPath();
        try
        {
            var writer = Mdf4StreamWriter.Create(path, [Rpm], Start);
            await writer.WriteRecordAsync(0, 0.0, 1.0);
            await writer.WriteInvalidRecordAsync(0, 0.5);
            await writer.WriteRecordAsync(0, 1.0, 3.0);
            await writer.FinalizeAsync();
            await writer.DisposeAsync();

            var file = Mdf4StreamReader.Read(path);

            var samples = file.Channels[0].Samples;
            Assert.Equal(3, samples.Count);
            Assert.False(samples[0].Invalid);
            Assert.True(samples[1].Invalid);   // 空窗行失效（raw NaN 不可当数据）
            Assert.False(samples[2].Invalid);
            Assert.Equal(3.0, samples[2].Value);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task Gap_events_extracted_with_sd_strings()
    {
        var path = TempPath();
        try
        {
            var writer = Mdf4StreamWriter.Create(path, [Rpm], Start);
            await writer.WriteGapEventAsync(1.5, "MissingCauseAttributed",
                "ConversionUnsupported", "cvt MAP2 unsupported", "MalformedFrame", 0.0);
            await writer.WriteGapEventAsync(3.0, "PlanGapOpened", "", "plan gap", "", 0.25);
            await writer.FinalizeAsync();
            await writer.DisposeAsync();

            var file = Mdf4StreamReader.Read(path);

            Assert.Equal(2, file.GapEvents.Count);
            var e0 = file.GapEvents[0];
            Assert.Equal(1.5, e0.Time);
            Assert.Equal("MissingCauseAttributed", e0.Kind);
            Assert.Equal("ConversionUnsupported", e0.Cause);
            Assert.Equal("cvt MAP2 unsupported", e0.Detail);
            Assert.Equal("MalformedFrame", e0.ReceiveKind);
            Assert.Equal(0.0, e0.ExpectedMaxSeconds);
            Assert.Equal(0.25, file.GapEvents[1].ExpectedMaxSeconds);
            Assert.Single(file.Channels); // 事件组不进通道列表
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task Attachment_round_trip_with_integrity()
    {
        var path = TempPath();
        try
        {
            var payload = Encoding.UTF8.GetBytes("""{"schemaVersion":1}""");
            var writer = Mdf4StreamWriter.Create(path, [Rpm], Start);
            await writer.WriteAttachmentAsync("application/json", "contract snapshot", payload);
            await writer.FinalizeAsync();
            await writer.DisposeAsync();

            var file = Mdf4StreamReader.Read(path);

            var at = Assert.Single(file.Attachments);
            Assert.Equal("application/json", at.MimeType);
            Assert.Equal("contract snapshot", at.Comment);
            Assert.Equal(payload, at.Data);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task Unfinalized_file_is_salvageable_with_flag()
    {
        var path = TempPath();
        try
        {
            var writer = Mdf4StreamWriter.Create(path, [Rpm], Start);
            await writer.WriteRecordAsync(0, 0.0, 42.0);
            await writer.DisposeAsync(); // 崩溃语义：不 Finalize

            var file = Mdf4StreamReader.Read(path);

            Assert.False(file.IsFinalized);
            var s = Assert.Single(file.Channels[0].Samples);
            Assert.Equal(42.0, s.Value);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task Unknown_block_magic_fails_loudly()
    {
        var path = TempPath();
        try
        {
            var writer = Mdf4StreamWriter.Create(path, [Rpm], Start);
            await writer.WriteRecordAsync(0, 0.0, 1.0);
            await writer.FinalizeAsync();
            await writer.DisposeAsync();

            var bytes = await System.IO.File.ReadAllBytesAsync(path);
            // 首个 DG 位于 ID(64)+HD(104) = 168（writer ComputeFirstDataGroupOffset 固定）。
            Encoding.ASCII.GetBytes("##ZZ").CopyTo(bytes, 168);
            var tampered = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"s6r_{Guid.NewGuid():N}.mf4");
            await System.IO.File.WriteAllBytesAsync(tampered, bytes);
            try
            {
                var ex = Assert.Throws<FormatException>(() => { _ = Mdf4StreamReader.Read(tampered); });
                Assert.Contains("##ZZ", ex.Message);
            }
            finally
            {
                System.IO.File.Delete(tampered);
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task Multi_dt_spanning_channel_reads_all_slots()
    {
        var path = TempPath();
        try
        {
            var writer = Mdf4StreamWriter.Create(path, [Rpm], Start);
            // 1M 条 × 17B = 17 MB > 16 MB 单 DT 目标 → 跨 2 个 DT 槽位（DL 链路关键路径）。
            const int n = 1_000_000;
            for (var i = 0; i < n; i++)
                await writer.WriteRecordAsync(0, i * 0.001, i);
            await writer.FinalizeAsync();
            await writer.DisposeAsync();

            var file = Mdf4StreamReader.Read(path);

            var samples = file.Channels[0].Samples;
            Assert.Equal(n, samples.Count);
            Assert.Equal(0.0, samples[0].Time);
            Assert.Equal(n - 1, samples[^1].Value);
            Assert.Equal((n - 1) * 0.001, samples[^1].Time, 9);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}
