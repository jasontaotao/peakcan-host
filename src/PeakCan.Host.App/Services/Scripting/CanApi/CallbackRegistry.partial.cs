// CanApi/CallbackRegistry.partial.cs — W26 T2 (Flow B, ~80 LoC)
// IScriptCanApi callback registry: 4 mutators that register/unregister
// JS-script callbacks. Frame dispatch itself (OnFrame(CanFrame)) stays
// in SinkLifecycle.partial.cs; this partial only owns the
// registration surface.
//
// Touches 3 ConcurrentDictionary state fields in main:
// - _frameCallbacks (Action<CanFrame> by callbackId)
// - _messageCallbacks (Action<CanFrame> by int CAN ID + callbackId)
// - _prefixCallbacks (Action<CanFrame> by hex prefix + callbackId)
// Plus _callbackCounter for unique ID generation.
//
// All 6 [LoggerMessage] calls (LogFrameCallbackRegistered/Unregistered +
// LogMessageCallbackRegistered + LogPrefixCallbackRegistered) stay
// on CanApi.cs per W18 R1 + W22 D4 + W23 D4 + W25 D4 sister precedent
// (CS8795 mitigation).
//
// W26 T2 verbatim re-extracted via `git show HEAD:src/.../CanApi.cs | sed -n '106,184p'`
// per W20 T2 R1 fabrication LESSON (25th application).

using System.Collections.Concurrent;
using System.Globalization;
using PeakCan.Host.Core;

namespace PeakCan.Host.App.Services.Scripting;

public sealed partial class CanApi
{
    public string OnFrame(Action<CanFrame> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var id = $"frame_{Interlocked.Increment(ref _callbackCounter)}";
        _frameCallbacks[id] = callback;
        LogFrameCallbackRegistered(_logger, id);
        return id;
    }

    /// <summary>
    /// Unregister a frame callback.
    /// </summary>
    /// <param name="callbackId">Callback ID returned by <see cref="OnFrame"/>.</param>
    public void OffFrame(string callbackId)
    {
        if (callbackId is null) return;
        if (_frameCallbacks.TryRemove(callbackId, out _))
        {
            LogFrameCallbackUnregistered(_logger, callbackId);
        }
    }

    /// <summary>
    /// Register a callback for frames with a specific CAN ID.
    /// </summary>
    /// <param name="id">Exact CAN ID to match, or hex prefix string (e.g., "1A" matches 0x1A0-0x1AF).</param>
    /// <param name="callback">JavaScript function to call for matching frames.</param>
    /// <returns>Callback ID string.</returns>
    public string OnMessage(object id, Action<CanFrame> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var callbackId = $"msg_{Interlocked.Increment(ref _callbackCounter)}";

        if (id is int intId)
        {
            // Exact CAN ID match.
            var bucket = _messageCallbacks.GetOrAdd((uint)intId, _ => new());
            bucket[callbackId] = callback;
            LogMessageCallbackRegistered(_logger, callbackId, intId);
        }
        else if (id is string prefix)
        {
            // Hex prefix match.
            var bucket = _prefixCallbacks.GetOrAdd(prefix.ToUpper(CultureInfo.InvariantCulture), _ => new());
            bucket[callbackId] = callback;
            LogPrefixCallbackRegistered(_logger, callbackId, prefix);
        }
        else
        {
            throw new ArgumentException("id must be a number (exact CAN ID) or string (hex prefix)", nameof(id));
        }

        return callbackId;
    }

    /// <summary>
    /// Unregister a message callback.
    /// </summary>
    /// <param name="id">Same ID used in <see cref="OnMessage"/>.</param>
    /// <param name="callbackId">Callback ID returned by <see cref="OnMessage"/>.</param>
    public void OffMessage(object id, string callbackId)
    {
        if (id is int intId)
        {
            if (_messageCallbacks.TryGetValue((uint)intId, out var bucket))
            {
                bucket.TryRemove(callbackId, out _);
            }
        }
        else if (id is string prefix)
        {
            if (_prefixCallbacks.TryGetValue(prefix.ToUpper(CultureInfo.InvariantCulture), out var bucket))
            {
                bucket.TryRemove(callbackId, out _);
            }
        }
    }
}
