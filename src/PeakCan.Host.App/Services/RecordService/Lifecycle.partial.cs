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
            Interlocked.Increment(ref _recordingGeneration);
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

    /// <summary>
    /// 等待 drain task 把已入队帧全部写盘（计数收敛）。
    /// 事件驱动：drain task 每写完一帧检查收敛条件并置位 _drainConverged；
    /// 本方法先标记 _stopPending 再 Reset 事件（顺序防止漏信号），然后带
    /// 5s 上限等待——上限兜底 DropOldest 丢帧（被丢帧仍计入入队计数，两计数
    /// 永不收敛）、写盘失败与 drain task 已停止（宿主关停）等无法收敛的场景。
    /// </summary>
    private void WaitForDrainConvergence()
    {
        // 快路径：已收敛（或从未入队）→ 不等待。
        if (Interlocked.Read(ref _frameCount) >= Interlocked.Read(ref _frameEnqueuedCount))
            return;
        // 宿主关停竞态：事件已 Dispose（另一线程正在收尾）→ 放弃等待，
        // 走 5s 兜底语义之外的快速返回（此时代次守卫会阻止触碰新 writer）。
        if (Volatile.Read(ref _drainConvergedDisposed))
            return;
        Volatile.Write(ref _stopPending, true);
        try
        {
            _drainConverged.Reset();
            // Reset 后复查：信号可能在标记 _stopPending 与 Reset 之间已被置位。
            if (Interlocked.Read(ref _frameCount) >= Interlocked.Read(ref _frameEnqueuedCount))
                return;
            if (!_drainConverged.Wait(TimeSpan.FromMilliseconds(5000)))
                LogDrainWaitTimedOut(_logger, _frameEnqueuedCount, _frameCount);
        }
        catch (ObjectDisposedException)
        {
            // Dispose-vs-Wait 竞态兜底（review MEDIUM）：Dispose 在检查
            // _drainConvergedDisposed 之后、Wait 之前发生。
        }
    }

    /// <summary>StopRecording 的收尾（footer/flush/dispose）后清除等待标记，供下次录制复用事件。</summary>
    private void ClearStopPending() => Volatile.Write(ref _stopPending, false);

    private void StopRecordingInner()
    {
        if (!_isRecording) return;
        _isRecording = false;
        var gen = Volatile.Read(ref _recordingGeneration);

        // The background drain task (ExecuteAsync) is the SOLE channel reader.
        // Do NOT TryRead here: a frame dequeued by the drain task but not yet
        // written would be silently lost when the writer is disposed. Instead,
        // wait for the counters to converge: FrameEnqueuedCount counts frames
        // that entered the channel; FrameCount counts frames written to disk.
        // Equality guarantees the drain task has flushed everything. Note:
        // DropOldest-evicted frames DO enter the enqueued counter (OnFrame
        // increments on the TryWrite==true branch), so sustained overload or
        // write failures can make the counters never converge — the 5s cap
        // bounds the wait in those cases.
        // 2026-09-06：等待由 Thread.Sleep(1) 轮询改为事件驱动（_drainConverged），
        // 零轮询；快路径（计数已收敛，绝大多数场景）完全不等待。
        WaitForDrainConvergence();

        // 代次守卫：等待期间有新录制开启（_writer 已被替换）→ 本次停止不得
        // footer/dispose 新 writer，也不能把 _writer 置 null（会静默中断新录制）。
        if (Volatile.Read(ref _recordingGeneration) != gen)
        {
            ClearStopPending();
            return;
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
            ClearStopPending();
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
        Volatile.Write(ref _drainConvergedDisposed, true);
        _drainConverged.Dispose();
        base.Dispose();
    }
}