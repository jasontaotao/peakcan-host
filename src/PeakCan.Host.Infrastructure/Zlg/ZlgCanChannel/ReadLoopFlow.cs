using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core;

namespace PeakCan.Host.Infrastructure.Zlg;

// 读循环：轮询 VCI_Receive / VCI_ReceiveFD，分发帧到 FrameReceived 事件。
public sealed partial class ZlgCanChannel
{
    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var consecutiveFailures = 0;
        while (!ct.IsCancellationRequested)
        {
            var gotAnyFrame = false;
            var iterationFailed = false;

            // 经典 CAN 读
            try
            {
                while (true)
                {
                    var ret = _reader.ReadClassic(_devType, _devIdx, _canIdx, out var msg);
                    if (ret == 0) break;
                    var ts = Timestamp.FromMillis(msg.TimeStamp, 0);
                    var frame = ZlgCanFrameFormatter.DecodeClassic(Id, msg, ts);
                    gotAnyFrame = true;
                    FrameReceived?.Invoke(frame);
                }
            }
            catch (Exception ex)
            {
                LogReadLoopException(_logger, _devType, _devIdx, _canIdx, "classic", ex);
                SafeEmitReadLoopError(new ReadLoopError(Id.Handle, ReadLoopErrorKind.ClassicReadException, ex));
                iterationFailed = true;
            }

            // CAN FD 读
            try
            {
                while (true)
                {
                    var ret = _reader.ReadFd(_devType, _devIdx, _canIdx, out var fdMsg);
                    if (ret == 0) break;
                    // ZLG 的时间戳单位是毫秒
                    var ts = Timestamp.FromMillis(fdMsg.TimeStamp, 0);
                    var frame = ZlgCanFrameFormatter.DecodeFd(Id, fdMsg, ts);
                    gotAnyFrame = true;
                    FrameReceived?.Invoke(frame);
                }
            }
            catch (Exception ex)
            {
                LogReadLoopException(_logger, _devType, _devIdx, _canIdx, "FD", ex);
                SafeEmitReadLoopError(new ReadLoopError(Id.Handle, ReadLoopErrorKind.FdReadException, ex));
                iterationFailed = true;
            }

            if (iterationFailed && !gotAnyFrame) consecutiveFailures++;
            if (gotAnyFrame) consecutiveFailures = 0;

            if (consecutiveFailures >= MaxConsecutiveReadFailures)
            {
                LogReadLoopGivingUp(_logger, _devType, _devIdx, _canIdx, "giving-up", consecutiveFailures);
                SafeEmitReadLoopError(new ReadLoopError(Id.Handle, ReadLoopErrorKind.LoopGivingUp, null));
                return;
            }

            var delay = consecutiveFailures == 0
                ? 1
                : ReadLoopBackoffMs[Math.Min(consecutiveFailures - 1, ReadLoopBackoffMs.Length - 1)];
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>每订阅者独立 try/catch 隔离。</summary>
    private void SafeEmitReadLoopError(ReadLoopError err)
    {
        var handler = ReadLoopError;
        if (handler is null) return;
        foreach (Action<ReadLoopError> sub in handler.GetInvocationList())
        {
            try { sub(err); }
            catch (Exception ex)
            {
                LogReadLoopSubscriberThrew(_logger, _devType, _devIdx, _canIdx,
                    sub.Method.DeclaringType?.FullName ?? "?", ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "ZLG read loop: dev={DevType}/{DevIdx} ch={CanIdx} {Kind} — giving up after {Failures} consecutive failures")]
    private static partial void LogReadLoopGivingUp(ILogger logger, uint devType, uint devIdx, uint canIdx, string kind, int failures);

    [LoggerMessage(Level = LogLevel.Error, Message = "ZLG read loop exception: dev={DevType}/{DevIdx} ch={CanIdx} {Kind}")]
    private static partial void LogReadLoopException(ILogger logger, uint devType, uint devIdx, uint canIdx, string kind, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ZLG read loop subscriber threw: dev={DevType}/{DevIdx} ch={CanIdx} sub={Sub}")]
    private static partial void LogReadLoopSubscriberThrew(ILogger logger, uint devType, uint devIdx, uint canIdx, string sub, Exception ex);
}