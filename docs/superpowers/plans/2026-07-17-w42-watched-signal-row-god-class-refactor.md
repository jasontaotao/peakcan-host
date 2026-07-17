# W42 Plan — WatchedSignalRow god-class refactor (32nd overall)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Note**: Per mid-W41 user feedback ("不要subagent driver了"), W42 execution mode is **inline** (NOT subagent-driven). Each task is implemented directly in this session with verification between tasks.

**Goal:** Reduce `src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs` from 266 LoC to ~88 LoC by extracting 3 NEW partials (`SignalContextFlow.partial.cs` + `LiveValueFlow.partial.cs` + `FormattedTextFlow.partial.cs`) into a new `WatchedSignalRow/` subdirectory, following the W3-W41 god-class refactor pattern.

**Architecture:** Sister of W22 RecentSessionsService + W34 DbcSendViewModel + W37 AscLocator + W38 ScriptViewModel + W39 DbcViewModel + W40 DbcSendViewModel 2nd cycle. Main stays with 6 init-only properties + 3 [ObservableProperty] backing fields + public ctor + computed SignalKey; 3 NEW partials host the 3 distinct responsibilities (SignalContext binding + LiveValue updates + FormattedText output). No public API change; no test change.

**Tech Stack:** C# .NET 10 / WPF / CommunityToolkit.Mvvm (partial-class source-gen friendly)

## Global Constraints

1. **Public API 100% preserved** — `WatchedSignalRow` class + ctor (10 params) + 6 init-only properties (`WatchId` + `CanIdHex` + `MessageName` + `SignalName` + `Unit` + `SourceId`) + 3 source-gen properties (`IsPlotted` + `FrameCount` + `IsPlaceholder`) + 6 plain properties (`Signal` + `Dbc` + `LatestValue` + `BlueLatestValue` + `BlueFrameCount` + `DeltaValue`) + 3 get-only text properties (`LatestText` + `BlueText` + `DeltaText`) + 1 computed `SignalKey` remain publicly callable from XAML / DI / tests.
2. **Tests must pass without modification** — full suite 1470 PASS / 0 FAIL / 3 SKIP maintained; `WatchedSignalRowTests` 10/10 + 3 sister tests (GreenLineAnchorFlow + TraceViewerViewModelFixtureIntegration + TraceViewerViewModelTests) all PASS.
3. **Partial keyword unchanged** — `public sealed partial class WatchedSignalRow : ObservableObject` (line 27) — already partial from v3.15.0 MINOR; do NOT edit.
4. **LoC formula W8.5 D7 32-locked** — delta must EXACTLY match range deletion counts; re-grep boundaries before each task.
5. **Re-extract verbatim from HEAD** — no fabricated code; use `git show HEAD:src/...cs | sed -n '<range>p'` for each partial's content.
6. **W20 + W23 LESSON APPLIED** — boundary verification + struct-ctor verification + verbatim re-extract. (W42 has NO struct ctor — only plain SetProperty calls — so struct-ctor verification N/A.)
7. **W19 R1 LESSON ENHANCED** — re-grep post-T(N-1) boundaries BEFORE running each deletion script.
8. **W39 T1 2-non-contiguous-block LESSON** — be alert to non-contiguous-block deletions. For W42 T1: `_signal` + `Signal` setter (L58-82) + `_dbc` + `Dbc` setter (L88-101) are continuous (L83-87 is xmldoc + blank lines ≤ 5 LoC gap) → SINGLE-block deletion suffices. **Re-verify Phase 1**.
9. **Build + filter tests after each task** — catch extraction errors immediately.
10. **v3.50.6 PATCH `_decimalDigits` cache stays with `Signal` setter** (SignalContextFlow.partial.cs) — NOT FormattedTextFlow, because cache is WRITE-time concern, not READ-time.
11. **v3.50.7 PATCH DeltaText INPC stays in `LatestValue` setter** (LiveValueFlow.partial.cs) — because that's the INPC fire site; FormattedTextFlow.partial.cs only has get-only properties that don't fire INPC.

---

## File Structure

```
src/PeakCan.Host.App/ViewModels/
├── WatchedSignalRow.cs                                (~88 LoC after W42; was 266)
└── WatchedSignalRow/                                  (NEW subdirectory)
    ├── SignalContextFlow.partial.cs                   (NEW ~52 LoC; _signal + _dbc + _decimalDigits + 2 setters)
    ├── LiveValueFlow.partial.cs                       (NEW ~69 LoC; _latestValue + _blueLatestValue + _blueFrameCount + 3 setters + DeltaValue)
    └── FormattedTextFlow.partial.cs                   (NEW ~57 LoC; LatestText + BlueText + DeltaText get-only)
```

Each partial hosts one responsibility. Main stays with bindable state + init-only properties + ctor + computed SignalKey.

---

### Task 0: Branch + spec verify + plan commit

**Files:**
- Verify: `docs/superpowers/specs/2026-07-17-w42-watched-signal-row-god-class-refactor.md` committed at `29807ef`
- Create: `docs/superpowers/plans/2026-07-17-w42-watched-signal-row-god-class-refactor.md`

- [ ] **Step 1: Verify spec is committed**

```bash
git log --oneline -3
```

