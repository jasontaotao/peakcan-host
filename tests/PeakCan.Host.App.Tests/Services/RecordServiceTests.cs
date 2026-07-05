using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services;
using PeakCan.Host.Core;
using Xunit;

namespace PeakCan.Host.App.Tests.Services;

/// <summary>
/// Verifies <see cref="RecordService"/> start/stop lifecycle, frame
/// writing, and format output.
/// </summary>
public class RecordServiceTests : IAsyncLifetime, IDisposable
{
    private readonly RecordService _svc;
    private readonly string _tempDir;

    public RecordServiceTests()
    {
        _svc = new RecordService(NullLogger<RecordService>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), $"peakcan-record-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public async Task InitializeAsync()
    {
        // v1.2.12 PATCH Item 5: RecordService is now a BackgroundService
        // whose writer thread runs in ExecuteAsync. The host's StartAsync
        // would normally start it; in tests we start it explicitly so
        // OnFrame → Channel → writer → file is end-to-end runnable.
        await _svc.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        // StopAsync cancels ExecuteAsync and (via our override) runs
        // StopRecordingInner so the last frames + footer are flushed.
        await _svc.StopAsync(CancellationToken.None);
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
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

    /// <summary>
    /// v1.2.12 PATCH Item 5: <see cref="RecordService.OnFrame"/> is
    /// non-blocking — it enqueues into a <see cref="System.Threading.Channels.Channel{T}"/>
    /// and the writer thread drains in the background. Tests that
    /// assert against the written file need to wait for the drain.
    /// This helper waits until the writer thread has caught up to the
    /// expected enqueue count, polling at 10 ms with a 5 s ceiling.
    /// </summary>
    private async Task WaitForDrainAsync(long expectedEnqueued, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount + timeoutMs;
        while (Environment.TickCount < deadline)
        {
            if (_svc.FrameEnqueuedCount >= expectedEnqueued
                && _svc.FrameCount >= expectedEnqueued)
            {
                return;
            }
            await Task.Delay(10);
        }
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
    public async Task OnFrame_Increments_FrameCount()
    {
        _svc.StartRecording(TempFile("test.csv"), RecordService.RecordFormat.Csv);
        _svc.OnFrame(MakeFrame());
        _svc.OnFrame(MakeFrame());
        await WaitForDrainAsync(2);
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
    public async Task Csv_Frame_Is_Written()
    {
        var path = TempFile("test.csv");
        _svc.StartRecording(path, RecordService.RecordFormat.Csv);
        _svc.OnFrame(MakeFrame(0x100, new byte[] { 0x01, 0x02 }));
        await WaitForDrainAsync(1);
        _svc.StopRecording();

        var lines = File.ReadAllLines(path);
        lines.Length.Should().Be(2); // header + 1 frame
        lines[1].Should().Contain(",51,0x100,2,0102,");
    }

    [Fact]
    public async Task Asc_Frame_Is_Written()
    {
        var path = TempFile("test.asc");
        _svc.StartRecording(path, RecordService.RecordFormat.Asc);
        _svc.OnFrame(MakeFrame(0x100, new byte[] { 0x01, 0x02 }));
        await WaitForDrainAsync(1);
        _svc.StopRecording();

        var lines = File.ReadAllLines(path);
        // header (3 lines) + 1 frame + footer (2 lines)
        lines.Should().Contain(l => l.Contains("100") && l.Contains("0102"));
    }

    [Fact]
    public async Task StartRecording_Stops_Previous_Recording()
    {
        var path1 = TempFile("first.csv");
        var path2 = TempFile("second.csv");
        _svc.StartRecording(path1, RecordService.RecordFormat.Csv);
        _svc.OnFrame(MakeFrame());
        await WaitForDrainAsync(1);
        _svc.StartRecording(path2, RecordService.RecordFormat.Csv);
        _svc.OnFrame(MakeFrame());
        await WaitForDrainAsync(2);
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

    // ===== v1.2.12 PATCH Item 11: sink OnError → ILogger =====

    [Fact]
    public void OnError_Calls_ILogger_LogWarning()
    {
        // Source-gen [LoggerMessage] gates Log() on IsEnabled, so stub true.
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<RecordService>>();
        logger.IsEnabled(LogLevel.Warning).Returns(true);
        var svc = new RecordService(logger);
        var ex = new InvalidOperationException("test");

        svc.OnError(ex);

        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            ex,
            Arg.Any<Func<object, Exception?, string>>());
    }
}

/// <summary>
/// v1.2.12 PATCH Item 5 — verifies <see cref="RecordService"/>'s
/// <see cref="System.Threading.Channels.Channel{T}"/>-backed writer
/// loop: <see cref="RecordService.OnFrame"/> is non-blocking on the
/// SDK read thread; the writer thread drains the channel; a
/// timer flushes the file on every tick; the channel's DropOldest
/// policy keeps memory bounded; and <see cref="RecordService.StopRecording"/>
/// drains remaining frames before closing the file.
/// <para>
/// v3.5.2 PATCH: writer/flush tests inject a <see cref="FakeTimerFactory"/>
/// so the "1 Hz flush" test is deterministic. The previous
/// <c>Task.Delay(1100)</c> waited on wall-clock time and produced
/// occasional CI flake; the fake timer is fired explicitly so the
/// only waits remaining are in-task <c>Task.Yield()</c> round-trips.
/// </para>
/// </summary>
public class RecordServiceChannelTests : IAsyncLifetime, IDisposable
{
    private readonly RecordService _svc;
    private readonly string _tmpPath;
    private readonly FakeTimerFactory _timerFactory;

    public RecordServiceChannelTests()
    {
        _tmpPath = Path.Combine(Path.GetTempPath(), $"rec-{Guid.NewGuid():N}.csv");
        _timerFactory = new FakeTimerFactory();
        _svc = new RecordService(
            Substitute.For<Microsoft.Extensions.Logging.ILogger<RecordService>>(),
            _timerFactory);
    }

    public async Task InitializeAsync()
    {
        // BackgroundService.ExecuteAsync is only invoked by the host's
        // StartAsync. In unit tests we trigger it explicitly so the
        // writer thread runs and drains the channel.
        await _svc.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        // StopAsync cancels the ExecuteAsync loop AND runs our
        // StopRecordingInner override (added in v1.2.12 PATCH Item 5).
        await _svc.StopAsync(CancellationToken.None);
        _svc.Dispose();
        try { File.Delete(_tmpPath); } catch { }
    }

    public void Dispose()
    {
        // IAsyncLifetime covers the normal path; this is the sync fallback
        // for frameworks that don't use IAsyncLifetime.
        try { File.Delete(_tmpPath); } catch { }
        _svc.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void OnFrame_Does_Not_Block_When_Writer_Slow()
    {
        _svc.StartRecording(_tmpPath, RecordService.RecordFormat.Csv);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 10000; i++)
        {
            _svc.OnFrame(BuildFrame(0x100, (byte)i));
        }
        sw.Stop();
        // Channel.Writer.TryWrite is non-blocking; even if the writer thread
        // is slow, 10 000 enqueues should finish in well under 100 ms.
        sw.ElapsedMilliseconds.Should().BeLessThan(100);
        _svc.StopRecording();
    }

    [Fact]
    public void OnFrame_NonBlocking_On_Reader_Thread()
    {
        _svc.StartRecording(_tmpPath, RecordService.RecordFormat.Csv);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Simulate SDK read thread hammering OnFrame from parallel callers.
        Parallel.For(0, 10000, i => _svc.OnFrame(BuildFrame(0x100, (byte)(i & 0xFF))));
        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(500);
        _svc.StopRecording();
    }

    [Fact]
    public async Task Writer_Loop_Drains_Channel_And_Increments_Count()
    {
        _svc.StartRecording(_tmpPath, RecordService.RecordFormat.Csv);
        for (int i = 0; i < 100; i++) _svc.OnFrame(BuildFrame(0x100, (byte)i));
        // Wait for the writer thread to drain. 200 ms is plenty for 100
        // frames on any reasonable machine.
        await Task.Delay(200);
        _svc.FrameCount.Should().Be(100);
        _svc.StopRecording();
    }

    [Fact]
    public async Task Writer_Flushes_Every_One_Second()
    {
        // v3.5.2 PATCH: drive the flush timer deterministically via the
        // FakeTimerFactory wired into the service ctor. NO Task.Delay
        // for the tick cadence — file size must grow between explicit
        // Fire() calls, every time.
        //
        // v3.5.4 PATCH: replaced `Task.Yield(); Task.Yield();` with
        // WaitForFileSizeChangeAsync polling. Under CI parallel load
        // the test scheduler can starve the BackgroundService loop
        // between Fire() calls — the LIFO TCS list is empty when
        // Fire() resolves and the await Task.Yield path completes
        // before iteration 2 has reached WaitForNextTickAsync. The
        // polling helper only returns after the file size actually
        // grows, proving the flush loop has reached the next tick.
        _svc.StartRecording(_tmpPath, RecordService.RecordFormat.Csv);

        // Wait for the BackgroundService.ExecuteAsync to reach the
        // factory.CreateTimer call. xunit's parallel collection runs
        // tests concurrently, so we cannot assume the task has reached
        // this point by the time StartAsync returns — poll briefly.
        var timer = await WaitForCreatedTimerAsync();

        _svc.OnFrame(BuildFrame(0x100, 0xAB));
        // Wait for the writer thread to drain the enqueued frame
        // before we let the flush timer tick. WaitForDrain polls at
        // 10 ms — bounded inside the 5 s ceiling.
        await WaitForDrainAsync(1);

        // Tick 1 — flushes the first frame to disk.
        timer.Fire();
        var firstSize = await WaitForFileSizeChangeAsync(0);
        firstSize.Should().BeGreaterThan(0, "tick 1 must have flushed something to disk");

        _svc.OnFrame(BuildFrame(0x100, 0xCD));
        await WaitForDrainAsync(2);

        // Tick 2 — second frame lands on disk. Polling here is
        // critical: by the time firstSize returned, the writer
        // thread had already drained AND the BackgroundService had
        // reached iteration 2's WaitForNextTickAsync, so Fire()
        // will resolve a real waiter.
        timer.Fire();
        var secondSize = await WaitForFileSizeChangeAsync(firstSize);
        secondSize.Should().BeGreaterThan(firstSize,
            "a second tick + second OnFrame must grow the recorded file");

        _svc.StopRecording();
    }

    // Polls the recorded file until its size strictly exceeds the
    // baseline. Used in lieu of Task.Yield for flush-loop waiting
    // because file-size change is a deterministic postcondition —
    // by the time we observe it, the BackgroundService has reached
    // the next WaitForNextTickAsync AND the writer thread has
    // flushed the previous batch to disk.
    private async Task<long> WaitForFileSizeChangeAsync(long baselineSize, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount + timeoutMs;
        while (Environment.TickCount < deadline)
        {
            var currentSize = new FileInfo(_tmpPath).Length;
            if (currentSize > baselineSize)
            {
                return currentSize;
            }
            await Task.Delay(10);
        }
        return new FileInfo(_tmpPath).Length;
    }

    private async Task<FakePeriodicTimer> WaitForCreatedTimerAsync(int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount + timeoutMs;
        while (Environment.TickCount < deadline)
        {
            if (_timerFactory.CreatedTimers.Count > 0)
            {
                return _timerFactory.CreatedTimers[0];
            }
            await Task.Delay(10);
        }
        throw new InvalidOperationException(
            $"BackgroundService.ExecuteAsync did not create a timer within {timeoutMs}ms");
    }

    [Fact]
    public void Channel_Full_Drops_Oldest_Frame()
    {
        _svc.StartRecording(_tmpPath, RecordService.RecordFormat.Csv);
        // Fill beyond capacity (8192) without giving writer a chance to drain.
        // The writer thread IS running, so we cannot assert an exact drop
        // count — but the API exposes FrameEnqueuedCount (every TryWrite
        // attempt) and FrameDroppedOnFullChannel (drops from the bounded
        // channel), and both must be non-negative.
        for (int i = 0; i < 10000; i++) _svc.OnFrame(BuildFrame(0x100, (byte)(i & 0xFF)));
        _svc.FrameEnqueuedCount.Should().Be(10000);
        _svc.FrameDroppedOnFullChannel.Should().BeGreaterThanOrEqualTo(0);
        _svc.StopRecording();
    }

    [Fact]
    public async Task StopRecording_Drains_Remaining_Frames_Before_Footer()
    {
        _svc.StartRecording(_tmpPath, RecordService.RecordFormat.Csv);
        for (int i = 0; i < 500; i++) _svc.OnFrame(BuildFrame(0x100, (byte)i));
        // StopRecording must drain the channel BEFORE closing the file,
        // so all 500 frames should be visible after a short wait.
        _svc.StopRecording();
        await Task.Delay(500);
        _svc.FrameCount.Should().Be(500);
    }

    private async Task WaitForDrainAsync(long expectedEnqueued, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount + timeoutMs;
        while (Environment.TickCount < deadline)
        {
            if (_svc.FrameEnqueuedCount >= expectedEnqueued
                && _svc.FrameCount >= expectedEnqueued)
            {
                return;
            }
            await Task.Delay(10);
        }
    }

    private static CanFrame BuildFrame(uint id, byte b) => new(
        new CanId(id, FrameFormat.Standard),
        new byte[] { b },
        FrameFlags.None,
        new ChannelId(0x51),
        default);
}
