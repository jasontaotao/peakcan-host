using System.IO;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Services;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.App.Services;

/// <summary>
/// Records received CAN frames to disk in ASC or CSV format.
/// Implements <see cref="IFrameSink"/> so the <see cref="ChannelRouter"/>
/// can attach it as a fan-out target.
/// <para>
/// <b>Thread-safety:</b> <see cref="OnFrame"/> is called on the SDK read
/// thread. Frames are enqueued via a bounded
/// <see cref="Channel{T}"/> and drained by a single writer thread, so
/// the read thread never blocks on file I/O. The writer flushes the
/// <see cref="StreamWriter"/> every 1 second (or on stop), keeping
/// data loss under 1 s in the event of a crash.
/// </para>
/// <para>
/// <b>File formats:</b>
/// <list type="bullet">
///   <item><b>ASC</b> — Vector ASCII format, compatible with CANoe/CANalyzer.
///     One line per frame: <c>timestamp channel ID dlc data flags</c>.</item>
///   <item><b>CSV</b> — Simple comma-separated: <c>timestamp,channel,id,dlc,data,flags</c>.</item>
/// </list>
/// </para>
/// <para>
/// <b>Back-pressure:</b> the channel uses
/// <see cref="BoundedChannelFullMode.DropOldest"/> with capacity 8192;
/// if the writer falls behind (slow disk / AV scan), the oldest
/// queued frame is silently dropped and counted in
/// <see cref="FrameDroppedOnFullChannel"/>. Counters are exposed via
/// <see cref="FrameEnqueuedCount"/>, <see cref="FrameCount"/>, and
/// <see cref="FrameDroppedOnFullChannel"/>.
/// </para>
/// </summary>
public sealed partial class RecordService : BackgroundService, IFrameSink
{
    /// <summary>Supported recording formats.</summary>
    public enum RecordFormat
    {
        /// <summary>Vector ASCII format (CANoe/CANalyzer compatible).</summary>
        Asc,
        /// <summary>Comma-separated values.</summary>
        Csv
    }

    /// <summary>Bounded channel capacity; matches the SDK ring buffer budget.</summary>
    private const int FrameChannelCapacity = 8192;

    /// <summary>1 Hz flush cadence; matches the doc-promised behavior.</summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    private readonly ILogger<RecordService> _logger;
    // v3.5.2 PATCH: hold the factory, not a concrete PeriodicTimer, so
    // unit tests can drive flush ticks deterministically (see
    // FakeTimerFactory in App.Tests). Default ctor wires a real
    // PeriodicTimer-backed factory for production; internal ctor lets
    // tests inject a fake.
    private readonly ITimerFactory _timerFactory;
    private readonly Channel<CanFrame> _frameChannel = Channel.CreateBounded<CanFrame>(
        new BoundedChannelOptions(FrameChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false  // SDK thread + UI thread can both write
        });

    private RecordFormat _format;
    private DateTime _startTime;
    private long _frameEnqueuedCount;
    private long _frameCount;
    private long _frameDroppedOnFullChannel;
    private volatile bool _isRecording;
    private volatile TextWriter? _writer;

    /// <summary>True when actively recording to a file.</summary>
    public bool IsRecording => _isRecording;

    /// <summary>Number of frames written to disk since the last <see cref="StartRecording"/>.</summary>
    public long FrameCount => Interlocked.Read(ref _frameCount);

    /// <summary>Number of frames enqueued into the channel (including those later dropped).</summary>
    public long FrameEnqueuedCount => Interlocked.Read(ref _frameEnqueuedCount);

    /// <summary>Number of frames the channel refused because it was full (oldest dropped).</summary>
    public long FrameDroppedOnFullChannel => Interlocked.Read(ref _frameDroppedOnFullChannel);

    public RecordService(ILogger<RecordService> logger)
        : this(logger, new PeriodicTimerFactory())
    {
    }

