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
public sealed partial class DbcApi : IScriptDbcApi
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
