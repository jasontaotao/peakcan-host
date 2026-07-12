using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;

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