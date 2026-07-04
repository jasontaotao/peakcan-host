# peakcan-host v3.4.3 PATCH — Per-source CanIdFilter override

## Summary

v3.4.3 PATCH extends the v3.4.2 global `CanIdFilter` with a **per-source
override**: each loaded trace now carries its own filter string in the
legend strip. Empty per-source filter = inherit the global; non-empty =
override the global for that source. The Trace Viewer's chart + signal
list now reflect each source independently.

## Architecture

**`TraceSource` becomes a class** with manual `INotifyPropertyChanged` on
a single mutable field (`CanIdFilter`). The other 5 fields stay
init-only to preserve v3.2.0-v3.4.2 back-compat at the
`TraceSessionRegistry.LoadAsync` construction site. Manual INPC (no
`CommunityToolkit.Mvvm` dependency) keeps the file Core-agnostic.

**Why class + INPC, not the preplan agent's "registry-setter swap the
record" approach:** the registry-setter approach would re-fire
`SourcesChanged` on every keystroke in the per-source TextBox, causing a
full registry-wide rebuild (re-binding `_allServices`, re-attaching
service handlers, etc.). INPC on the source instance is scoped: only
the source's `CanIdFilter` changed, only that source's chart slots
rebuild via `RebuildSignalsCore`.

**`CanIdFilterParser.Parse` — reused verbatim** for both global (v3.4.2
path) and per-source (v3.4.3 path). Inheritance ("empty per-source =
inherit global") is decided in `RebuildSignalsCore`'s bucket loops:
`var perSourceAllowed = CanIdFilterParser.Parse(source.CanIdFilter); var
allowed = perSourceAllowed ?? globalAllowed;` — null-fill in the parser,
inherit at the call site.

**`RebuildSignalsCore` per-source resolution:** the existing two bucket
loops (global frame bucket + per-source chart-series re-group) each
parse the per-source filter at the top of their outer iteration and
fall back to `globalAllowed` when per-source is null. Behavior:
- Global = "0x100", Source A = "" → Source A inherits "0x100".
- Global = "0x100", Source A = "0x200" → Source A uses "0x200".
- Global = "", Source A = "0x200" → Source A uses "0x200".
- Global = "", Source A = "" → both null → no filter (show all).

## Implementation notes

### Plan deviations (all caught at TDD-RED, resolved during implementation)

1. **TraceSource location**: Plan claimed `src/PeakCan.Host.Core/Replay/`;
   actual is `src/PeakCan.Host.App/Services/Trace/`. The OxyPlot type
   dependency (`OxyColor`, `LineStyle`) keeps the type App-layer. Test
   file placed in `tests/.../App.Tests/Services/Trace/` accordingly.
   The preplan analog scan got the path right; the plan inherited a
   stale Core/Replay path from earlier v3.x plans. **Fix in plan
   template for v3.4.4+**: check `find . -name TraceSource.cs` before
   writing paths.

2. **`StrokeStyleInit` dead alias**: Plan code snippet accidentally
   included `public LineStyle StrokeStyleInit { get => StrokeStyle; }`
   — a useless alias that was never consumed anywhere in the repo
   (Grep zero callers). Caught at pre-ship review (HIGH finding);
   removed in ship commit via amend.

3. **`CanIdFilter_Set_SameValue_DoesNotFire` test reshape**: Plan
   asserted null-coerce-then-no-fire; C# compiler rejected
   `source.CanIdFilter = null` (property typed `string`, not `string?`).
   Test reshaped to assert "two idempotent + one different-value set =
   exactly 1 fire", which is the meaningful INPC contract: same-value
   sets don't add to the fire count.

4. **`PerSourceFilter_NonEmptyOverridesGlobal_*` baseline counts**:
   Plan asserted 2 chart series at baseline; actual is 4 (2 sources ×
   2 DBC messages × 1 signal each = 4 series per the established
   chart-wiring convention). Test corrected; behavior under test
   (per-source override) is unaffected.

## Files (5 modified-or-new + 1 release notes)

| Path | Δ | Purpose |
|------|---|---------|
| `src/PeakCan.Host.App/Services/Trace/TraceSource.cs` | rewrite (~68 LOC, was ~30) | Record → class + manual INPC on `CanIdFilter` |
| `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` | +38 / -2 | INPC subscribe/unsubscribe + per-source filter resolution |
| `src/PeakCan.Host.App/Views/TraceViewerView.xaml` | +6 / 0 | Per-row TextBox in legend strip DataTemplate |
| `tests/PeakCan.Host.App.Tests/Services/Trace/TraceSourceTests.cs` | NEW +76 | 3 INPC unit tests |
| `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelCanIdFilterTests.cs` | +145 / 0 | 3 per-source integration tests |
| `docs/release-notes-v3.4.3.md` | NEW (this file) | Release notes |

**Test delta:**

| Suite | v3.4.2 | v3.4.3 | Δ |
|-------|--------|--------|---|
| App | 586 | **592** | **+6 net** |
| Core | 393 | 393 | 0 |
| Infrastructure | 84 | 84 | 0 |
| **Total** | **1063 + 6 SKIP** | **1069 + 6 SKIP** | **+6 net** |

3 new INPC tests + 3 new per-source integration tests, all PASS first
try (after the 3 plan-deviation corrections detailed above).

## Pre-ship review

| Severity | Count | Notes |
|----------|-------|-------|
| CRITICAL | 0 | — |
| HIGH | 0 | — (1 HIGH finding fixed pre-ship: `StrokeStyleInit` removal) |
| MEDIUM | 0 | — |
| LOW | 1 | Same `UpdateSourceTrigger=PropertyChanged` lag exposure as v3.4.2 (per-keystroke chart rebuild); YAGNI per v3.4.3 plan |
| **Verdict** | **APPROVE_WITH_FIXES → APPROVE** | After 1-line `StrokeStyleInit` removal. |

## Backwards compatibility

- All existing `new TraceSource(...)` callsites compile unchanged
  (explicit ctor mirrors the v3.2.0 positional record shape; default
  `StrokeStyle = LineStyle.Solid` preserved).
- v3.4.2 global filter UX unchanged (per-source UI is purely
  additive — the global TextBox + Clear button remain in the toolbar).
- v3.2.0-v3.4.0 single-trace / multi-trace trace workflows behave
  identically when per-source filters are empty (the default).
- **Loss: record auto-equality.** `TraceSource` is now a regular class,
  no auto-generated value equality. No code in the project compared
  `TraceSource` instances for equality (only `SourceId` strings) — safe.
- New `System.ComponentModel` `using` directive in `TraceSource.cs`;
  transitive BCL dependency, no new package.

## Non-scope (intentional deferral)

| Item | Defer to | Why |
|------|----------|-----|
| **`.tmtrace` bundle file format** (save/restore multi-trace session + per-source filters) | **v3.5.0 MINOR** | Bigger feature (serialization + UI) |
| **`CanIdFilterParser` move to Core for DRY with Replay tab** | **v3.4.4 PATCH candidate** | Replay uses different parser (error UX vs silent skip); unifying is a UX-affecting change |
| **`ITraceViewerService.CanIdFilter` model-level wire-up** | YAGNI | Existing bucket-loop pattern is the v3.4.2/v3.4.3 convention; service-level filter is the Replay tab's mechanism |
| **Negative filters** ("hide ID 0x100") | YAGNI | Users can type the allow-list they want |
| **Per-source filter UI indicator** (chip showing which sources have non-empty filters) | YAGNI | The TextBox content alone is the indicator |

## Lessons

**0 NEW lessons** for v3.4.3. The TDD-RED plan-spec-bug catch
mechanism is now demonstrated across **2 consecutive PATCHes**
(v3.4.2 caught `AllowHexSpecifier` invalid; v3.4.3 caught wrong file
path + dead alias property). Re-affirming this as a durable pattern.

The v3.4.2 lesson `check-pre-existing-analogs-before-planning` was
correctly applied — preplan agent dispatched BEFORE plan draft,
surfaced 5 existing filter analogs (1 hidden) and informed the
per-source design. **Plan accuracy materially higher than v3.4.2** —
fewer deviations to resolve at RED, zero at ship.
