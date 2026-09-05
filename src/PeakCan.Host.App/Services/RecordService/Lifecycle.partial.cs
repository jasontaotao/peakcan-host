using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core;

namespace PeakCan.Host.App.Services;

public sealed partial class RecordService
{
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

        // The background drain task (ExecuteAsync) is the SOLE channel reader.
        // Do NOT TryRead here: a frame dequeued by the drain task but not yet
        // written would be silently lost when the writer is disposed. Instead,
        // wait for the counters to converge: FrameEnqueuedCount counts frames
        // that entered the channel; FrameCount counts frames written to disk.
        // Equality guarantees the drain task has flushed everything (minus
        // DropOldest losses, which never enter the enqueued counter).
        var deadlineTicks = Environment.TickCount64 + 5000;
        while (Interlocked.Read(ref _frameCount) < Interlocked.Read(ref _frameEnqueuedCount)
               && Environment.TickCount64 < deadlineTicks)
        {
            Thread.Sleep(1);
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
}