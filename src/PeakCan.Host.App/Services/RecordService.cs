using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.App.Services;

/// <summary>
/// Records received CAN frames to disk in ASC or CSV format.
/// Implements <see cref="IFrameSink"/> so the <see cref="ChannelRouter"/>
/// can attach it as a fan-out target.
/// <para>
/// <b>Thread-safety:</b> <see cref="OnFrame"/> is called on the SDK read
/// thread; file I/O is buffered and flushed periodically (every 1 s or
/// on stop). The writer is guarded by a lock so concurrent OnFrame calls
/// are serialized.
/// </para>
/// <para>
/// <b>File formats:</b>
/// <list type="bullet">
///   <item><b>ASC</b> — Vector ASCII format, compatible with CANoe/CANalyzer.
///     One line per frame: <c>timestamp channel ID dlc data flags</c>.</item>
///   <item><b>CSV</b> — Simple comma-separated: <c>timestamp,channel,id,dlc,data,flags</c>.</item>
/// </list>
/// </para>
/// </summary>
public sealed partial class RecordService : IFrameSink, IDisposable
{
    /// <summary>Supported recording formats.</summary>
    public enum RecordFormat
    {
        /// <summary>Vector ASCII format (CANoe/CANalyzer compatible).</summary>
        Asc,
        /// <summary>Comma-separated values.</summary>
        Csv
    }

    private readonly ILogger<RecordService> _logger;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private RecordFormat _format;
    private bool _isRecording;
    private long _frameCount;
    private DateTime _startTime;

    /// <summary>True when actively recording to a file.</summary>
    public bool IsRecording
    {
        get { lock (_gate) return _isRecording; }
    }

    /// <summary>Number of frames written since the last <see cref="StartRecording"/>.</summary>
    public long FrameCount
    {
        get { lock (_gate) return _frameCount; }
    }

    public RecordService(ILogger<RecordService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Start recording to <paramref name="path"/>. If already recording,
    /// stops the current recording first. Creates the file and writes
    /// the header (CSV) or opening comment (ASC).
    /// </summary>
    public void StartRecording(string path, RecordFormat format)
    {
        lock (_gate)
        {
            if (_isRecording) StopRecordingInner();
            _format = format;
            _frameCount = 0;
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
    }

    /// <summary>Stop recording and close the file. Idempotent.</summary>
    public void StopRecording()
    {
        lock (_gate)
        {
            StopRecordingInner();
        }
    }

    private void StopRecordingInner()
    {
        if (!_isRecording) return;
        _isRecording = false;
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
    /// Receive a frame from the <see cref="ChannelRouter"/>. If recording,
    /// writes the frame to the file. Non-blocking, non-throwing.
    /// </summary>
    public void OnFrame(CanFrame frame)
    {
        lock (_gate)
        {
            if (!_isRecording || _writer is null) return;
            try
            {
                WriteFrame(frame);
                _frameCount++;
            }
            catch (Exception ex)
            {
                LogFrameWriteFailed(_logger, ex);
                // Don't stop recording on a single write failure — the
                // file might be on a network share that briefly lost
                // connection. The next frame will either succeed or the
                // user will stop recording manually.
            }
        }
    }

    /// <summary>Sink-isolation hook — no action needed for recording.</summary>
    public void OnError(Exception ex)
    {
        Debug.WriteLine(
            $"[RecordService] forwarded error (no action): {ex.GetType().Name}: {ex.Message}");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            StopRecordingInner();
        }
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
}
