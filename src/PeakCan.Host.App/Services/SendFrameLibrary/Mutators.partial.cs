// SendFrameLibrary/Mutators.partial.cs — W29 T2 (Flow B, LARGEST cluster 104 LoC)
// 6 lock-gated mutator methods: Load + Save x2 + Add + Remove + Count.
// All 6 share the `_gate` lock pattern (sister of W22 RecordService
// Mutators partial + W27 RecentSessionsService Mutators partial) +
// every mutator calls cross-partial helpers (EnsureLoaded +
// LoadUnlocked + SaveUnlocked from PersistenceFlow.partial.cs) via
// partial-class visibility.
//
// All methods <50 LoC individually — NO W25 D5 deviation applied
// (per W12+W14+W18+W19+W20+W21+W22+W23 D5 default sister-principle:
// "largest method stays inline" unless it's the LARGEST method in
// a god-class with sharp discrete flow boundary and ≥60 LoC body).
// NEW LESSON CANDIDATE: small-god-class-no-largest-method-keeps-all-inline-default-pattern
// (NEW 1/3 at W29 SPEC: W29 SendFrameLibrary 276 LoC has LARGEST
// method 24 LoC SaveUnlocked; too small for W25 D5 deviation; default
// D5 = extract per flow-boundary clarity, NOT LARGEST-method-can-move).
//
// 2 [LoggerMessage] declarations (LogCorrupt + LogSaveUnlockedFailed)
// stay on SendFrameLibrary.cs per W18+W22+W23+W25+W26+W27+W28
// sister precedent (CS8795 mitigation). Called from Flow A
// (PersistenceFlow) LoadUnlocked + SaveUnlocked.
//
// W29 T2 verbatim re-extracted via `git show HEAD:src/.../SendFrameLibrary.cs | sed -n '106,209p'`
// per W20 T2 R1 fabrication LESSON (33rd application).

using Microsoft.Extensions.Logging;

namespace PeakCan.Host.App.Services;

public sealed partial class SendFrameLibrary
{
    /// <summary>
    /// Read the library from disk. Returns an empty list if the file is
    /// missing or corrupt (corrupt is logged at Error level so the user can
    /// investigate via the log file).
    /// v1.2.11 PATCH review fix: takes the gate so callers can't race with
    /// Add / Remove / Save.
    /// </summary>
    public IReadOnlyList<SavedFrame> Load()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return LoadUnlocked();
        }
    }

    /// <summary>
    /// Persist <paramref name="frames"/> to disk atomically: write to
    /// <c>{path}.tmp</c>, then rename over <c>{path}</c>. A crash mid-rename
    /// leaves either the old file or the new file — never a half-written one.
    /// v1.2.11 PATCH review fix: deletes the orphaned .tmp on failure.
    /// </summary>
    public void Save(IEnumerable<SavedFrame> frames)
    {
        // v1.2.13 PATCH Item 7: caller-supplied list is authoritative for
        // both on-disk state and the cached count.
        var snapshot = frames.ToList();
        lock (_gate)
        {
            SaveUnlocked(snapshot);
            _cachedCount = snapshot.Count;
        }
    }

    /// <summary>
    /// v1.2.12 PATCH Item 13: parameterless save re-writes the current
    /// on-disk library back to disk. Used by tests to trigger the
    /// SaveUnlocked error path without supplying frames.
    /// </summary>
    public void Save()
    {
        lock (_gate)
        {
            EnsureLoaded();
            var current = LoadUnlocked().ToList();
            SaveUnlocked(current);
            _cachedCount = current.Count;
        }
    }

    /// <summary>
    /// v1.2.11 PATCH review fix (HIGH): atomic Add. Loads the current list,
    /// appends <paramref name="frame"/>, saves — all under the gate so two
    /// callers don't drop each other's changes. Returns the new frame count.
    /// </summary>
    public int Add(SavedFrame frame)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var current = LoadUnlocked().ToList();
            current.Add(frame);
            SaveUnlocked(current);
            _cachedCount = current.Count;
            return _cachedCount;
        }
    }

    /// <summary>
    /// v1.2.11 PATCH review fix (HIGH): atomic Remove-by-Name. Returns true
    /// if a frame was removed.
    /// </summary>
    public bool Remove(string name)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var current = LoadUnlocked().ToList();
            int before = current.Count;
            current.RemoveAll(f => f.Name == name);
            if (current.Count == before) return false;
            SaveUnlocked(current);
            _cachedCount = current.Count;
            return true;
        }
    }

    /// <summary>
    /// v1.2.12 PATCH Item 1: number of saved frames. Lock-snapshotted under
    /// <c>_gate</c> so the value is consistent with concurrent Add/Remove.
    /// Used by <c>SendViewModel</c> to surface a count in the post-save
    /// status message.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                EnsureLoaded();
                return _cachedCount;
            }
        }
    }
}
