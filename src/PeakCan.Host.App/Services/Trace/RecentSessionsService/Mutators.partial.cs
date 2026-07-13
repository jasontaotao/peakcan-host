// RecentSessionsService/Mutators.partial.cs — W27 T2 (Flow B, 98 LoC)
// Public mutation API: 4 methods (Add + Clear × 2 each). Touches
// _items + _logger + Persist() + Raise() private helpers — all
// across-partial-visible (sister pattern: helper declarations
// stay where they were originally, partial-class makes them
// visible from any partial).
//
// Sister of W22 RecordService/Format.partial.cs: same lock-free
// MRU-list mutation pattern. W22 RecordService is file-I/O heavy
// with single record-shape format; W27 is more complex with
// viewType discriminator + AT-cap enforcement + Raise() helper
// call.
//

using System.IO;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.App.Services.Trace;

public sealed partial class RecentSessionsService
{
    /// <summary>
    /// Push <paramref name="path"/> to the top of the MRU with the default
    /// <c>"trace"</c> viewType. Preserved for source-compatibility with
    /// the v3.6.0–v3.6.4 callers that pre-date the viewType discriminator.
    /// </summary>
    public void Add(string path) => Add(path, viewType: "trace");

    /// <summary>
    /// Push <paramref name="path"/> to the top of the MRU tagged with
    /// <paramref name="viewType"/>. If the path is already present
    /// (case-insensitive exact match), remove the older entry first so
    /// we don't double-list — re-adding also refreshes <c>SavedAt</c> to
    /// <c>DateTimeOffset.UtcNow</c>, matching standard MRU semantics (the
    /// entry moves to the top of the list and its timestamp is the most
    /// recent use). Caps the result at <see cref="MaxEntries"/>; older
    /// entries fall off the bottom. Persists atomically (tmp + rename,
    /// UTF-8 BOM). Raises <see cref="PropertyChanged"/> on <c>Recent</c>.
    /// <para>
    /// v3.7.0 MINOR Chunk 2: <paramref name="viewType"/> is the surface
    /// discriminator (<c>"trace"</c> for AppShell, <c>"replay"</c> for
    /// the Replay tab). Filter at the consumer (AppShell filter narrows
    /// to trace/replay entries; Replay VM filters to replay entries) —
    /// the service itself is the unfiltered MRU list.
    /// </para>
    /// </summary>
    public void Add(string path, string viewType)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(viewType);
        // Case-insensitive exact-path match — Windows file paths are
        // case-insensitive at the OS level, so "C:\foo.tmtrace" and
        // "c:\FOO.tmtrace" should not both appear in the list.
        _items.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        _items.Insert(0, new RecentSessionDto(
            Path: path,
            Label: Path.GetFileName(path),
            SavedAt: DateTimeOffset.UtcNow,
            ViewType: viewType));
        if (_items.Count > MaxEntries)
        {
            _items.RemoveRange(MaxEntries, _items.Count - MaxEntries);
        }
        Persist();
        Raise();
    }

    /// <summary>
    /// Empty the entire MRU list (any <see cref="RecentSessionDto.ViewType"/>)
    /// and delete the backing file (best effort). Preserved for the v3.6.0
    /// back-compat caller; new callers should use <see cref="Clear(string)"/>
    /// so a Trace entry's <c>Clear</c> doesn't accidentally wipe the
    /// user's Replay entries (and vice versa).
    /// </summary>
    public void Clear() => Clear(viewType: null);

    /// <summary>
    /// Drop every entry whose <see cref="RecentSessionDto.ViewType"/>
    /// matches <paramref name="viewType"/>. <c>null</c> clears ALL entries
    /// (any viewType) plus the backing file — matches the v3.6.0
    /// <c>Clear()</c> behavior. <c>""</c> clears only legacy-trace
    /// entries (predates the field, ViewType default value).
    /// </summary>
    public void Clear(string? viewType)
    {
        if (viewType is null)
        {
            _items.Clear();
            try
            {
                if (File.Exists(_path)) File.Delete(_path);
            }
            catch (Exception ex)
            {
                LogDeleteFailed(_logger, ex, _path);
            }
            Raise();
            return;
        }
        var removed = _items.RemoveAll(e =>
            string.Equals(e.ViewType, viewType, StringComparison.Ordinal));
        if (removed == 0) return;
        if (_items.Count == 0)
        {
            try
            {
                if (File.Exists(_path)) File.Delete(_path);
            }
            catch (Exception ex)
            {
                LogDeleteFailed(_logger, ex, _path);
            }
        }
        else
        {
            Persist();
        }
        Raise();
    }
}
