# v3.3.1 PATCH — Palette exhaustion + dead array extraction (2026-07-04)

## Summary

Lifts the v3.2.0 hard-cap of 10 distinct trace sources per
`ITracePalette` instance. Sources past 10 now receive a
deterministic hash-based HSL color (same `sourceId` → same color)
instead of throwing `InvalidOperationException`. Also deletes the
truly-dead `TraceChartViewModel.Palette` array and `_nextColorSlot`
field (both pre-v3.2.0 remnants; 0 production references after the
v3.2.0 registry refactor).

**3 task-commits on top of v3.3.0 `21a0ed47`.**

## Files modified

- `src/PeakCan.Host.App/Services/Trace/TableauPalette.cs` — `PickColorFor`
  past-capacity branch now returns hash-based `OxyColor` (computed via
  inline standard HSL→RGB formula; OxyPlot 2.2.0 has no `FromHsl`)
  instead of throwing. Added a separate `_hashCache` dict so the
  fixed-slot cache (`_assigned[sourceId] → slot`) stays semantically
  pure (slot index in `[0, Colors.Length)`). Class XML doc updated.
- `src/PeakCan.Host.App/Services/Trace/TraceSessionRegistry.cs` —
  comment on the palette-slot allocation step updated to reflect the
  v3.3.1 lifted cap (no longer expects a `past capacity` throw).
- `src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs` — deleted
  `internal static readonly OxyColor[] Palette` + `private int _nextColorSlot`
  (and its `#pragma warning disable CS0169`); replaced with an
  explanatory comment block.
- `tests/PeakCan.Host.App.Tests/Services/Trace/TableauPaletteTests.cs`
  — inverted 1 v3.2.0 throw-assertion test into 2 hash-fallback tests:
  `PickColorFor_PastCapacity10_DoesNotThrow_ReturnsHashBasedColor` +
  `PickColorFor_PastCapacity_DeterministicAcrossCalls`.
- `tests/PeakCan.Host.App.Tests/Services/Trace/TraceSessionRegistryTests.cs`
  — inverted the parallel throw-assertion at the registry level
  (`LoadAsync_PastCapacity10_ThrowsInvalidOperationException` →
  `LoadAsync_PastCapacity10_DoesNotThrow_HashBasedFallbackColor`).
- `docs/release-notes-v3.3.0.md` — 2 of 6 deferred items marked
  **CLOSED in v3.3.1 PATCH**.
- `docs/release-notes-v3.3.1.md` — NEW (+this).

## Files new

| File | Purpose |
|------|---------|
| `docs/release-notes-v3.3.1.md` | This file |

## Behavior change (v3.3.0 → v3.3.1)

For `ITracePalette.PickColorFor` (consumed via `TraceSessionRegistry.LoadAsync`):

1. **No more `InvalidOperationException` past 10 sources.** The v3.2.0
   throw ("capacity exceeded: 10 distinct sources per session") is
   removed. Sources past 10 receive a deterministic hash-based HSL
   color via the standard formula `(uint)sourceId.GetHashCode()` →
   `h = hash % 360`, `l = 0.55 + ((hash / 360) % 20) / 100.0`,
   `s = 0.6`. `OxyPlot.OxyColor.FromHsl` does not exist in 2.2.0, so
   the formula is computed inline (HslToOxyColor helper).
2. **Determinism invariant preserved.** Same `sourceId` always returns
   the same `OxyColor` within an instance. Hash-based colors are
   cached in `_hashCache` (separate from the fixed-slot `_assigned`)
   so repeated lookups are O(1) and identical.
3. **Dead code swept.** `TraceChartViewModel.Palette` (Tableau-10
   colors inlined) + `_nextColorSlot` (unused since pre-v3.2.0) are
   deleted. 0 production/test references after the v3.2.0 registry
   refactor — confirmed by pre-flight grep. `SignalChartViewModel.Palette`
   is **live** (used by Signal tab `AddSignal`) and is intentionally
   not touched by this PATCH.

## Pre-ship review

**Code-reviewer (sonnet)**: pending — final whole-branch review at end
of pipeline.

**Tasks 1-3 (per-task review notes)**:

- **Task 1** (palette exhaustion hash-based fallback): RED tests
  pinned the throw → GREEN after impl. **2 brief deviations surfaced
  to the implementer and applied** (with rationale):
  - OxyPlot 2.2.0 does not expose `OxyColor.FromHsl`. Implemented the
    standard HSL→RGB formula inline (`HslToOxyColor` helper).
  - The plan's sentinel-`-1` cache approach (`_assigned[sourceId] = -1`)
    crashes `Colors[slot]` on the determinism test's second lookup of
    the same hash-based sourceId (IndexOutOfRange). Replaced with a
    separate `_hashCache: Dictionary<string, OxyColor>` so the
    fixed-slot cache stays semantically pure (slot index in
    `[0, Colors.Length)`). Both deviations preserve the plan's
    determinism + HSL semantics.
