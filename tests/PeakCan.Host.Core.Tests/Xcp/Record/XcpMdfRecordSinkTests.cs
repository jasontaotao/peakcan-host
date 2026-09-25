using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using PeakCan.Host.Core.Xcp.Record;
using PeakCan.Host.Core.Xcp.Receive;
using PeakCan.Host.Core.Xcp.Scheduling;

namespace PeakCan.Host.Core.Tests.Xcp.Record;

/// <summary>
/// S4-T2：自研最小 MF4 写入器（spec D1 T0 裁决）+ 记录 sink 队列纪律。
/// 字节级参照 = asammdf 8.8.27 golden 样本（artifacts/s4-spike-golden.mf4 反推布局）。
/// 布局：每对象一个 DG（DG 链），每 DG 记录 = [time f64 master, value f64]。
/// </summary>
public sealed class Mdf4StreamWriterTests
{
    private static readonly MdfChannelSpec Rpm = new("Rpm", "rpm");
    private static readonly MdfChannelSpec Speed = new("Speed", "km/h");

    private static string TempPath() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"s4w_{Guid.NewGuid():N}.mf4");

    [Fact]
    public async Task Finalized_file_has_valid_id_block_and_block_chain()
    {
        var path = TempPath();
        try
        {
            var start = DateTimeOffset.Parse("2026-09-25T08:00:00Z", CultureInfo.InvariantCulture);
            var writer = Mdf4StreamWriter.Create(path, [Rpm, Speed], start);
            await writer.WriteRecordAsync(0, 0.0, 1.0);
            await writer.WriteRecordAsync(1, 0.0, 10.0);
            await writer.WriteRecordAsync(0, 0.01, 2.0);
            await writer.FinalizeAsync();
            Assert.Equal(3, writer.RecordCount);
            await writer.DisposeAsync();

            var bytes = await File.ReadAllBytesAsync(path);

            // ID 块（64B）：终态签名 + 版本 410（对齐 golden 偏移）。
            Assert.Equal("MDF     ", Encoding.ASCII.GetString(bytes, 0, 8));
            Assert.Equal("4.10    ", Encoding.ASCII.GetString(bytes, 8, 8));
            Assert.Equal(410, BitConverter.ToUInt16(bytes, 0x1C));

            // 块链：ID(64) → HD(104) → 首个 DG(64) → 首个 CG(104)，偏移确定。
            Assert.Equal("##HD"u8.ToArray(), bytes[64..68]);
            Assert.Equal("##DG"u8.ToArray(), bytes[0xA8..0xAC]);
            Assert.Equal("##CG"u8.ToArray(), bytes[0xE8..0xEC]);
            Assert.Equal(0xA8UL, BitConverter.ToUInt64(bytes, 0x58)); // HD.first_dg
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Records_are_patched_into_dt_and_cycles()
    {
        var path = TempPath();
        try
        {
            var writer = Mdf4StreamWriter.Create(path, [Rpm], DateTimeOffset.UtcNow);
            await writer.WriteRecordAsync(0, 0.0, 1.0);
            await writer.WriteRecordAsync(0, 0.5, 2.0);
            await writer.WriteRecordAsync(0, 1.0, 3.0);
            await writer.FinalizeAsync();
            await writer.DisposeAsync();

            var bytes = await File.ReadAllBytesAsync(path);
            var layout = Mdf4Layout.Parse(bytes);

            // 单通道 = 单 DG；条数回写 CG cycles；记录布局 [time, value] = 16B。
            Assert.Equal(1, layout.DataGroupCount);
            Assert.Equal(3, layout.CycleCount);
            Assert.Equal(16, layout.RecordSizeBytes);
            Assert.True(layout.Records.Count == 3, $"count={layout.Records.Count} dg={layout.DataGroupCount} cyc={layout.CycleCount} size={layout.RecordSizeBytes}");
            Assert.Equal((0.0, 1.0), layout.Records[0]);
            Assert.Equal((1.0, 3.0), layout.Records[2]);
            Assert.Equal("Rpm", layout.ValueChannelName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Two_channels_produce_a_two_dg_chain()
    {
        var path = TempPath();
        try
        {
            var writer = Mdf4StreamWriter.Create(path, [Rpm, Speed], DateTimeOffset.UtcNow);
            await writer.WriteRecordAsync(0, 0.0, 1.0);
            await writer.WriteRecordAsync(1, 0.0, 10.0);
            await writer.FinalizeAsync();
            await writer.DisposeAsync();

            var bytes = await File.ReadAllBytesAsync(path);
            var layout = Mdf4Layout.Parse(bytes);
            Assert.Equal(2, layout.DataGroupCount);
            Assert.Equal(1, layout.CycleCount);
            Assert.Equal("Rpm", layout.ValueChannelName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Unfinished_file_carries_unfinmf_signature()
    {
        var path = TempPath();
        try
        {
            var writer = Mdf4StreamWriter.Create(path, [Rpm], DateTimeOffset.UtcNow);
            await writer.WriteRecordAsync(0, 0.0, 1.0);
            await writer.DisposeAsync(); // 未 Finalize 即关：文件保持 UnFinMF（崩溃语义）。

            var bytes = await File.ReadAllBytesAsync(path);
            Assert.Equal("UnFinMF ", Encoding.ASCII.GetString(bytes, 0, 8));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>S4-T2：记录 sink 队列纪律（照 XcpCardPanelSink 先例：不阻塞 / DropOldest / 计数可见）。</summary>
public sealed class XcpMdfRecordSinkTests
{
    private static readonly MdfChannelSpec Rpm = new("Rpm", null);

    private static XcpMdfRecordSink CreateSink(string dir, int capacity = 4096) =>
        new(new XcpMdfRecordSinkOptions
        {
            QueueCapacity = capacity,
            Directory = dir,
            Channels = [Rpm],
        });

    private static PlannedDaqEntry Entry(int i) =>
        new(1, 0, 0, "Rpm", 0, 2, 0, 0x1000, null);

    private static string TempDir()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"s4sink_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Start_stop_records_all_samples_and_reports_counts()
    {
        var dir = TempDir();
        try
        {
            var sink = CreateSink(dir);
            await sink.StartAsync(DateTimeOffset.UtcNow);

            for (var i = 0; i < 50; i++)
                sink.OnValues(new XcpDaqSample(Entry(i), i, DateTimeOffset.UtcNow.AddMilliseconds(i)));

            await sink.StopAsync();

            Assert.Equal(50, sink.WrittenCount);
            Assert.Equal(0, sink.DroppedCount);
            Assert.False(sink.IsFaulted);
            Assert.True(sink.FilePath is not null && File.Exists(sink.FilePath));
            Assert.EndsWith(".mf4", sink.FilePath);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Full_queue_drops_oldest_without_blocking()
    {
        var dir = TempDir();
        try
        {
            var sink = CreateSink(dir, capacity: 4);
            await sink.StartAsync(DateTimeOffset.UtcNow);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 400; i++)
                sink.OnValues(new XcpDaqSample(Entry(i), i, DateTimeOffset.UtcNow));
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 3000, "入队必须不阻塞");
            await sink.StopAsync();
            Assert.True(sink.DroppedCount > 0, "满队列必须可见丢条");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Writer_fault_isolates_and_marks_faulted()
    {
        var dir = TempDir();
        try
        {
            var sink = new XcpMdfRecordSink(new XcpMdfRecordSinkOptions
            {
                QueueCapacity = 64,
                Directory = dir,
                Channels = [Rpm],
                WriterFactory = (_, _) => new ExplodingWriter(),
            });
            await sink.StartAsync(DateTimeOffset.UtcNow);

            for (var i = 0; i < 20; i++)
                sink.OnValues(new XcpDaqSample(Entry(i), i, DateTimeOffset.UtcNow));

            await sink.StopAsync();

            Assert.True(sink.IsFaulted);
            Assert.NotNull(sink.LastError);
            Assert.Equal(0, sink.WrittenCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Idle_sink_noops_without_throw()
    {
        var dir = TempDir();
        try
        {
            var sink = CreateSink(dir);
            sink.OnValues(new XcpDaqSample(Entry(0), 1, DateTimeOffset.UtcNow)); // 未 Start
            await sink.StopAsync(); // 未 Start 的 Stop 幂等
            sink.OnValues(new XcpDaqSample(Entry(0), 1, DateTimeOffset.UtcNow)); // Stop 后
            Assert.Equal(0, sink.WrittenCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Unknown_object_samples_are_counted_not_recorded()
    {
        var dir = TempDir();
        try
        {
            var sink = CreateSink(dir);
            await sink.StartAsync(DateTimeOffset.UtcNow);

            var other = new PlannedDaqEntry(2, 1, 0, "NotWatched", 0, 2, 0, 0x2000, null);
            sink.OnValues(new XcpDaqSample(Entry(0), 1, DateTimeOffset.UtcNow));
            sink.OnValues(new XcpDaqSample(other, 5, DateTimeOffset.UtcNow));

            await sink.StopAsync();
            Assert.Equal(1, sink.WrittenCount);
            Assert.Equal(1, sink.UnknownSampleCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>故障注入：首次写入即炸（记录故障不拖采集——spec D5 的 sink 侧前提）。</summary>
internal sealed class ExplodingWriter : IMdfRecordWriter
{
    public long RecordCount => 0;

    public Task WriteRecordAsync(int channelIndex, double timeSeconds, double value, CancellationToken ct = default) =>
        throw new IOException("disk exploded");

    public Task WriteAttachmentAsync(string mimeType, string comment, ReadOnlyMemory<byte> data, CancellationToken ct = default) =>
        throw new IOException("disk exploded");

    public Task WriteInvalidRecordAsync(int channelIndex, double timeSeconds, CancellationToken ct = default) =>
        throw new IOException("disk exploded");

    public Task WriteGapEventAsync(double timeSeconds, string kind, string cause, string detail,
        string receiveKind, double expectedMaxSeconds, CancellationToken ct = default) =>
        throw new IOException("disk exploded");

    public Task FinalizeAsync(CancellationToken ct = default) => throw new IOException("disk exploded");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>测试侧最小 MDF4 解析（只解析本写入器产出的布局面，作回读断言）。</summary>
internal sealed record Mdf4Layout(
    int DataGroupCount,
    long CycleCount,
    int RecordSizeBytes,
    IReadOnlyList<(double Time, double Value)> Records,
    string ValueChannelName)
{
    public static Mdf4Layout Parse(byte[] b)
    {
        ulong U64(int o) => BitConverter.ToUInt64(b, o);
        uint U32(int o) => BitConverter.ToUInt32(b, o);

        Assert.Equal("##HD", Encoding.ASCII.GetString(b, 64, 4));
        var dg = U64(0x58); // HD.first_dg

        var dgCount = 0;
        long lastCycles = -1;
        int recordSize = -1;
        string name = string.Empty;
        var records = new List<(double, double)>();

        while (dg != 0)
        {
            Assert.Equal("##DG", Encoding.ASCII.GetString(b, (int)dg, 4));
            var cg = U64((int)dg + 32);
            var dl = U64((int)dg + 40);

            // DL → DT 槽位（DL 设计：DG.data 指向 DL，DT 在槽位表）。

            // CG：links @cg+24（next/first_ch/acqn/acqs/sr/cm），数据 @cg+72。
            Assert.Equal("##CG", Encoding.ASCII.GetString(b, (int)cg, 4));
            var firstCh = U64((int)cg + 32);

            // T4 归因事件组（独立 DG、无失效位）不属于样本面，跳过。
            if (U32((int)cg + 100) == 0)
            {
                dg = U64((int)dg + 24);
                continue;
            }

            dgCount++;
            lastCycles = (long)U64((int)cg + 80);
            recordSize = (int)U32((int)cg + 96);

            // 值 CN = time CN 的 next；名在 name link。
            var timeCn = (int)firstCh;
            var valueCn = (int)U64(timeCn + 24); // cn.next
            var nameAddr = U64(valueCn + 40); // cn.name link
            var nameLen = (int)U64((int)nameAddr + 8) - 24;
            if (name.Length == 0)
                name = Encoding.ASCII.GetString(b, (int)nameAddr + 24, nameLen).TrimEnd('\0');

            Assert.Equal("##DL", Encoding.ASCII.GetString(b, (int)dl, 4));
            var dt = U64((int)dl + 32); // 首数据槽位（小数据量单 DT）
            Assert.Equal("##DT", Encoding.ASCII.GetString(b, (int)dt, 4));
            var dtLen = (long)U64((int)dt + 8);
            for (var off = (int)dt + 24; off + 17 <= (int)dt + dtLen; off += 17)
                records.Add((BitConverter.ToDouble(b, off), BitConverter.ToDouble(b, off + 8)));

            dg = U64((int)dg + 24); // dg.next
        }

        return new Mdf4Layout(dgCount, lastCycles, recordSize, records, name);
    }
}