Expected: `29807ef W42 spec: WatchedSignalRow god-class refactor (3 partials + 5-task roll-out, 32nd overall)` visible.

- [ ] **Step 2: Verify branch + baseline tests**

```bash
git status
git branch --show-current
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --nologo
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-build --nologo -c Debug --filter "FullyQualifiedName~WatchedSignalRow" --logger "console;verbosity=minimal"
```

Expected: branch = `feature/w42-watched-signal-row-god-class`; 0 errors, 6 pre-existing warnings (NOT W42-introduced); WatchedSignalRow filter tests 10/10 PASS.

- [ ] **Step 3: Capture exact pre-W42 line ranges**

```bash
grep -n "_signal\b\|_dbc\b\|_decimalDigits\|_latestValue\|_blueLatestValue\|_blueFrameCount\|public.*Signal\b\|public.*Dbc\b\|public.*LatestValue\|public.*BlueLatestValue\|public.*BlueFrameCount\|public.*DeltaValue\|public.*LatestText\|public.*BlueText\|public.*DeltaText" src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs
```

Expected: exact line numbers printed; capture as `SIGNAL_START..SIGNAL_END` (L50-101 per spec; re-verify), `LIVE_START..LIVE_END` (L102-170 per spec; re-verify), `TEXT_START..TEXT_END` (L171-227 per spec; re-verify). Save to `scripts/w42_ranges.txt`.

**CRITICAL re-verify**:
- `Signal` set property body: L65-82 (含 v3.50.6 PATCH `_decimalDigits` 缓存 + 3 OnPropertyChanged)
- `Dbc` set property body: L89-101 (含 3 OnPropertyChanged)
- `LatestValue` set property body: L113-135 (含 v3.50.7 PATCH DeltaText INPC + 4 OnPropertyChanged)
- `BlueLatestValue` set property body: L143-156 (含 3 OnPropertyChanged)
- `BlueFrameCount` set property body: L159-163 (5 LoC 简单 SetProperty)
- `DeltaValue` computed: L167-170
- `LatestText` get-only: L182-195
- `BlueText` get-only: L199-211
- `DeltaText` get-only: L216-227

- [ ] **Step 4: Commit plan**

```bash
git add docs/superpowers/plans/2026-07-17-w42-watched-signal-row-god-class-refactor.md
git commit -m "W42 plan: WatchedSignalRow god-class refactor (3 partials: SignalContextFlow + LiveValueFlow + FormattedTextFlow)"
```

---

### Task 1: SignalContextFlow partial extraction (~52 LoC)

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/WatchedSignalRow/SignalContextFlow.partial.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs:50-101` (L50-58 `_signal` field + xmldoc + `_decimalDigits` field + L65-82 `Signal` setter + xmldoc + L88 `_dbc` field + L89-101 `Dbc` setter)

**Interfaces:**
- Consumes: 6 init-only properties (`WatchId` + `CanIdHex` + `MessageName` + `SignalName` + `Unit` + `SourceId`) — all stay in main
- Produces: 2 plain private fields (`_signal` + `_dbc` + `_decimalDigits`) + 2 properties (`Signal` get/set + `Dbc` get/set)

**SPECIAL NOTE — Signal setter has 3 OnPropertyChanged cascade**: v3.50.6 PATCH caches `_decimalDigits` at signal-set time; OnPropertyChanged fires `LatestText` + `BlueText` + `DeltaText` (these are properties that live in FormattedTextFlow.partial.cs, but partial-class visibility means direct INPC call works fine — sister of W9.5 cross-partial-method-calls 3/3 LOCKED).

- [ ] **Step 1: Re-grep boundaries BEFORE running script**

```bash
grep -n "private.*_signal\|private.*_dbc\|private.*_decimalDigits\|public.*Signal\b\|public.*Dbc\b" src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs
```

Expected: 4 matches — `_signal` field (L58), `_decimalDigits` field (L63), `_dbc` field (L88), `Signal` property (L65) + `Dbc` property (L89). Capture exact start/end. Re-verify against `scripts/w42_ranges.txt` from Task 0 Step 3.

- [ ] **Step 2: Extract verbatim content from HEAD**

```bash
git show HEAD:src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs | sed -n '50,101p'
```

Expected: L50-58 (`_signal` field + xmldoc comment) + L60-63 (`_decimalDigits` field + xmldoc comment) + L65-82 (Signal get/set) + L83-87 (gap) + L88-101 (`_dbc` field + Dbc get/set). Save output to `scripts/w42_t1_signal_content.txt`.

**Note**: L83-87 contains xmldoc comments and blank lines — these can stay in main OR move with the partial (decision: MOVE with the partial, since they're contextually about Signal/Dbc; the line `/// </summary>` at L86 closes the L60-63 xmldoc for `_decimalDigits`).

- [ ] **Step 3: Create SignalContextFlow.partial.cs**

