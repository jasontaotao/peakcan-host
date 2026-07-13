// SequenceLibrary/Mutators.partial.cs — W33 T2 (Flow B, 75 LoC)
// 5 lock-gated mutator methods: Load + Save + Add + Remove + Count.
// All 5 share the _gate lock pattern (sister of W22 RecordService Mutators
// partial + W27 RecentSessionsService Mutators partial + W29 SendFrameLibrary
// Mutators partial) + every mutator calls cross-partial helpers from
// PersistenceFlow.partial.cs via partial-class visibility.
//
// All methods <50 LoC individually — NO W25 D5 deviation applied
// (per W29 NEW `small-god-class-no-largest-method-keeps-all-inline-default-pattern`
// 2/3 PROMOTION at W31.5: W33 SequenceLibrary SaveUnlocked 21 LoC LARGEST method
// < 50 LoC threshold → default D5 sister-principle applied).
//
// 2 [LoggerMessage] declarations (LogCorrupt + LogSaveFailed) stay on
// SequenceLibrary.cs per W18+W22+W23+W25+W26+W27+W28+W29+W30+W31+W32+W33
// sister precedent (CS8795 mitigation). Called from Flow A (PersistenceFlow)
// LoadUnlocked + SaveUnlocked.
//
// W33 T2 verbatim re-extracted via `git show main:src/.../SequenceLibrary.cs | sed -n '110,188p'`
// per W20 T2 R1 fabrication LESSON (42nd application).

namespace PeakCan.Host.App.Services.Sequence;

public sealed partial class SequenceLibrary
{
    /// <summary>
    /// Read the library from disk. Returns an empty list if the file is
    /// missing or corrupt (corrupt is logged at Error level so the user
    /// can investigate via the log file).
    /// </summary>
    public IReadOnlyList<SavedSequence> Load()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return LoadUnlocked();
        }
    }

    /// <summary>
    /// Persist the entire library atomically: write to
    /// <c>{path}.tmp</c>, then rename over <c>{path}</c>. A crash
    /// mid-rename leaves either the old file or the new file — never
    /// a half-written one.
    /// </summary>
    public void Save(IEnumerable<SavedSequence> sequences)
    {
        var snapshot = sequences.ToList();
        lock (_gate)
        {
            SaveUnlocked(snapshot);
            _cachedCount = snapshot.Count;
        }
    }

    /// <summary>
    /// Atomic Add. Loads the current list, appends
    /// <paramref name="sequence"/>, saves — all under the gate so two
    /// callers don't drop each other's changes. Returns the new count.
    /// If a sequence with the same name already exists, it's replaced
    /// (last-wins, mirrors <c>DidDatabase.AddRange</c>).
    /// </summary>
    public int Add(SavedSequence sequence)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var current = LoadUnlocked().ToList();
            current.RemoveAll(s => s.Name == sequence.Name);
            current.Add(sequence);
            SaveUnlocked(current);
            _cachedCount = current.Count;
            return _cachedCount;
        }
    }

    /// <summary>Atomic Remove-by-Name. Returns true if a sequence was removed.</summary>
    public bool Remove(string name)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var current = LoadUnlocked().ToList();
            int before = current.Count;
            current.RemoveAll(s => s.Name == name);
            if (current.Count == before) return false;
            SaveUnlocked(current);
            _cachedCount = current.Count;
            return true;
        }
    }

    /// <summary>Number of saved sequences.</summary>
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
