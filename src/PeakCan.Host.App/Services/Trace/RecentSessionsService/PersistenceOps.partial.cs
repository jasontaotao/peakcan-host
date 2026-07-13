// RecentSessionsService/PersistenceOps.partial.cs — W27 T1 (Flow A, LARGEST 94 LoC with xmldoc)
// File I/O lifecycle: LoadAsync (async load from JSON file) +
// Persist (atomic temp+rename save) + Raise (PropertyChanged
// trigger). Sister of W22 RecordService.Lifecycle partial.
//
// LoadAsync 60 LoC LARGEST method moves here per W25 D5 deviation
// (file-I/O lifecycle = sharp discrete flow, sister of W25
// OnChannelFrame + W26 OnFrame(CanFrame) 2 prior moves; 3rd
// confirmation of "largest method CAN move" pattern).
//
// 4 [LoggerMessage] declarations (LogCorrupt + LogOversized +
// LogSaveFailed + LogDeleteFailed) stay on RecentSessionsService.cs
// per W18 R1 + W22 D4 + W23 D4 + W25 D4 + W26 D4 + W27 sister
// precedent (CS8795 mitigation: cross-partial visibility works
// for caller-methods-as-long-as-declarations-stay-on-main).
//
// W23 STRUCT-FABRICATION LESSON: verify JsonSerializer.Serialize/
// Deserialize 2-arg + File.WriteAllText 3-arg + File.Delete/
// File.Move 1-arg + Environment.GetFolderPath 1-arg signatures
// (verified during verbatim re-extraction from HEAD).
//
// W27 T1 verbatim re-extracted via `git show HEAD:src/...cs | sed -n '208,301p'`
// per W20 T2 R1 fabrication LESSON (27th application).

using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PeakCan.Host.App.Services.Trace;

public sealed partial class RecentSessionsService
{
    /// <summary>
    /// Read the persisted file into <see cref="Recent"/>. Missing or
    /// corrupt files leave the list empty (logged at Error on corrupt).
    /// Explicit method (rather than a load-eagerly ctor) so unit tests
    /// can construct without a file existing and can defer the load
    /// until after the test arranges a fixture.
    /// </summary>
    public Task LoadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _items.Clear();
        if (!File.Exists(_path))
        {
            Raise();
            return Task.CompletedTask;
        }
        // v3.8.8 PATCH F2: precheck the file size BEFORE reading. The
        // call site is a fire-and-forget _ = _recentSessions.LoadAsync(...)
        // in AppShellViewModel ctor at line 336 (runs on the WPF
        // dispatcher at app startup). Without this guard, a user who
        // drops a large file (a logfile, a stray binary) at the
        // persisted path would block the UI thread for the full
        // File.ReadAllText + JsonSerializer.Deserialize duration. A 1 GB
        // file risks OOM. Refuse to deserialize anything beyond
        // MaxLoadFileBytes (1 MB) and treat as corrupt -- the existing
        // catch (JsonException or IOException) below leaves _items empty.
        // 1 MB is far above any legitimate recent-sessions payload
        // (5 entries × ~200 bytes ≈ 1 KB) and gives 1000x headroom for
        // future growth.
        var info = new FileInfo(_path);
        if (info.Length > MaxLoadFileBytes)
        {
            LogOversized(_logger, _path, info.Length, MaxLoadFileBytes);
            // _items already cleared at the top -- oversized-load
            // leaves the list empty rather than throwing, mirroring
            // the corrupt-load contract.
            Raise();
            return Task.CompletedTask;
        }
        try
        {
            var json = File.ReadAllText(_path);
            var dto = JsonSerializer.Deserialize<Envelope>(json, JsonOpts);
            if (dto?.Recent is { Count: > 0 })
            {
                _items.AddRange(dto.Recent);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            LogCorrupt(_logger, _path, ex);
            // _items already cleared above — corrupt-load leaves the
            // list empty rather than throwing, mirroring the
            // TraceSessionLibrary.Load contract.
        }
        // v3.8.6 PATCH H2: enforce the MaxEntries cap symmetric with Add.
        // Pre-fix, a hand-edited persisted JSON (or a back-compat user
        // upgrading from a pre-v3.6.0 build that did not enforce the cap)
        // could land on a 6-10 entry list -- the MRU menu would show more
        // than 5 entries until the next Add trimmed the tail.
        if (_items.Count > MaxEntries)
        {
            _items.RemoveRange(MaxEntries, _items.Count - MaxEntries);
        }
        Raise();
        return Task.CompletedTask;
    }

    private void Persist()
    {
        var dto = new Envelope
        {
            Schema = CurrentSchema,
            Recent = _items.ToList(),
        };
        var json = JsonSerializer.Serialize(dto, JsonOpts);
        var tmp = _path + ".tmp";
        try
        {
            File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            // Atomic on POSIX (rename) and Windows (MoveFileEx
            // MOVEFILE_REPLACE_EXISTING) — same pattern as
            // TraceSessionLibrary.Save and FileAutoSavePrefsStore.Save.
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            LogSaveFailed(_logger, ex, _path);
        }
    }

    private void Raise() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Recent)));
}