- **Task 2** (dead array extraction): pure deletion; no test changes;
  pre-flight grep confirmed 0 references.
- **Task 3** (release notes): doc-only; 2 of 6 v3.3.0 deferred items
  closed.

**Build verification**: `dotnet build PeakCan.Host.slnx -c Debug`
→ 0 warnings, 0 errors.

**Test verification**: `dotnet test PeakCan.Host.slnx`
→ **1047 + 6 SKIP / 0 fail** (1 transient race-flake hit during
pre-ship, passes in isolation per documented pattern).

## Test count

| Suite | v3.3.0 | v3.3.1 | Δ |
|-------|--------|--------|---|
| App | 569 | **570** | **+1 net** (1 v3.2.0 throw test `PickColorFor_PastCapacity10_ThrowsInvalidOperationException` inverted → 2 new tests `*_DoesNotThrow_ReturnsHashBasedColor` + `*_PastCapacity_DeterministicAcrossCalls`; parallel inversion at the registry level: 1 throw test `LoadAsync_PastCapacity10_ThrowsInvalidOperationException` → 1 new test `*_DoesNotThrow_HashBasedFallbackColor`. Net = -1 + 2 + 0 = +1.) |
| Core | 393 | 393 | 0 |
| Infrastructure | 84 | 84 | 0 |
| **Total** | **1046 + 6 SKIP** | **1047 + 6 SKIP** | **+1 net** |

Pre-ship full suite: **1047 + 6 SKIP / 0 fail** (race flake did
not fire on the final run; documented pre-existing race flake
pattern (`CyclicSendServiceRaceTests` /
`CyclicDbcSendServiceRaceTests`) continues to pass in isolation
per MEMORY).

## Closes 2 of 6 v3.3.0 deferred items

- **FULLY CLOSED** — Palette exhaustion at 11+ (hash-based fallback)
- **PARTIAL** — `TraceChartViewModel.Palette` dead array extraction
  (only the truly-dead `TraceChartViewModel.Palette` + `_nextColorSlot`
  deleted. `SignalChartViewModel.Palette` is **live** — used by Signal
  tab `AddSignal` — so consolidation would be a refactor not a
  deletion; deferred to v3.3.2 / v3.4.0.)

## Non-scope / follow-up items (intentional)

| Item | Why deferred |
|------|--------------|
| `.tmtrace` bundle file format (save/restore multi-trace session) | v3.3.x — JSON Schema + file dialog + 4 new commands |
| Per-source `CanIdFilter` | v3.3.x — independent filter per source; small VM extension |
| Stroke style differentiation (solid/dashed) for color-blind accessibility | **CLOSED in v3.4.0 MINOR** (5-style cycle via `ITracePalette.PickStrokeFor` + per-source `LineStyle StrokeStyle` on `TraceSource` + applied to `LineSeries.LineStyle` in chart wiring) |
| Cross-source Y-axis auto-scale coordination | v3.3.2 — `SyncYAxes()` method shipped (forward-looking, testable in isolation); production wiring from `RebuildSignalsAsync` deferred to v3.4.0 with chart series construction. **CLOSED in v3.3.2 PATCH** |
| `SignalChartViewModel.Palette` consolidation into `TableauPalette` (refactor, not deletion) | v3.3.2 / v3.4.0 — palette is live; extraction requires an additional API surface for live color reuse |
| Master dropdown + per-source radio (both present today) | v3.3.x — pick one; current dual UI is a v3.3.0 shipping compromise for keyboard + mouse parity |
| v1.6.0 MINOR OEM `IKeyDerivationAlgorithm` concrete | 44th consecutive list; crypto review needed |
| v2.2 🔜 ODX CONDITIONAL / ECU-VARIANT | Implementation still pending |

## Lessons

**0 NEW lessons.** v3.x PATCH pattern — surgical refactor, no
architectural shifts; hash determinism verified by test.

## Process notes

- **Branch:** `feature/v3-3-1-patch` (3 commits on top of v3.3.0
  `21a0ed47`).
- **Pre-ship review:** code-reviewer (sonnet) on whole-branch —
  pending end-of-pipeline.
- **Build verification:** `dotnet build PeakCan.Host.slnx -c Debug`
  → 0 warnings, 0 errors.
- **Test verification:** `dotnet test PeakCan.Host.slnx`
  → 1047 + 6 SKIP / 0 fail (1 transient race-flake hit during
  pre-ship, passes in isolation per documented pattern).
- **Ship mechanism:** Tier 3 (`tier3_v331.py` — clone of
  `tier3_v330.py`, PARENT_SHA `21a0ed47`, 4 file overlays, 1 squash,
  PATCH tag = non-FF).