Write `src/PeakCan.Host.App/ViewModels/WatchedSignalRow/SignalContextFlow.partial.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class WatchedSignalRow
{
    // Flow: Signal context binding (Signal + Dbc + decimalDigits cache).
    // Methods moved verbatim from WatchedSignalRow.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - TraceViewerViewModel._signalByKey lookup on CollectionChanged (main caller)
    //   - OnPropertyChanged(nameof(LatestText|BlueText|DeltaText)) — fires INPC
    //     on properties that live in FormattedTextFlow.partial.cs (sister of
    //     W9.5 cross-partial-method-calls 3/3 LOCKED).
    //
    // v3.50.6 PATCH: _decimalDigits cache lives here (WRITE-time concern,
    // not READ-time). FormattedTextFlow reads via _decimalDigits directly
    // via partial-class visibility.

    /// <summary>v3.50.0 MINOR: cached DBC signal reference, populated by
    /// TraceViewerViewModel._signalByKey lookup on CollectionChanged.
    /// Enables SignalDecoder.DecodeRaw(this, frame.Data) per-row when
    /// green-line anchor refreshes (anchor-driven watch-sync Q1).
    /// Plain private field (no [ObservableProperty] source-gen) because
    /// the generated .g.cs file under the XAML temp csproj does not pull
    /// PeakCan.Host.Core.dll — using global:: still fails to resolve
    /// core types in the partial .g.cs.</summary>
    private PeakCan.Host.Core.Dbc.Signal? _signal;

    // v3.50.6 PATCH: cached minimum decimal digits derived from
    // _signal.Factor. Recomputed at Signal-set time (not per refresh
    // tick). Plain int field, sister of v3.50.0 _signal and v3.50.5 _dbc.
    private int _decimalDigits;

    public PeakCan.Host.Core.Dbc.Signal? Signal
    {
        get => _signal;
        set
        {
            if (SetProperty(ref _signal, value))
            {
                // v3.50.6 PATCH: cache digit count at signal-set time.
                // value is null → 0 digits (consistent with no-signal fallback).
                _decimalDigits = value is null
                    ? 0
                    : SignalFormatter.ResolveDecimalDigits(value.Factor);
                OnPropertyChanged(nameof(LatestText));
                OnPropertyChanged(nameof(BlueText));
                OnPropertyChanged(nameof(DeltaText));
            }
        }
    }

    // v3.50.5 PATCH: DBC document reference for VAL_ table lookups.
    // Sister of the v3.50.0 Signal field: plain C# (NOT [ObservableProperty])
    // because CommunityToolkit.Mvvm source-gen emits partial .g.cs into the
    // XAML temp csproj which cannot pull PeakCan.Host.Core.dll.
    private DbcDocument? _dbc;
    public DbcDocument? Dbc
    {
        get => _dbc;
        set
        {
            if (SetProperty(ref _dbc, value))
            {
                OnPropertyChanged(nameof(LatestText));
                OnPropertyChanged(nameof(BlueText));
                OnPropertyChanged(nameof(DeltaText));
            }
        }
    }
}
```

- [ ] **Step 4: Verify build before deletion**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --nologo
```

Expected: 0 errors (both partials declare duplicate members — that's the whole point of partial classes; build succeeds with duplicate declarations). Pre-existing 6 warnings unchanged.

- [ ] **Step 5: Delete from main via range removal script**

Write `scripts/w42_task1_delete_signalcontextflow.py`:

```python
#!/usr/bin/env python3
"""Delete SignalContextFlow range from WatchedSignalRow.cs per W42 Task 1."""
from pathlib import Path

path = Path("src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs")
text = path.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

# Exact range from Task 0 Step 3 re-grep (placeholder; T1 implementer MUST re-grep)
START = 50
END = 101

# Convert to 0-indexed
deleted = lines[START - 1:END]
print(f"W42 T1: deleting {len(deleted)} lines ({START}..{END})")

# W19 R1 LESSON: 2/3 loose assertion — warn if delta off by ±3
EXPECTED_DELTA = 52
actual_delta = len(deleted)
if abs(actual_delta - EXPECTED_DELTA) > 3:
    print(f"WARNING: W19 R1 — expected ~{EXPECTED_DELTA} LoC, got {actual_delta}")

remaining = lines[:START - 1] + lines[END:]
path.write_text("".join(remaining), encoding="utf-8")
print(f"W42 T1: main LoC now {sum(1 for _ in path.read_text(encoding='utf-8').splitlines())}")
```

Run:

```bash
python scripts/w42_task1_delete_signalcontextflow.py
```

Expected: prints ~52 LoC deleted; main LoC count drops from 266 → ~214.

- [ ] **Step 6: Build + run WatchedSignalRow tests**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --nologo
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-build --nologo -c Debug --filter "FullyQualifiedName~WatchedSignalRow" --logger "console;verbosity=minimal"
```

Expected: 0 errors, 6 pre-existing warnings; ALL WatchedSignalRow tests PASS (10/10 same count as Task 0 Step 2 baseline).

- [ ] **Step 7: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/WatchedSignalRow/SignalContextFlow.partial.cs src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs scripts/w42_task1_delete_signalcontextflow.py
git commit -m "W42 T1: WatchedSignalRow SignalContextFlow partial extraction (~52 LoC; _signal + _dbc + _decimalDigits + 2 setters)"
```

---

### Task 2: LiveValueFlow partial extraction (~69 LoC)

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/WatchedSignalRow/LiveValueFlow.partial.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs` (post-T1 ranges for `_latestValue` + `LatestValue` setter + `_blueLatestValue` + `BlueLatestValue` setter + `_blueFrameCount` + `BlueFrameCount` setter + `DeltaValue`)

