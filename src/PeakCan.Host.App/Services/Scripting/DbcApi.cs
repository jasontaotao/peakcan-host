using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.Services.Scripting;

/// <summary>
/// Exposes DBC decoding operations to the JavaScript scripting engine.
/// Provides functions for loading DBC files, decoding frames, and
/// querying signal values.
/// <para>
/// <b>Thread-safety:</b> The current DBC document is read with
/// <see cref="Volatile.Read{T}"/> for cross-thread visibility.
/// Loading is async and updates the document atomically.
/// </para>
/// </summary>
public sealed partial class DbcApi
{
    private readonly ILogger<DbcApi> _logger;
    private readonly DbcService _dbcService;

    // Most recent decoded signal values keyed by "MessageName.SignalName".
    private readonly ConcurrentDictionary<string, SignalSnapshot> _signalValues = new();

    private DbcDocument? _currentDocument;

    public DbcApi(
        ILogger<DbcApi> logger,
        DbcService dbcService)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(dbcService);

        _logger = logger;
        _dbcService = dbcService;

        // Subscribe to DBC load events to keep our local reference in sync.
        _dbcService.DbcLoaded += OnDbcLoaded;
    }

    /// <summary>
    /// Load and parse a DBC file.
    /// </summary>
    /// <param name="path">Absolute path to the .dbc file.</param>
    /// <returns>Result object with success status, message count, and optional error.</returns>
    public async Task<object> Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new { success = false, messageCount = 0, error = "Path is empty" };
        }

        try
        {
            await _dbcService.LoadAsync(path).ConfigureAwait(false);

            var doc = _currentDocument;
            if (doc is not null)
            {
                LogDbcLoadedViaScript(_logger, path, doc.Messages.Count);
                return new { success = true, messageCount = doc.Messages.Count, error = (string?)null };
            }

            // LoadAsync completed but Current is still null — should not happen.
            return new { success = false, messageCount = 0, error = "Load completed but no document available" };
        }
        catch (Exception ex)
        {
            LogDbcLoadFailed(_logger, ex, path);
            return new { success = false, messageCount = 0, error = ex.Message };
        }
    }

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

    /// <summary>
    /// Called when DbcService loads a new document.
    /// </summary>
    private void OnDbcLoaded(DbcDocument doc)
    {
        Volatile.Write(ref _currentDocument, doc);
        _signalValues.Clear();
    }

    /// <summary>
    /// Cleanup.
    /// </summary>
    public void Dispose()
    {
        _dbcService.DbcLoaded -= OnDbcLoaded;
        _signalValues.Clear();
    }

    /// <summary>
    /// Snapshot of a decoded signal value.
    /// </summary>
    private sealed record SignalSnapshot(
        double PhysicalValue,
        double RawValue,
        string? Unit,
        DateTimeOffset Timestamp);

    [LoggerMessage(Level = LogLevel.Information, Message = "DBC loaded via script: {Path} ({Count} messages)")]
    private static partial void LogDbcLoadedViaScript(ILogger logger, string path, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to load DBC from {Path}")]
    private static partial void LogDbcLoadFailed(ILogger logger, Exception ex, string path);
}