    /// <summary>
    /// v3.5.2 PATCH: internal ctor lets unit tests inject an
    /// <see cref="ITimerFactory"/> (typically a <c>FakeTimerFactory</c>)
    /// so flush-tick tests can advance time deterministically without a
    /// real <see cref="System.Threading.PeriodicTimer"/>. Production DI
    /// uses the public single-arg ctor above, which constructs a real
    /// <see cref="PeriodicTimerFactory"/>.
    /// </summary>
    internal RecordService(ILogger<RecordService> logger, ITimerFactory timerFactory)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timerFactory = timerFactory ?? throw new ArgumentNullException(nameof(timerFactory));
    }

    /// <summary>
    /// Start recording to <paramref name="path"/>. If already recording,
    /// stops the current recording first. Creates the file and writes
    /// the header (CSV) or opening comment (ASC).
    /// </summary>
    public void StartRecording(string path, RecordFormat format)
    {
        if (_isRecording) StopRecordingInner();
        _format = format;
        Interlocked.Exchange(ref _frameEnqueuedCount, 0);
        Interlocked.Exchange(ref _frameCount, 0);
        Interlocked.Exchange(ref _frameDroppedOnFullChannel, 0);
        _startTime = DateTime.UtcNow;
        try
        {
            _writer = new StreamWriter(path, append: false, encoding: System.Text.Encoding.UTF8);
            WriteHeader();
            _isRecording = true;
            LogRecordingStarted(_logger, path, format switch
            {
                RecordFormat.Asc => "ASC",
                RecordFormat.Csv => "CSV",
                _ => format.ToString()
            });
        }
        catch (Exception ex)
        {
            LogRecordingFailed(_logger, path, ex);
            _writer?.Dispose();
            _writer = null;
            throw;
        }
    }

    /// <summary>Stop recording and close the file. Idempotent. Drains the channel first.</summary>
    public void StopRecording()
    {
        StopRecordingInner();
    }

    private void StopRecordingInner()
    {
        if (!_isRecording) return;
        _isRecording = false;

        // Spin-wait for the writer thread to drain the channel. We do NOT
        // call _frameChannel.Writer.TryComplete() because the channel
        // outlives any individual recording — StartRecording may be
        // called again on the same instance. Spin-waiting on the
        // reader count is sufficient: it is decremented by the drain
        // task as soon as each frame is consumed, so by the time the
        // count hits 0 every queued frame has been passed to WriteFrame.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (_frameChannel.Reader.Count > 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }

        try
        {
            WriteFooter();
            _writer?.Flush();
            _writer?.Dispose();
            LogRecordingStopped(_logger, _frameCount);
        }
        catch (Exception ex)
        {
            LogRecordingStopFailed(_logger, ex);
        }
        finally
        {
            _writer = null;
        }
    }

    /// <summary>
    /// Receive a frame from the <see cref="ChannelRouter"/>. Non-blocking:
    /// enqueues into the bounded channel. If the channel is full, the
    /// oldest queued frame is dropped and <see cref="FrameDroppedOnFullChannel"/>
    /// is incremented.
    /// </summary>
    public void OnFrame(CanFrame frame)
    {
        if (!_isRecording || _writer is null) return;
        if (_frameChannel.Writer.TryWrite(frame))
        {
            Interlocked.Increment(ref _frameEnqueuedCount);
        }
        else
        {
            // TryWrite on a bounded channel with DropOldest should never
            // return false, but defend against the contract changing.
            Interlocked.Increment(ref _frameDroppedOnFullChannel);
        }
    }

    /// <summary>Sink-isolation hook — logs via ILogger. Debug.WriteLine stripped in Release builds; ILogger is not.</summary>
    public void OnError(Exception ex)
    {
        LogSinkError(_logger, ex, nameof(RecordService));
    }

    /// <summary>
    /// Writer thread: drain the channel, write each frame, and flush the
    /// <see cref="StreamWriter"/> every 1 second so a crash loses at most
    /// 1 second of recording. Runs for the lifetime of the host (until
    /// <see cref="StopAsync"/> cancels <paramref name="stoppingToken"/>).
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // v3.5.2 PATCH: factory-supplied timer so flush ticks are
        // test-deterministic (FakeTimerFactory.Fire()) when wired by
        // the internal test-only ctor. await using because
        // IPeriodicTimer is IAsyncDisposable (PeriodicTimer dispose is
        // sync but the IAsyncDisposable contract is required of any
        // async-capable timer).
        await using var flushTimer = _timerFactory.CreateTimer(FlushInterval);
        try
        {
            // Drain loop: read frames from the channel and write to disk.
            // ReadAllAsync yields when the channel is empty and returns
            // only when the writer is completed OR the token is canceled.
            // We never complete the channel writer (the channel outlives
            // individual recordings), so this loop only exits on host
            // shutdown via stoppingToken.
            var drainTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var frame in _frameChannel.Reader.ReadAllAsync(stoppingToken))
                    {
                        try
                        {
                            WriteFrame(frame);
                            Interlocked.Increment(ref _frameCount);
                        }
                        catch (Exception ex)
                        {
                            // Don't stop recording on a single write failure —
                            // the file might be on a network share that
                            // briefly lost connection.
                            LogFrameWriteFailed(_logger, ex);
                        }
                    }
                }
                catch (OperationCanceledException) { /* shutdown */ }
            }, stoppingToken);

            // Flush loop: timer tick → Flush(). This is the
            // "1 Hz flush" promised by the class doc.
            var flushTask = Task.Run(async () =>
            {
                try
                {
                    while (await flushTimer.WaitForNextTickAsync(stoppingToken))
                    {
                        try { _writer?.Flush(); }
                        catch (Exception ex) { LogFrameWriteFailed(_logger, ex); }
                    }
                }
                catch (OperationCanceledException) { /* shutdown */ }
            }, stoppingToken);

            await Task.WhenAll(drainTask, flushTask);
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Ensure any in-flight recording is closed before the base class
        // signals ExecuteAsync to stop. Without this, the drain loop
        // might miss the last few frames.
        StopRecordingInner();
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        StopRecordingInner();
        base.Dispose();
    }

    private void WriteHeader()
    {
        if (_writer is null) return;
        if (_format == RecordFormat.Csv)
        {
            _writer.WriteLine("timestamp,channel,id,dlc,data,flags");
        }
        else
        {
            _writer.WriteLine($"date {DateTime.UtcNow:ddd MMM dd HH:mm:ss yyyy}");
            _writer.WriteLine($"base hex  timestamps absolute");
            _writer.WriteLine($"no internal events logged");
        }
    }

    private void WriteFooter()
    {
        if (_writer is null) return;
        if (_format == RecordFormat.Asc)
        {
            var elapsed = DateTime.UtcNow - _startTime;
            _writer.WriteLine();
            _writer.WriteLine($"// {elapsed.TotalSeconds:F3} s");
        }
    }

    private void WriteFrame(CanFrame frame)
    {
        if (_writer is null) return;
        var elapsed = DateTime.UtcNow - _startTime;
        var dataHex = Convert.ToHexString(frame.Data.Span);

        if (_format == RecordFormat.Csv)
        {
            _writer.WriteLine(
                $"{elapsed.TotalSeconds:F6},{frame.Channel.Handle:X2},0x{frame.Id.Raw:X},{frame.Dlc},{dataHex},{FormatFlags(frame)}");
        }
        else
        {
            // ASC format: timestamp channel ID dlc data flags
            var fdFlag = frame.IsFd ? "  fd" : "";
            var brsFlag = (frame.Flags & FrameFlags.BitRateSwitch) != 0 ? " brs" : "";
            var esiFlag = (frame.Flags & FrameFlags.ErrorStateIndicator) != 0 ? " esi" : "";
            var errFlag = frame.IsError ? " error" : "";
            _writer.WriteLine(
                $"{elapsed.TotalSeconds:F6} {frame.Channel.Handle:X2}  {frame.Id.Raw:X}  {frame.Dlc}  {dataHex}{fdFlag}{brsFlag}{esiFlag}{errFlag}");
        }
    }

    private static string FormatFlags(CanFrame frame)
    {
        var flags = new List<string>();
        if (frame.IsFd) flags.Add("FD");
        if ((frame.Flags & FrameFlags.BitRateSwitch) != 0) flags.Add("BRS");
        if ((frame.Flags & FrameFlags.ErrorStateIndicator) != 0) flags.Add("ESI");
        if (frame.IsError) flags.Add("ERR");
        return string.Join("|", flags);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording started: {Path} ({Format})")]
    private static partial void LogRecordingStarted(ILogger logger, string path, string format);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording stopped: {Frames} frames written")]
    private static partial void LogRecordingStopped(ILogger logger, long frames);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recording failed to start: {Path}")]
    private static partial void LogRecordingFailed(ILogger logger, string path, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recording stop failed")]
    private static partial void LogRecordingStopFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Frame write failed (recording continues)")]
    private static partial void LogFrameWriteFailed(ILogger logger, Exception ex);

    // v1.2.12 PATCH Item 11: sink OnError → ILogger. The previous
    // Debug.WriteLine was stripped in Release builds (DEBUG not defined),
    // leaving production with no record of forwarded errors. Per service
    // EventId (6001) keeps the telemetry stream unambiguous.
    [LoggerMessage(EventId = 6001, Level = LogLevel.Warning, Message = "{Service} OnError forwarded")]
    private static partial void LogSinkError(ILogger logger, Exception ex, string service);
}