**Interfaces:**
- Consumes: 3 init-only properties (`WatchId` + `CanIdHex` + `SignalName` + `SourceId`) — used only by `SignalKey` computed in main; this flow does NOT touch init-only
- Produces: 3 plain private fields (`_latestValue` + `_blueLatestValue` + `_blueFrameCount`) + 3 properties (`LatestValue` get/set + `BlueLatestValue` get/set + `BlueFrameCount` get/set) + 1 computed (`DeltaValue`)

**SPECIAL NOTE — LatestValue setter has 4-property OnPropertyChanged cascade**: v3.50.7 PATCH fires DeltaValue + LatestText + BlueText + DeltaText INPC. LatestText/BlueText/DeltaText live in FormattedTextFlow.partial.cs (not yet extracted at T2 time) — INPC fires across partials via standard OnPropertyChanged(string) call, sister of W9.5 + W21 cross-partial method visibility.

- [ ] **Step 1: Re-grep post-T1 boundaries**

```bash
grep -n "_latestValue\|_blueLatestValue\|_blueFrameCount\|public.*LatestValue\|public.*BlueLatestValue\|public.*BlueFrameCount\|public.*DeltaValue" src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs
```

Expected: capture exact start/end for `_latestValue` field + `LatestValue` property + `_blueLatestValue` field + `BlueLatestValue` property + `_blueFrameCount` field + `BlueFrameCount` property + `DeltaValue` computed. **CRITICAL**: re-verify because T1 changed line numbers.

- [ ] **Step 2: Extract verbatim content from HEAD (post-T1 commit)**

```bash
git show HEAD:src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs | sed -n '<LIVE_START>,<LIVE_END>p'
```

Expected: `_latestValue` field + `LatestValue` property + `_blueLatestValue` field + `BlueLatestValue` property + `_blueFrameCount` field + `BlueFrameCount` property + `DeltaValue` computed. Save output to `scripts/w42_t2_live_content.txt`.

- [ ] **Step 3: Create LiveValueFlow.partial.cs**

Write `src/PeakCan.Host.App/ViewModels/WatchedSignalRow/LiveValueFlow.partial.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class WatchedSignalRow
{
    // Flow: Live value updates (LatestValue + BlueLatestValue + BlueFrameCount
    // setters + DeltaValue computed). Methods moved verbatim from
    // WatchedSignalRow.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - RefreshFrameCounts (main caller, sets FrameCount + LatestValue)
    //   - BlueLatestValue setter (TraceViewerViewModel, sets on blue-line
    //     anchor refresh)
    //   - OnPropertyChanged(nameof(LatestText|BlueText|DeltaText)) — fires
    //     INPC on FormattedTextFlow.partial.cs properties (sister of W9.5).
    //
    // v3.50.7 PATCH: LatestValue setter fires 4-property OnPropertyChanged
    // cascade (DeltaValue + LatestText + BlueText + DeltaText). The user
    // reported stale DeltaText from prior _latestValue setter (screenshot
    // 2026-07-16: B2V_Ucel1_N Latest=3.395, Blue=3.346, Δ=-0.007 when
    // true diff was 0.049) — this PATCH is the fix.

    /// <summary>Last decoded value across the watched source(s). Set
    /// once at AddToWatch + refreshed when ASC reloads. NaN when no
    /// frames exist yet (DBC loaded but no ASC).</summary>
    private double _latestValue = double.NaN;
    public double LatestValue
    {
        get => _latestValue;
        set
        {
            if (SetProperty(ref _latestValue, value))
            {
                OnPropertyChanged(nameof(DeltaValue));
                OnPropertyChanged(nameof(LatestText));
                // v3.50.7 PATCH: Δ column binds to DeltaText (string), not
                // DeltaValue (double). Without this INPC, dragging the
                // green anchor updates LatestText but leaves DeltaText
                // showing the value computed against the previous
                // _latestValue (user screenshot 2026-07-16: B2V_Ucel1_N
                // Latest=3.395, Blue=3.346, Δ=-0.007 when true diff was
                // 0.049 — stale DeltaText from a prior BlueLatestValue
                // setter call). Sister pattern of v3.50.2 DeltaValue
                // INPC; extends it to the v3.50.5-introduced string
                // sibling.
                OnPropertyChanged(nameof(DeltaText));
            }
        }
    }

    // === v3.50.2 PATCH T2: blue-line + Delta column ===
    // Sister pattern of v3.50 Signal reference: plain property (NOT
    // [ObservableProperty]) because CommunityToolkit.Mvvm source-gen
    // emits partial .g.cs into XAML temp csproj which can't pull
    // PeakCan.Host.Core.dll. SetProperty inline instead.

    private double _blueLatestValue = double.NaN;
    public double BlueLatestValue
    {
        get => _blueLatestValue;
        set
        {
            if (SetProperty(ref _blueLatestValue, value))
            {
                OnPropertyChanged(nameof(DeltaValue));
                OnPropertyChanged(nameof(BlueText));
                OnPropertyChanged(nameof(DeltaText));
            }
        }
    }

    private int _blueFrameCount;
    public int BlueFrameCount
    {
        get => _blueFrameCount;
        set => SetProperty(ref _blueFrameCount, value);
    }

    /// <summary>Computed Delta = BlueLatest - Green Latest. NaN when
    /// either side is NaN. Watch list DataGrid binds this column.</summary>
    public double DeltaValue =>
        double.IsNaN(_blueLatestValue) || double.IsNaN(LatestValue)
            ? double.NaN
            : _blueLatestValue - LatestValue;
}
```

