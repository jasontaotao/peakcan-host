# v2.1.2 PATCH — Multi-frame sequence persistence (2026-07-02)

## Summary

Closes the deferred gap from v2.1.0: sequence list was volatile (lost on window close). v2.1.2 PATCH adds **named sequence persistence** to `%APPDATA%\PeakCan.Host\sequences.json`, mirroring the existing `SendFrameLibrary` pattern.

```
Multi-frame send window toolbar:
  [ComboBox: SavedSequences] [Load] [Delete] | [Name input] [💾 Save current]
```

- **Save current** — snapshot the current Rows + mode + delay + iterations under the typed name
- **Load** — replace current Rows with the saved sequence; restore mode + delay + iterations
- **Delete** — remove the saved sequence from the library
- **Duplicate name** — last-wins (mirrors `DidDatabase.AddRange` semantics)
- **Round-trip** — both Raw and DBC rows preserve all fields (kind, ID, data, flags, message name, per-signal values)

## Architecture

```
SequenceLibrary  (App/Services/Sequence/)  ← new in v2.1.2
   SavedSequence record: name, mode, delayMs, iterations, List<SavedRow>, savedAt
   SavedRow record:      Kind (Raw/Dbc) + all Raw fields + DBC fields + List<SavedSignalValue>
   SavedSignalValue:     Name, Value?
   JSON envelope:        { version: 1, sequences: [...] }
   Atomic write:         File.WriteAllText(tmp) + File.Move(overwrite:true)
   Lock:                 Concurrent-safe (mirror SendFrameLibrary)

MultiFrameSendViewModel  (extended v2.1.2)
   + SavedSequences  ObservableCollection<SavedSequence>
   + SelectedSavedSequence
   + SaveNameText
   + SaveCurrentCommand / LoadSavedCommand / DeleteSavedCommand
   + BuildSavedSequence / SnapshotRow / MaterializeRow helpers
```

### Pattern parity with SendFrameLibrary

| Aspect | SendFrameLibrary | SequenceLibrary (NEW) |
|--------|------------------|----------------------|
| On-disk path | `%APPDATA%\PeakCan.Host\send-library.json` | `%APPDATA%\PeakCan.Host\sequences.json` |
| Atomic write | `File.Move(tmp, overwrite: true)` | Same |
| Lock-based concurrency | `_gate` + `lock (_gate)` | Same |
| Corrupt file recovery | Log + empty list | Same |
| Version envelope | `LibraryFile.Version = 1` | Same |
| Duplicate name | Last-wins | Same |
| Test seam | `internal ctor(string path, ...)` | Same |

## Test counts

| Suite | v2.1.1 | v2.1.2 | Δ |
|-------|--------|--------|---|
| Core  | 388    | 388    | 0 |
| App   | 484    | 492    | +8 (`SequenceLibraryTests`) |
| Infra | 84     | 84     | 0 |
| **Total** | **956 + 6 SKIP** | **964 + 6 SKIP** | +8 |

Race-flake counter preserved (28/28+).

### New tests (8)

| Test | Coverage |
|------|----------|
| `Add_PersistsToDisk_ReloadableAfterNewInstance` | round-trip across instances |
| `Add_DuplicateName_LastWins_ReplacesExisting` | last-wins on duplicate |
| `Remove_ReturnsTrue_WhenPresent_False_WhenAbsent` | Remove correctness |
| `Count_ReflectsCurrentState` | Count + cache |
| `Load_MissingFile_ReturnsEmptyList_DoesNotThrow` | first-run path |
| `Load_CorruptJson_ReturnsEmptyList_LogsError` | corrupt-file recovery |
| `Save_AtomicWrite_LeavesIntactFile_OnSuccess` | file-on-disk + tmp cleanup |
| `Sequence_RoundTrips_DbcRows_PreserveAllFields` | full DBC row round-trip |

## Files changed (5)

### Added (3)
- `src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs` (~250 lines)
- `tests/PeakCan.Host.App.Tests/Services/Sequence/SequenceLibraryTests.cs` (~180 lines, 8 tests)

### Modified (3)
- `src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs` (+~140 lines: SavedSequences + commands + snapshot/materialize helpers)
- `src/PeakCan.Host.App/Windows/MultiFrameSendWindow.xaml` (+toolbar row: picker + Load/Delete + Name + Save)
- `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` (+1 DI registration)

### Docs (1)
- `docs/release-notes-v2.1.2.md` (this file)

## Process lessons (NEW — from this PATCH)

1. **Mirror a proven service design for new persistence needs** — `SendFrameLibrary` already had the right shape: lock + atomic write + version envelope + corrupt-file recovery + last-wins on duplicate. Re-implementing the same primitives for sequences would have invited subtle drift (different atomic write, different JSON error handling, etc.). Mirror the pattern verbatim; the lessons from the original ship carry forward. Cost: ~250 lines of code that's structurally identical to its sibling, but with new domain types. Benefit: zero new failure modes in the persistence layer.

2. **Snapshot/materialize symmetry is essential** — When persisting live `MultiFrameSequenceRow` (an `ObservableObject` with two-way bindings) into a record, you can't serialize the row directly — the serialization layer needs plain records with no INPC overhead. The VM owns both directions: `SnapshotRow(row)` for save, `MaterializeRow(savedRow)` for load. Both directions go through the same field mapping, so a new column on the row needs the same mapping added in two places (flagged via the symmetry in the code comments).

3. **`internal ctor(string path, ILogger)` test seam** — Same pattern as `SendFrameLibrary` v1.2.11: production ctor resolves `%APPDATA%` via `Environment.GetFolderPath`; test ctor takes a custom path so tests can write to `Path.GetTempPath()` and not pollute user state. `internal` visibility + `InternalsVisibleTo("PeakCan.Host.App.Tests")` lets tests reach it without exposing the API surface.

## Pre-ship review

- 0C / 0H / 0M / 0L
- Self-review:
  - SequenceLibrary mirrors SendFrameLibrary 1:1 in shape; same lock + atomic-write + corrupt-recovery invariants
  - VM commands all CanExecute=false when `IsRunning` (mirrors row commands)
  - Load replaces all Rows (no append) so re-loading a sequence is idempotent
  - DBC rows round-trip via separate `SavedSignalValue` list (not embedded in row)
  - Race-test flake counter preserved (28/28+; same 2 pre-existing flakes from earlier cycles)

## Ship method

Tier 3 fallback (github.com:443 still down). 预计 11-call pipeline.

## Open follow-ups

- **v2.2.0 MINOR** candidate: Replay-from-file (load ASC/CSV trace, dispatch as sequence)
- **Future PATCH** candidate: drag-drop reordering of frames within the sequence (current MoveUp/MoveDown is fine but DnD is nicer UX)