using A2lEditor.Core.Layout;
using PeakCan.Host.Core.Xcp.Record;
using PeakCan.Host.Core.Xcp.Receive;
using PeakCan.Host.Core.Xcp.Scheduling;

namespace PeakCan.Host.Core.Tests.Xcp.Record;

/// <summary>
/// S4-T6：触发记录 + 环形缓冲（spec D6）。常驻环（采集运行即写环）、
/// 触发 → 环 + 后续流落独立 MF4（xcp_trigger_{ts}）、超 60s 配置拒绝。
/// </summary>
public sealed class XcpTriggerRecordEngineTests
{
    private static readonly MdfChannelSpec Rpm = new("Rpm", "rpm");
    private static readonly MdfChannelSpec Speed = new("Speed", "km/h");

    private static PlannedDaqEntry Entry(string name) =>
        new(1, 0, 0, name, 0, 2, 0, 0x1000, null);

    private static string TempDir()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"s4trig_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Trigger_with_unwritable_directory_faults_and_unlocks_engine()
    {
        // T8 评审 P1-3 回归钉：目录/writer 创建失败必须复位 _capturing 并完成 TCS，
        // 否则引擎永久锁死（后续触发全拒 + 采集 Stop 无限等待）。
        var filePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"s4trig_{Guid.NewGuid():N}.f");
        await System.IO.File.WriteAllTextAsync(filePath, "not a directory");
        try
        {
            var engine = new XcpTriggerRecordEngine(new XcpTriggerRecordOptions
            {
                Directory = filePath, // 用文件路径当目录 → CreateDirectory 抛
                PreTriggerSeconds = 1,
                PostTriggerSeconds = 1,
                EstimatedRatePerSecond = 10,
                Channels = [Rpm],
                WriterFactory = (_, _) => new CapturingWriter(),
            });

            var accepted = await engine.TriggerAsync(DateTimeOffset.Now, "test");
            Assert.True(accepted);
            await engine.WaitCaptureAsync(); // 必须能完成（修复前无限等待）

            Assert.False(engine.IsCapturing, "失败路径必须复位 capturing");
            Assert.True(engine.IsFaulted);
            Assert.NotNull(engine.LastError);

            // 引擎未锁死：可再次触发（这次目录正常）。
            var dir = TempDir();
            try
            {
                var engine2 = engine;
                Assert.True(await engine2.TriggerAsync(DateTimeOffset.Now, "retry"));
                await engine2.CloseCaptureAsync();
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        finally
        {
            System.IO.File.Delete(filePath);
        }
    }

    [Fact]
    public async Task Post_queue_capacity_covers_post_window()
    {
        // T8 评审 P2-2 回归钉：post > pre 时窗口内样本不得因容量 = pre×rate 被丢。
        var dir = TempDir();
        try
        {
            var writerBox = new CapturingWriter[1];
            var engine = new XcpTriggerRecordEngine(new XcpTriggerRecordOptions
            {
                Directory = dir,
                PreTriggerSeconds = 1,
                PostTriggerSeconds = 2,
                EstimatedRatePerSecond = 10, // 旧实现 post 容量 = 10，本测试喂 15 条 post
                Channels = [Rpm],
                WriterFactory = (_, _) => writerBox[0] = new CapturingWriter(),
            });

            var triggerAt = DateTimeOffset.Now;
            Assert.True(await engine.TriggerAsync(triggerAt, "test"));
            for (var i = 0; i < 15; i++)
                engine.OnValues(new XcpDaqSample(Entry("Rpm"), i, triggerAt.AddMilliseconds(100 + i * 100)));
            await engine.CloseCaptureAsync();

            Assert.NotNull(writerBox[0]);
            Assert.Equal(15, writerBox[0]!.Records.Count); // 窗口内 15 条全落盘
            Assert.Equal(0, engine.PostDroppedCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Ring_snapshot_is_trimmed_to_pre_window_by_time()
    {
        // T8 评审 P2-3 回归钉：实际条率低于估计时环内是远超 N 秒的数据，
        // 触发文件前窗必须按时间裁剪（无过期样本、无负相对时间）。
        var dir = TempDir();
        try
        {
            var writerBox = new CapturingWriter[1];
            var engine = new XcpTriggerRecordEngine(new XcpTriggerRecordOptions
            {
                Directory = dir,
                PreTriggerSeconds = 2,
                PostTriggerSeconds = 1,
                EstimatedRatePerSecond = 10, // 容量 20 远大于本测试条数 → 低条率方向
                Channels = [Rpm],
                WriterFactory = (_, _) => writerBox[0] = new CapturingWriter(),
            });

            var triggerAt = DateTimeOffset.Now;
            engine.OnValues(new XcpDaqSample(Entry("Rpm"), 1.0, triggerAt.AddSeconds(-10))); // 过期
            engine.OnValues(new XcpDaqSample(Entry("Rpm"), 2.0, triggerAt.AddSeconds(-8)));  // 过期
            engine.OnValues(new XcpDaqSample(Entry("Rpm"), 3.0, triggerAt.AddSeconds(-1)));  // 窗内
            engine.OnValues(new XcpDaqSample(Entry("Rpm"), 4.0, triggerAt.AddSeconds(-0.5))); // 窗内

            Assert.True(await engine.TriggerAsync(triggerAt, "test"));
            await engine.CloseCaptureAsync();

            Assert.NotNull(writerBox[0]);
            var times = writerBox[0]!.Records.Select(r => r.TimeSeconds).ToArray();
            double[] expected = [1.0, 1.5];
            Assert.Equal(expected, times); // fileStart = trigger - 2
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Options_reject_pre_and_post_out_of_range()
    {
        // Q2：60 s 硬顶；1–60 可配，越界构造即拒（配置错误 fail-fast）。
        Assert.Throws<ArgumentOutOfRangeException>(() => new XcpTriggerRecordEngine(new XcpTriggerRecordOptions { PreTriggerSeconds = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new XcpTriggerRecordEngine(new XcpTriggerRecordOptions { PreTriggerSeconds = 61 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new XcpTriggerRecordEngine(new XcpTriggerRecordOptions { PostTriggerSeconds = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new XcpTriggerRecordEngine(new XcpTriggerRecordOptions { PostTriggerSeconds = 61 }));
    }

    [Fact]
    public void TrySetWindows_rejects_out_of_range_and_keeps_values()
    {
        var engine = new XcpTriggerRecordEngine(new XcpTriggerRecordOptions { PreTriggerSeconds = 10, PostTriggerSeconds = 10 });
        Assert.False(engine.TrySetWindows(0, 10));
        Assert.False(engine.TrySetWindows(61, 10));
        Assert.False(engine.TrySetWindows(10, 0));
        Assert.False(engine.TrySetWindows(10, 61));
        Assert.Equal(10, engine.PreTriggerSeconds);
        Assert.Equal(10, engine.PostTriggerSeconds);

        Assert.True(engine.TrySetWindows(5, 20));
        Assert.Equal(5, engine.PreTriggerSeconds);
        Assert.Equal(20, engine.PostTriggerSeconds);
    }

    [Fact]
    public async Task TrySetWindows_rejects_while_capturing()
    {
        var dir = TempDir();
        try
        {
            var engine = new XcpTriggerRecordEngine(new XcpTriggerRecordOptions
            {
                Directory = dir,
                PreTriggerSeconds = 1,
                PostTriggerSeconds = 60,
                EstimatedRatePerSecond = 100,
                Channels = [Rpm],
                WriterFactory = (_, _) => new CapturingWriter(),
            });

            Assert.True(await engine.TriggerAsync(DateTimeOffset.Now, "test"));
            Assert.False(engine.TrySetWindows(5, 5));
            await engine.CloseCaptureAsync();
            Assert.True(engine.TrySetWindows(5, 5));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TrySetWindows_trims_ring_with_dropped_count()
    {
        // 容量 = pre × rate：pre=2、rate=10 → 20 条；缩到 pre=1 → 10 条，最旧 10 条挤出可见。
        var engine = new XcpTriggerRecordEngine(new XcpTriggerRecordOptions
        {
            PreTriggerSeconds = 2,
            PostTriggerSeconds = 1,
            EstimatedRatePerSecond = 10,
            Channels = [Rpm],
        });

        var now = DateTimeOffset.Now;
        for (var i = 0; i < 20; i++)
            engine.OnValues(new XcpDaqSample(Entry("Rpm"), i, now.AddMilliseconds(-2000 + i * 50)));
        Assert.Equal(0, engine.RingDroppedCount);

        Assert.True(engine.TrySetWindows(1, 1));
        Assert.Equal(10, engine.RingDroppedCount);
    }

    [Fact]
    public async Task Trigger_writes_ring_then_post_stream_to_trigger_file()
    {
        var dir = TempDir();
        try
        {
            CapturingWriter? writer = null;
            string? createdPath = null;
            var engine = new XcpTriggerRecordEngine(new XcpTriggerRecordOptions
            {
                Directory = dir,
                PreTriggerSeconds = 2,
                PostTriggerSeconds = 1,
                EstimatedRatePerSecond = 100,
                Channels = [Rpm, Speed],
                WriterFactory = (p, ch) =>
                {
                    createdPath = p;
                    writer = new CapturingWriter();
                    return writer;
                },
            });

            // 环内容（触发前 2s 窗口内）：t = -1.5、-0.5、-0.1
            var triggerAt = DateTimeOffset.Now;
            engine.OnValues(new XcpDaqSample(Entry("Rpm"), 1.0, triggerAt.AddSeconds(-1.5)));
            engine.OnValues(new XcpDaqSample(Entry("Rpm"), 2.0, triggerAt.AddSeconds(-0.5)));
            engine.OnValues(new XcpDaqSample(Entry("Speed"), 30.0, triggerAt.AddSeconds(-0.1)));

            Assert.True(await engine.TriggerAsync(triggerAt, "test"));
            Assert.True(engine.IsCapturing);

            // 后续流：post 窗口（1s）内 t=0.2、0.8 写入；t=1.0（≥ 窗口终点）关窗不入文件。
            engine.OnValues(new XcpDaqSample(Entry("Rpm"), 3.0, triggerAt.AddSeconds(0.2)));
            engine.OnValues(new XcpDaqSample(Entry("Rpm"), 4.0, triggerAt.AddSeconds(0.8)));
            engine.OnValues(new XcpDaqSample(Entry("Rpm"), 5.0, triggerAt.AddSeconds(1.0)));
            await engine.WaitCaptureAsync();

            Assert.False(engine.IsCapturing);
            Assert.Equal(1, engine.CaptureCount);
            Assert.NotNull(createdPath);
            Assert.Contains("xcp_trigger_", System.IO.Path.GetFileName(createdPath!));
            Assert.EndsWith(".mf4", createdPath);
            Assert.NotNull(writer);

            // 文件起点 = triggerAt - pre；环项在前（时间升序 = 到达序），后续流在后；关窗条不入。
            var times = writer!.Records.Select(r => r.TimeSeconds).ToArray();
            double[] expectedTimes = [0.5, 1.5, 1.9, 2.2, 2.8];
            double[] expectedValues = [1.0, 2.0, 30.0, 3.0, 4.0];
            Assert.Equal(expectedTimes, times);
            Assert.Equal(expectedValues, writer.Records.Select(r => r.Value).ToArray());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Ring_drops_oldest_and_counts()
    {
        var dir = TempDir();
        try
        {
            var writerBox = new CapturingWriter[1];
            var engine = new XcpTriggerRecordEngine(new XcpTriggerRecordOptions
            {
                Directory = dir,
                PreTriggerSeconds = 1,
                PostTriggerSeconds = 1,
                EstimatedRatePerSecond = 10, // 容量 = 10 条
                Channels = [Rpm],
                WriterFactory = (_, _) => writerBox[0] = new CapturingWriter(),
            });

            var triggerAt = DateTimeOffset.Now;
            for (var i = 0; i < 15; i++)
                engine.OnValues(new XcpDaqSample(Entry("Rpm"), i, triggerAt.AddMilliseconds(-1000 + i * 50)));

            Assert.Equal(5, engine.RingDroppedCount); // 15 - 10 = 5 条最旧被挤掉，计数可见

            Assert.True(await engine.TriggerAsync(triggerAt, "test"));
            await engine.CloseCaptureAsync();
            Assert.NotNull(writerBox[0]);
            Assert.Equal(10, writerBox[0]!.Records.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Gap_in_ring_becomes_event_and_invalid_rows()
    {
        var dir = TempDir();
        try
        {
            var writerBox = new CapturingWriter[1];
            var engine = new XcpTriggerRecordEngine(new XcpTriggerRecordOptions
            {
                Directory = dir,
                PreTriggerSeconds = 2,
                PostTriggerSeconds = 1,
                EstimatedRatePerSecond = 100,
                Channels = [Rpm, Speed],
                WriterFactory = (_, _) => writerBox[0] = new CapturingWriter(),
            });

            var triggerAt = DateTimeOffset.Now;
            engine.OnValues(new XcpDaqSample(Entry("Rpm"), 1.0, triggerAt.AddSeconds(-1.0)));
            engine.OnGap(new XcpAcquisitionGap(XcpAcquisitionGapKind.PlanGapOpened, "plan gap", ExpectedMaxDuration: TimeSpan.FromSeconds(2)));
            engine.OnValues(new XcpDaqSample(Entry("Rpm"), 2.0, triggerAt.AddSeconds(-0.5)));

            Assert.True(await engine.TriggerAsync(triggerAt, "test"));
            await engine.CloseCaptureAsync();

            Assert.NotNull(writerBox[0]);
            var w = writerBox[0]!;
            Assert.Single(w.Gaps);
            Assert.Equal(2, w.InvalidRecords.Count); // 会话级归因：每个样本通道一条失效行
            Assert.Equal(2, w.Records.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Second_trigger_while_capturing_is_rejected_and_counted()
    {
        var dir = TempDir();
        try
        {
            var engine = new XcpTriggerRecordEngine(new XcpTriggerRecordOptions
            {
                Directory = dir,
                PreTriggerSeconds = 1,
                PostTriggerSeconds = 60, // 长窗口保持 capturing 状态确定
                EstimatedRatePerSecond = 100,
                Channels = [Rpm],
                WriterFactory = (_, _) => new CapturingWriter(),
            });

            var triggerAt = DateTimeOffset.Now;
            Assert.True(await engine.TriggerAsync(triggerAt, "first"));
            Assert.False(await engine.TriggerAsync(triggerAt.AddSeconds(1), "second"));
            Assert.Equal(1, engine.RejectedTriggerCount);

            await engine.CloseCaptureAsync();
            await engine.WaitCaptureAsync();
            Assert.False(engine.IsCapturing);
            Assert.True(await engine.TriggerAsync(triggerAt.AddSeconds(2), "third")); // 关窗后可再触发
            await engine.CloseCaptureAsync();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Trigger_snapshot_attachment_written_on_capture()
    {
        // D2：触发文件同样自包含（快照经工厂在触发时刻导出）。
        var dir = TempDir();
        try
        {
            var writerBox = new CapturingWriter[1];
            var engine = new XcpTriggerRecordEngine(new XcpTriggerRecordOptions
            {
                Directory = dir,
                PreTriggerSeconds = 1,
                PostTriggerSeconds = 1,
                EstimatedRatePerSecond = 10,
                Channels = [Rpm],
                WriterFactory = (_, _) => writerBox[0] = new CapturingWriter(),
                SnapshotFactory = () => new ContractSnapshot(1, "sha", new A2lFingerprint(0, 0, 0, 0, 0), "test", []),
            });

            var triggerAt = DateTimeOffset.Now;
            Assert.True(await engine.TriggerAsync(triggerAt, "test"));
            await engine.CloseCaptureAsync();

            Assert.NotNull(writerBox[0]);
            Assert.Single(writerBox[0]!.Attachments);
            Assert.Equal("application/json", writerBox[0]!.Attachments[0].MimeType);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Unknown_object_samples_in_ring_are_counted_not_written()
    {
        var dir = TempDir();
        try
        {
            var writerBox = new CapturingWriter[1];
            var engine = new XcpTriggerRecordEngine(new XcpTriggerRecordOptions
            {
                Directory = dir,
                PreTriggerSeconds = 1,
                PostTriggerSeconds = 1,
                EstimatedRatePerSecond = 10,
                Channels = [Rpm],
                WriterFactory = (_, _) => writerBox[0] = new CapturingWriter(),
            });

            var triggerAt = DateTimeOffset.Now;
            engine.OnValues(new XcpDaqSample(Entry("Rpm"), 1.0, triggerAt.AddSeconds(-0.5)));
            engine.OnValues(new XcpDaqSample(Entry("NotWatched"), 9.0, triggerAt.AddSeconds(-0.4)));

            Assert.True(await engine.TriggerAsync(triggerAt, "test"));
            await engine.CloseCaptureAsync();

            Assert.Equal(1, engine.UnknownSampleCount);
            Assert.NotNull(writerBox[0]);
            Assert.Single(writerBox[0]!.Records);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>T6 捕获替身：记录全部调用面（测试断言用，不出盘）。</summary>
internal sealed class CapturingWriter : IMdfRecordWriter
{
    public List<(int ChannelIndex, double TimeSeconds, double Value)> Records { get; } = [];
    public List<(int ChannelIndex, double TimeSeconds)> InvalidRecords { get; } = [];
    public List<(double TimeSeconds, string Kind, string Cause, string Detail, string ReceiveKind, double ExpectedMax)> Gaps { get; } = [];
    public List<(string MimeType, string Comment)> Attachments { get; } = [];
    public bool Finalized { get; private set; }

    public long RecordCount => Records.Count + InvalidRecords.Count;

    public Task WriteRecordAsync(int channelIndex, double timeSeconds, double value, CancellationToken ct = default)
    {
        Records.Add((channelIndex, timeSeconds, value));
        return Task.CompletedTask;
    }

    public Task WriteInvalidRecordAsync(int channelIndex, double timeSeconds, CancellationToken ct = default)
    {
        InvalidRecords.Add((channelIndex, timeSeconds));
        return Task.CompletedTask;
    }

    public Task WriteGapEventAsync(double timeSeconds, string kind, string cause, string detail,
        string receiveKind, double expectedMaxSeconds, CancellationToken ct = default)
    {
        Gaps.Add((timeSeconds, kind, cause, detail, receiveKind, expectedMaxSeconds));
        return Task.CompletedTask;
    }

    public Task WriteAttachmentAsync(string mimeType, string comment, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        Attachments.Add((mimeType, comment));
        return Task.CompletedTask;
    }

    public Task FinalizeAsync(CancellationToken ct = default)
    {
        Finalized = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