- [ ] **Step 4: Verify build before deletion**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --nologo
```

Expected: 0 errors. Pre-existing 6 warnings unchanged.

- [ ] **Step 5: Delete from main via range removal script**

Write `scripts/w42_task2_delete_livevalueflow.py`:

```python
#!/usr/bin/env python3
"""Delete LiveValueFlow range from WatchedSignalRow.cs per W42 Task 2."""
from pathlib import Path

path = Path("src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs")
text = path.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

# Exact range from Task 2 Step 1 re-grep (placeholder; T2 implementer MUST re-grep)
START = <LIVE_START>  # post-T1 re-grep
END = <LIVE_END>      # post-T1 re-grep

# Convert to 0-indexed
deleted = lines[START - 1:END]
print(f"W42 T2: deleting {len(deleted)} lines ({START}..{END})")

# W19 R1 LESSON: 2/3 loose assertion — warn if delta off by ±3
EXPECTED_DELTA = 69
actual_delta = len(deleted)
if abs(actual_delta - EXPECTED_DELTA) > 3:
    print(f"WARNING: W19 R1 — expected ~{EXPECTED_DELTA} LoC, got {actual_delta}")

remaining = lines[:START - 1] + lines[END:]
path.write_text("".join(remaining), encoding="utf-8")
print(f"W42 T2: main LoC now {sum(1 for _ in path.read_text(encoding='utf-8').splitlines())}")
```

Run:

```bash
python scripts/w42_task2_delete_livevalueflow.py
```

Expected: prints ~69 LoC deleted; main LoC count drops from ~214 → ~145.

- [ ] **Step 6: Build + run WatchedSignalRow tests**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --nologo
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-build --nologo -c Debug --filter "FullyQualifiedName~WatchedSignalRow" --logger "console;verbosity=minimal"
```

Expected: 0 errors, 6 pre-existing warnings; ALL WatchedSignalRow tests PASS (10/10).

- [ ] **Step 7: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/WatchedSignalRow/LiveValueFlow.partial.cs src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs scripts/w42_task2_delete_livevalueflow.py
git commit -m "W42 T2: WatchedSignalRow LiveValueFlow partial extraction (~69 LoC; _latestValue + _blueLatestValue + _blueFrameCount + 3 setters + DeltaValue)"
```

---

### Task 3: FormattedTextFlow partial extraction (~57 LoC)

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/WatchedSignalRow/FormattedTextFlow.partial.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs` (post-T2 ranges for `LatestText` + `BlueText` + `DeltaText` get-only properties)

**Interfaces:**
- Consumes: 3 plain private fields (`_signal` + `_dbc` + `_decimalDigits` — defined in SignalContextFlow.partial.cs) + 2 plain private fields (`_latestValue` + `_blueLatestValue` — defined in LiveValueFlow.partial.cs) — all accessed via partial-class visibility
- Produces: 3 get-only string-formatted properties (`LatestText` + `BlueText` + `DeltaText`)

**SPECIAL NOTE — get-only properties only**: This partial has ZERO setter methods, ZERO private fields of its own, ZERO [ObservableProperty] source-gen. It is a pure read-side formatter. Sister of W19/W21/W22/W24/W37/W38/W39/W40 100% pure read partials.

- [ ] **Step 1: Re-grep post-T2 boundaries**

```bash
grep -n "public.*LatestText\|public.*BlueText\|public.*DeltaText" src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs
```

Expected: 3 matches — `LatestText` (line ~182), `BlueText` (line ~199), `DeltaText` (line ~216). Capture exact start/end. **CRITICAL**: re-verify because T2 changed line numbers.

- [ ] **Step 2: Extract verbatim content from HEAD (post-T2 commit)**

```bash
git show HEAD:src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs | sed -n '<TEXT_START>,<TEXT_END>p'
```

Expected: `LatestText` get-only + `BlueText` get-only + `DeltaText` get-only (3 properties total). Save output to `scripts/w42_t3_text_content.txt`.

- [ ] **Step 3: Create FormattedTextFlow.partial.cs**

Write `src/PeakCan.Host.App/ViewModels/WatchedSignalRow/FormattedTextFlow.partial.cs`:

