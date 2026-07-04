# v3.4.1 PATCH — `ITracePalette.PickStrokeFor` Past-Capacity Test Gap

## What changed

Adds `PickStrokeFor_PastCapacity_DeterministicAcrossCalls` test
to `TableauPaletteTests`. Mirrors the
`PickColorFor_PastCapacity_DeterministicAcrossCalls` test added
in v3.3.1 for the color counterpart. The test pre-fills 10
sources (capacity) and then verifies that 3 successive
`PickStrokeFor("guid-overflow-A")` calls all return the same
`LineStyle` — pins the hash-fallback branch's determinism.

**Files modified (2):**
- `tests/PeakCan.Host.App.Tests/Services/Trace/TableauPaletteTests.cs` — +1 test
- `docs/release-notes-v3.4.1.md` (new)

**Test count:** 1057 + 6 SKIP / 0 fail (+1 net from v3.4.0's 1056).

**No production code changes.** Pure test coverage addition.

**Lessons:** 0 NEW. Routine test-coverage closure.
