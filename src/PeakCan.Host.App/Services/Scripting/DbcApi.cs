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

    // v1.6.8 PATCH: capture last LoadFailed payload so Load() can surface
    // ErrorCode + Message to the ClearScript V8 caller. Volatile for
    // cross-thread visibility (LoadFailed fires from inside LoadAsync per
    // DbcService contract).
    private volatile Error? _lastLoadError;

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
        // v1.6.8 PATCH: subscribe to LoadFailed so Load() can surface
        // its payload to the script caller.
        _dbcService.LoadFailed += OnLoadFailed;
    }

    /// <summary>
    /// Load and parse a DBC file.
    /// </summary>
    /// <param name="path">Absolute path to the .dbc file.</param>
    /// <returns>
    /// Result object with <c>success</c>, <c>messageCount</c>,
    /// <c>errorCode</c>, and <c>error</c> fields. On success,
    /// <c>errorCode</c> and <c>error</c> are null. On failure,
    /// <c>errorCode</c> is the <see cref="ErrorCode"/> name
    /// (e.g. "IoError", "ParseFailure", "DbcFileTooLarge") and
    /// <c>error</c> is the disambiguating message. On
    /// cancellation, <c>errorCode</c> is "Cancelled".
    /// </returns>
    public async Task<object> Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new
            {
                success = false,
                messageCount = 0,
                errorCode = "EmptyPath",
                error = "Path is empty",
            };
        }

        try
        {
            await _dbcService.LoadAsync(path).ConfigureAwait(false);

            var doc = _currentDocument;
            if (doc is not null)
            {
                LogDbcLoadedViaScript(_logger, path, doc.Messages.Count);
                return new
                {
                    success = true,
                    messageCount = doc.Messages.Count,
                    errorCode = (string?)null,
                    error = (string?)null,
                };
            }

            // LoadAsync returned without a document. Either:
            // - LoadFailed fired (DbcService swallows IO/parse to event):
            //   surface its Error.Code + Error.Message.
            // - Cancellation (silent per DbcService contract): no
            //   LoadFailed fired, _lastLoadError stays null.
            //   Report as "Cancelled" so scripts can distinguish
            //   "I cancelled this" from "this failed with reason X".
            var err = _lastLoadError;
            if (err is not null)
            {
                return new
                {
                    success = false,
                    messageCount = 0,
                    errorCode = err.Code.ToString(),
                    error = err.Message,
                };
            }

            return new
            {
                success = false,
                messageCount = 0,
                errorCode = "Cancelled",
                error = "Load was cancelled",
            };
        }
        catch (Exception ex)
        {
            // Defensive — DbcService normally fires LoadFailed instead
            // of throwing. Only reachable for non-IO/parse exceptions
            // that escape DbcService's catch-all (e.g. assertion
            // failures in test stubs).
            LogDbcLoadFailed(_logger, ex, path);
            return new
            {
                success = false,
                messageCount = 0,
                errorCode = "Exception",
                error = ex.Message,
            };
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
        // v1.6.8 PATCH (D4): clear any stale error from a previous
        // failed load so it doesn't leak into the next successful
        // load's return value.
        _lastLoadError = null;
    }

    /// <summary>
    /// v1.6.8 PATCH: capture the last <see cref="DbcService.LoadFailed"/>
    /// payload so <see cref="Load"/> can surface its
    /// <see cref="ErrorCode"/> + message to the script caller.
    /// </summary>
    private void OnLoadFailed(Error error)
    {
        _lastLoadError = error;
    }

    /// <summary>
    /// Cleanup.
    /// </summary>
    public void Dispose()
    {
        _dbcService.DbcLoaded -= OnDbcLoaded;
        _dbcService.LoadFailed -= OnLoadFailed;  // v1.6.8 PATCH
        _signalValues.Clear();
        // v1.6.10 PATCH Item 1: clear stale state on Dispose (mirror
        // OnDbcLoaded's success-side clearing at line 228).
        Volatile.Write(ref _currentDocument, null);
        _lastLoadError = null;
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