```csharp
using PeakCan.Host.App.Composition.Converters;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class WatchedSignalRow
{
    // Flow: String-formatted text columns for XAML binding (LatestText +
    // BlueText + DeltaText get-only). Methods moved verbatim from
    // WatchedSignalRow.cs.
    //
    // Cross-flow state accessed via partial-class visibility:
    //   - _signal + _dbc + _decimalDigits (SignalContextFlow.partial.cs)
    //   - _latestValue + _blueLatestValue (LiveValueFlow.partial.cs)
    //
    // Pure read-side formatter: ZERO setter, ZERO [ObservableProperty],
    // ZERO plain private field of its own. v3.50.5 PATCH introduced these
    // string properties to replace the XAML-side DoubleNanToStr converter;
    // the .Text properties handle NaN formatting internally.
    //
    // G4: enum signals have no subtractable semantics between text labels;
    // DeltaText returns Placeholder when _signal?.ValueTableName is not null.

    /// <summary>Decoded Latest value as a string for XAML binding.
    /// Prefers DBC VAL_ table text when available; falls back to F2 numeric.</summary>
    public string LatestText
    {
        get
        {
            if (IsPlaceholder || double.IsNaN(_latestValue)) return DoubleNanToStringConverter.Placeholder;
            if (_signal is not null && _dbc is not null)
            {
                var text = SignalDecoder.TryDecodeEnumText(_signal, _latestValue, _dbc);
                if (text is not null) return text;
            }
            // v3.50.6 PATCH: factor-derived precision replaces F2.
            return SignalFormatter.FormatValue(_decimalDigits, _latestValue);
        }
    }

    /// <summary>Decoded Blue (comparison anchor) value as a string for XAML binding.
    /// Same fallback semantics as <see cref="LatestText"/>.</summary>
    public string BlueText
    {
        get
        {
            if (double.IsNaN(_blueLatestValue)) return DoubleNanToStringConverter.Placeholder;
            if (_signal is not null && _dbc is not null)
            {
                var text = SignalDecoder.TryDecodeEnumText(_signal, _blueLatestValue, _dbc);
                if (text is not null) return text;
            }
            return SignalFormatter.FormatValue(_decimalDigits, _blueLatestValue);
        }
    }

    /// <summary>Δ as a string for XAML binding. Enum signals show "—" (no
    /// subtractable semantics between text labels); numeric signals show
    /// F2 diff. NaN → "—" placeholder.</summary>
    public string DeltaText
    {
        get
        {
            if (double.IsNaN(_latestValue) || double.IsNaN(_blueLatestValue))
                return DoubleNanToStringConverter.Placeholder;
            // G4: enum signals have no subtractable semantics between text labels.
            if (_signal?.ValueTableName is not null)
                return DoubleNanToStringConverter.Placeholder;
            return SignalFormatter.FormatValue(_decimalDigits, _blueLatestValue - _latestValue);
        }
    }
}
```

- [ ] **Step 4: Verify build before deletion**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --nologo
```

Expected: 0 errors. Pre-existing 6 warnings unchanged.

- [ ] **Step 5: Delete from main via range removal script**

Write `scripts/w42_task3_delete_formattedtextflow.py`:

```python
#!/usr/bin/env python3
"""Delete FormattedTextFlow range from WatchedSignalRow.cs per W42 Task 3."""
from pathlib import Path

path = Path("src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs")
text = path.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

# Exact range from Task 3 Step 1 re-grep (placeholder; T3 implementer MUST re-grep)
START = <TEXT_START>  # post-T2 re-grep
END = <TEXT_END>      # post-T2 re-grep

# Convert to 0-indexed
deleted = lines[START - 1:END]
print(f"W42 T3: deleting {len(deleted)} lines ({START}..{END})")

# W19 R1 LESSON: 2/3 loose assertion — warn if delta off by ±3
EXPECTED_DELTA = 57
actual_delta = len(deleted)
if abs(actual_delta - EXPECTED_DELTA) > 3:
    print(f"WARNING: W19 R1 — expected ~{EXPECTED_DELTA} LoC, got {actual_delta}")

remaining = lines[:START - 1] + lines[END:]
path.write_text("".join(remaining), encoding="utf-8")
print(f"W42 T3: main LoC now {sum(1 for _ in path.read_text(encoding='utf-8').splitlines())}")
```

Run:

```bash
python scripts/w42_task3_delete_formattedtextflow.py
```

Expected: prints ~57 LoC deleted; main LoC count drops from ~145 → ~88.

- [ ] **Step 6: Build + run full test suite**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --nologo
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-build --nologo -c Debug --filter "FullyQualifiedName~WatchedSignalRow|FullyQualifiedName~TraceViewerViewModel" --logger "console;verbosity=minimal"
```

Expected: 0 errors, 6 pre-existing warnings; ALL WatchedSignalRow + TraceViewerViewModel tests PASS (10 + ~50 = ~60 tests, no regressions).

- [ ] **Step 7: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/WatchedSignalRow/FormattedTextFlow.partial.cs src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs scripts/w42_task3_delete_formattedtextflow.py
git commit -m "W42 T3: WatchedSignalRow FormattedTextFlow partial extraction (~57 LoC; LatestText + BlueText + DeltaText get-only)"
```

---

### Task 4: v3.60.0 → v3.61.0 MINOR + release notes

**Files:**
- Modify: `src/Directory.Build.props` (bump `<Version>3.60.0</Version>` to `<Version>3.61.0</Version>`)
- Create: `docs/release-notes-v3-61-0-minor.md`

- [ ] **Step 1: Bump version in Directory.Build.props**

```bash
grep -n "Version" src/Directory.Build.props | head -3
```

Expected: `<Version>3.60.0</Version>` (post-v3.60.x baseline).

- [ ] **Step 2: Edit Directory.Build.props**

Replace `3.60.0` with `3.61.0` (or current major.minor value, whichever is latest at Task 4 execution time).

- [ ] **Step 3: Write release notes**

Write `docs/release-notes-v3-61-0-minor.md` (mirror W39 release notes format):

```markdown
# v3.61.0 MINOR — WatchedSignalRow god-class refactor (32nd overall)

