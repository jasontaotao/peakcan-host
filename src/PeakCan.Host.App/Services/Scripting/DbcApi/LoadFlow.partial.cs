// DbcApi/LoadFlow.partial.cs — W32 T1 (Flow A, LARGEST 73 LoC)
// Load method: delegates to _dbcService.LoadAsync + surfaces 4 distinct result
// envelopes (success / LoadFailed-surfaced-error / Cancelled / Exception).
// Public async API for ClearScript V8 script callers (script engine uses
// default-value parameter so existing dbc.load(path) calls work bit-identically).
//
// W25 D5 deviation APPLIED: Load 73 LoC LARGEST method MOVES per the sharp
// discrete flow boundary criterion (Load → return result envelope = discrete
// Load dispatcher with 4 distinct result paths, NOT a single central
// orchestration loop). Sister of W25 OnChannelFrame 73 LoC + W26 OnFrame
// 62 LoC + W27 LoadAsync 60 LoC + W28 LoadAsync 79 LoC + W30 SendAsync
// 91 LoC moves. **6th move** in largest-method-can-move observations.
//
// Cross-partial helper pattern (W22+W23+W24+W25+W26+W27+W28+W29+W30+W31 sister):
// Load reads _currentDocument + _lastLoadError (both stay in main partial;
// written by OnDbcLoaded + OnLoadFailed respectively).
//
// W23 STRUCT-FABRICATION LESSON: Task<object> async signature + Volatile.Write
// 1-arg + ConcurrentDictionary indexer + DbcDocument.Messages enumerable
// signatures verified during verbatim re-extraction.
//
// W32 T1 verbatim re-extracted via `git show main:src/.../DbcApi.cs | sed -n '53,148p'`
// per W20 T2 R1 fabrication LESSON (39th application).

using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.Services.Scripting;

public sealed partial class DbcApi
{
    /// <summary>
    /// Load and parse a DBC file. v1.7.2 PATCH Item 2: accepts an
    /// optional <see cref="CancellationToken"/> so callers can cancel
    /// an in-flight load. The token is propagated to
    /// <see cref="DbcService.LoadAsync"/>, which already accepts CT.
    /// Cancellation surfaces as <c>errorCode="Cancelled"</c> via the
    /// silent-cancel branch in <c>DbcService.cs:162-165</c> combined
    /// with the post-load check below. The ClearScript V8 binder
    /// ignores the default-value parameter, so existing
    /// <c>dbc.load(path)</c> script calls work bit-identically.
    /// </summary>
    /// <param name="path">Absolute path to the .dbc file.</param>
    /// <param name="ct">Cancellation token. Pass
    /// <see cref="CancellationToken.None"/> (default) for no cancel.</param>
    /// <returns>
    /// Result object with <c>success</c>, <c>messageCount</c>,
    /// <c>errorCode</c>, and <c>error</c> fields. On success,
    /// <c>errorCode</c> and <c>error</c> are null. On failure,
    /// <c>errorCode</c> is the <see cref="ErrorCode"/> name
    /// (e.g. "IoError", "ParseFailure", "DbcFileTooLarge") and
    /// <c>error</c> is the disambiguating message. On
    /// cancellation, <c>errorCode</c> is "Cancelled".
    /// </returns>
    public async Task<object> Load(string path, CancellationToken ct = default)
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
            await _dbcService.LoadAsync(path, ct).ConfigureAwait(false);

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
}
