// CanApi/SinkLifecycle.partial.cs — W26 T1 (Flow A, LARGEST 80 LoC)
// IFrameSink interface implementation: receives frames from
// ChannelRouter fanout (OnFrame(CanFrame frame) sink dispatcher)
// + auto-detaches on error (OnError(Exception ex)) + cleanup
// unsubscribe from ChannelRouter (Dispose()). Sister of W25
// FrameRouting.partial.cs pattern (same fan-out-with-error-isolation
// shape across partial boundary).
//
// OnFrame(CanFrame frame) 62 LoC LARGEST method moved here per W25
// D5 deviation (frame-arrives → callback-fanout discrete dispatcher
// shape, sister of W25 OnChannelFrame 75 LoC which moved for
// fan-out-with-error-isolation).
//
// All 11 [LoggerMessage] declarations stay on CanApi.cs per W18 R1
// + W22 D4 + W23 D4 + W25 D4 sister precedent (CS8795 mitigation).
//
// W26 T1 verbatim re-extracted via `git show HEAD:src/.../CanApi.cs | sed -n '233,310p'`
// per W20 T2 R1 fabrication LESSON (24th application).

using System.Collections.Concurrent;
using System.Globalization;
using PeakCan.Host.Core;

namespace PeakCan.Host.App.Services.Scripting;

public sealed partial class CanApi
{
    public void OnFrame(CanFrame frame)
    {
        // Dispatch to all-frame callbacks.
        foreach (var callback in _frameCallbacks.Values)
        {
            try
            {
                callback(frame);
            }
            catch (Exception ex)
            {
                LogFrameCallbackError(_logger, ex);
            }
        }

        // Dispatch to exact-ID callbacks.
        // Script authors register with a plain int literal (e.g.
        // can.onMessage(0x100, cb) for an Extended frame with on-wire
        // ID 0x100), so the dictionary key is the raw 11/29-bit ID —
        // intentionally NOT the merged-IDE form used by
        // DbcDocument.MessagesById (see DbcDecodeBackgroundService.cs
        // for the contrast). Lookup uses frame.Id.Raw to match the
        // registration convention; do not "consistency-fix" this to
        // OR with 0x80000000u.
        if (_messageCallbacks.TryGetValue(frame.Id.Raw, out var idCallbacks))
        {
            foreach (var callback in idCallbacks.Values)
            {
                try
                {
                    callback(frame);
                }
                catch (Exception ex)
                {
                    LogMessageCallbackError(_logger, ex, frame.Id.Raw);
                }
            }
        }

        // Dispatch to prefix callbacks.
        var hexId = frame.Id.Raw.ToString("X", CultureInfo.InvariantCulture);
        foreach (var (prefix, callbacks) in _prefixCallbacks)
        {
            if (hexId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var callback in callbacks.Values)
                {
                    try
                    {
                        callback(frame);
                    }
                    catch (Exception ex)
                    {
                        LogPrefixCallbackError(_logger, ex, prefix);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Called by ChannelRouter when a sink error occurs.
    /// </summary>
    public void OnError(Exception ex)
    {
        LogSinkError(_logger, ex);
    }

    /// <summary>
    /// Cleanup: unsubscribe from ChannelRouter.
    /// </summary>
    public void Dispose()
    {
        _channelRouter.DetachSink(this);
        _frameCallbacks.Clear();
        _messageCallbacks.Clear();
        _prefixCallbacks.Clear();
    }
}