## 概述

W42 god-class refactor 收尾,3 NEW partials 把 `WatchedSignalRow` 从 266 → ~88 LoC 拆分到 `WatchedSignalRow/` subdirectory。Public API + tests + DI 全部不变。

## 主类变更

`src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs` 266 → ~88 LoC (-178 LoC, -66.9%)

## 3 NEW partials

- `src/PeakCan.Host.App/ViewModels/WatchedSignalRow/SignalContextFlow.partial.cs` (~52 LoC; `_signal` + `_dbc` + `_decimalDigits` + `Signal`/`Dbc` setters)
- `src/PeakCan.Host.App/ViewModels/WatchedSignalRow/LiveValueFlow.partial.cs` (~69 LoC; `_latestValue` + `_blueLatestValue` + `_blueFrameCount` + 3 setters + `DeltaValue` computed)
- `src/PeakCan.Host.App/ViewModels/WatchedSignalRow/FormattedTextFlow.partial.cs` (~57 LoC; `LatestText` + `BlueText` + `DeltaText` get-only)

## Architecture milestones

- **32nd god-class refactor SHIPPED** (W3-W42 系列)
- **13th App/ViewModels-layer** (sister of W37/W38/W39)
- **27th subdirectory-pattern deployment**
- **1st cycle** (全新拆分,v3.15.0 MINOR 引入后从未拆分过)
- **Cumulative LoC reduction**: -3,787 (W3-W41) + W42 -178 = **-3,965 LoC total** across 32 refactors

## 测试

- WatchedSignalRow filter: 10/10 PASS
- TraceViewerViewModel filter: ~50/50 PASS
- App.Tests full suite: 854/854 PASS / 0 FAIL / 3 SKIP
- Total: 1470/1470 PASS / 0 FAIL / 3 SKIP

## 不变

- Public API 100% 保留 (10-param ctor + 6 init-only + 3 [ObservableProperty] + 6 plain + 3 get-only + 1 computed = 19 properties)
- DI 注册不变
- XAML binding 11 处全部不变
- 9 PATCH history (v3.15.0 / v3.50.0 / v3.50.2 / v3.50.5 / v3.50.6 / v3.50.7) 完整保留
```

- [ ] **Step 4: Commit**

```bash
git add src/Directory.Build.props docs/release-notes-v3-61-0-minor.md
git commit -m "W42 T4: bump to v3.61.0 MINOR + release notes"
```

---

### Task 5: Tier-3 ship

**Steps:**

- [ ] **Step 1: Final full build + full test suite**

```bash
dotnet build "D:/claude_proj2/peakcan-host/PeakCan.Host.sln" -c Debug --nologo
dotnet test "D:/claude_proj2/peakcan-host/PeakCan.Host.sln" --no-build --nologo -c Debug --logger "console;verbosity=minimal"
```

Expected: 0 errors, 6 pre-existing warnings; **1470 PASS / 0 FAIL / 3 SKIP** (same as v3.59.0 baseline).

- [ ] **Step 2: gh pr create**

```bash
gh pr create --base main --head feature/w42-watched-signal-row-god-class --title "v3.61.0 MINOR: WatchedSignalRow god-class refactor (32nd overall; 3 NEW partials; main -66.9% LoC)" --body "## 概述

W42 god-class refactor — WatchedSignalRow 266 → ~88 LoC。

## 3 NEW partials

- SignalContextFlow (~52 LoC)
- LiveValueFlow (~69 LoC)
- FormattedTextFlow (~57 LoC)

## Architecture milestones

- 32nd god-class refactor SHIPPED
- 13th App/ViewModels-layer
- 27th subdirectory-pattern deployment
- 1st cycle (1st 拆分)

## 测试

- WatchedSignalRow: 10/10 PASS
- TraceViewerViewModel: ~50/50 PASS
- Total: 1470/1470 PASS / 0 FAIL / 3 SKIP

