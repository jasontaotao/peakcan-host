// DbcApi/QueryFlow.partial.cs — W32 T2 (Flow B, 75 LoC)
// Query+decode methods: Decode (frame → signal values + cache update) +
// GetSignal (most recent value lookup) + GetMessages (list all messages).
// All 3 touch _currentDocument (read-only, Volatile.Write'd by OnDbcLoaded)
// + _signalValues (ConcurrentDictionary, write in Decode).
//
// Called from ClearScript V8 script callers via IScriptDbcApi interface.
// Cross-partial caller pattern: LoadFlow (Flow A) Load reads _currentDocument
// + _lastLoadError; QueryFlow (Flow B) reads _currentDocument + writes
// _signalValues. Both partials share state via partial-class visibility
// (sister of W22+W23+W24+W25+W26+W27+W28+W29+W30+W31 cross-partial helper pattern).
//
// W23 STRUCT-FABRICATION LESSON: DbcDocument.Messages enumerable signature +
// Message.Signals signature + SignalDecoder.Decode static signature verified.
//
// W32 T2 verbatim re-extracted via `git show main:src/.../DbcApi.cs | sed -n '150,185p;187,208p;210,226p'`
// per W20 T2 R1 fabrication LESSON (40th application).

using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.Services.Scripting;

public sealed partial class DbcApi
{
    /// <summary>
    /// Decode a received frame using the loaded DBC document.
    /// </summary>
    /// <param name="frame">The CAN frame to decode.</param>
    /// <returns>Decoded message object with signals, or null if no DBC loaded or message not found.</returns>
    public object? Decode(CanFrame frame)
    {
        var doc = _currentDocument;
        if (doc is null) return null;

        var message = doc.Messages.FirstOrDefault(m => m.Id == frame.Id.Raw);
        if (message is null) return null;

        var signals = new Dictionary<string, object>();
        foreach (var signal in message.Signals)
        {
            var physicalValue = SignalDecoder.Decode(frame.Data.Span, signal);
            var rawValue = (physicalValue - signal.Offset) / signal.Factor;

            var key = $"{message.Name}.{signal.Name}";
            _signalValues[key] = new SignalSnapshot(physicalValue, rawValue, signal.Unit, DateTimeOffset.UtcNow);

            signals[signal.Name] = new
            {
                value = physicalValue,
                raw = rawValue,
                unit = signal.Unit ?? ""
            };
        }

        return new
        {
            message = message.Name,
            signals
        };
    }

    /// <summary>
    /// Get the most recent value of a specific signal.
    /// </summary>
    /// <param name="messageName">DBC message name.</param>
    /// <param name="signalName">DBC signal name.</param>
    /// <returns>Signal value object, or null if not yet received.</returns>
    public object? GetSignal(string messageName, string signalName)
    {
        var key = $"{messageName}.{signalName}";
        if (_signalValues.TryGetValue(key, out var snapshot))
        {
            return new
            {
                value = snapshot.PhysicalValue,
                raw = snapshot.RawValue,
                unit = snapshot.Unit ?? "",
                timestamp = snapshot.Timestamp.ToUnixTimeMilliseconds()
            };
        }

        return null;
    }

    /// <summary>
    /// List all messages in the loaded DBC document.
    /// </summary>
    /// <returns>Array of message info objects, or empty array if no DBC loaded.</returns>
    public object[] GetMessages()
    {
        var doc = _currentDocument;
        if (doc is null) return [];

        return doc.Messages.Select(m => new
        {
            id = (int)m.Id,
            name = m.Name,
            dlc = m.Dlc,
            sender = m.Sender ?? ""
        }).ToArray();
    }
}