🤖 Generated with [Claude Code](https://claude.com/claude-code)"
```

Expected: PR URL printed.

- [ ] **Step 3: Wait for CI + review feedback**

(If CI fails or reviewer requests changes, fix in this branch before merge.)

- [ ] **Step 4: Squash-merge PR + delete branch**

```bash
gh pr merge --squash --delete-branch
```

Expected: PR merged at v3.61.0 commit on main; `feature/w42-watched-signal-row-god-class` branch auto-deleted.

- [ ] **Step 5: Tag v3.61.0 + push to origin**

```bash
git checkout main
git pull
git tag v3.61.0
git push origin v3.61.0
```

Expected: tag `v3.61.0` at PR squash-commit on main, pushed to origin.

- [ ] **Step 6: Create GitHub release**

```bash
gh release create v3.61.0 --title "v3.61.0 MINOR — WatchedSignalRow god-class refactor (32nd overall)" --notes-file docs/release-notes-v3-61-0-minor.md
```

Expected: GH release published at https://github.com/jasontaotao/peakcan-host/releases/tag/v3.61.0.

- [ ] **Step 7: Dispatch pkm-capture (1st capture this ship cycle)**

Dispatch `vault-pkm:pkm-capture` agent in background to record W42 ship closure per the global 节流 rule (1 capture per ship-completion). Provide: SPEC + PLAN + 3 source commits + 1 docs commit + test results + 6 NEW 1/3 candidates.

---

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 6 pre-existing warnings (sister of W41 baseline)
- `dotnet test --filter "FullyQualifiedName~WatchedSignalRow"`: 10/10 PASS
- `dotnet test --filter "FullyQualifiedName~TraceViewerViewModel"`: ~50/50 PASS (sister of W41)
- `dotnet test` (full solution): 1470 PASS / 0 FAIL / 3 SKIP
- `wc -l src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs` ≤ 95 LoC (target ~88)
- 3 NEW partial files in `WatchedSignalRow/` directory
- 6 init-only properties remain in main (sister of W21/W22/W23/W24)
- 3 [ObservableProperty] backing fields remain in main (sister of W19/W21/W22)
- 1 public ctor (10 params) remains in main (sister of W18/W19/W20/W21/W22/W23/W24 D5)
- 1 computed `SignalKey` remains in main (sister of W22/W23/W24)
- 6 plain private fields (`_signal`/`_dbc`/`_decimalDigits`/`_latestValue`/`_blueLatestValue`/`_blueFrameCount`) move to per-flow partials
- 9 properties body methods move to per-flow partials (sister of W19/W21/W22/W23/W24/W39/W40)
- XAML bindings unchanged (all 11 XAML bindings to public properties remain valid)
- DI registration unchanged
- Tag v3.61.0 + GH release published
- Branch deleted post-merge

## Out of scope (YAGNI)

- No public/internal API change.
- No test changes (10 WatchedSignalRowTests + sister tests pass without modification).
- No facade pattern (W3-W41 CONFIRMED direct partial-class visibility).
- No xmldoc-grep risk (verified — tests do not path-grep main file content).
- No W18 R1 mitigation (zero `[LoggerMessage]` partials → no CS8795 risk; zero `[RelayCommand]` → no attribute travel).
- No struct-ctor verification (zero `CanId` / `CanFrame` / `CanFrameTimestamp` usage → W23 LESSON N/A).
- `IsPlaceholder` `[ObservableProperty]` stays in main (sister of W19/W21/W22 — bindable INPC state stays).
- No `WatchedSignalRow` constructor change (10 params unchanged; 0 LoC touch).
- No `WatchedSignalRow` computed `SignalKey` change (sister of W19/W22/W23 — mapping helper stays in main).
- `DbcTokenizer` / `SendViewModel` / `BlfParser` / `AppShellViewModel` god-class refactor candidates — explicitly out of scope (deferred to W43+).
- `RequestBasedMappers` god-class refactor — out of scope (W42 discovered `public static class` cannot be partial, so not a valid W42 target; deferred to W43+ alternative).

## Sister-lesson candidates to monitor

| Lesson | Status | What W42 might observe |
|---|---|---|
| `partial-extraction-must-use-original-code-from-head-not-fabricated-api` | 3/3 CONFIRMED (W21) | W42 4th+ application (T1+T2+T3 = 3 re-extractions) |
| `add-partial-keyword-to-monolithic-class-before-extraction` | 3/3 CONFIRMED (W21) | Held (W42 class already partial since v3.15.0 MINOR) |
| `subdirectory-partials-pattern-empirical-26-precedents` | 3/3 CONFIRMED (W20) | W42 27th deployment |
| `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` | 3/3 CONFIRMED (W24) | N/A (zero struct ctors in WatchedSignalRow) |
| `cross-partial-method-calls-resolve-identically-to-in-class-calls` | 3/3 CONFIRMED (W9.5) | W42 14th confirmation (OnPropertyChanged across 3 partials) |
| `plain-private-fields-with-setproperty-and-onpropertychanged-cascade-can-move-to-their-own-partial-when-they-form-a-distinct-responsibility-cluster` | NEW W42 1/3 | W42 1st observation: `_signal` + `_dbc` + `Signal`/`Dbc` setters move as SignalContextFlow |
| `get-only-string-formatting-properties-can-move-to-their-own-partial-when-they-form-a-distinct-text-output-cluster` | NEW W42 1/3 | W42 1st observation: 3 get-only string properties FormattedTextFlow |
| `live-value-setter-with-4-property-onpropertychanged-cascade-is-single-responsibility-for-numeric-state-flow` | NEW W42 1/3 | W42 1st observation: v3.50.7 PATCH 4-property INPC in LatestValue setter |
| `decimal-digits-cache-field-can-move-with-the-signal-property-not-with-formatted-text` | NEW W42 1/3 | W42 1st observation: `_decimalDigits` cache lives in SignalContextFlow (write-time) not FormattedTextFlow (read-time) |
| `3-partial-subdirectory-pattern-empirical-w37-w38-w39-w42` | NEW at W42 1/3 | 4 sister: W37 + W38 + W39 + W42 all 3-partial subdirectory |
