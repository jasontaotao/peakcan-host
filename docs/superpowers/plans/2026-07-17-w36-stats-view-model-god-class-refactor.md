# W36 Implementation Plan — StatsViewModel god-class refactor (21st overall)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `StatsViewModel.cs` from 263 LoC to ~128 LoC main + 2 NEW partials (`PlotFlow.partial.cs` + `SamplingFlow.partial.cs`). Public API + tests + DI registration unchanged.

**Architecture:** Subdirectory-partials pattern (sister of W34 DbcSendViewModel). The OxyPlot PlotModel setup + LineSeries creation (ctor body, ~85 LoC) moves to `PlotFlow.partial.cs`. The Push dispatcher hop + Apply rolling-window maintenance + LineSeries.Points rebuild (~50 LoC) moves to `SamplingFlow.partial.cs`. Main retains 4 readonly public properties + 2 [ObservableProperty] backing fields + 2 private readonly LineSeries fields + 1 const.

**Tech Stack:** C# .NET 10 / WPF / CommunityToolkit.Mvvm / OxyPlot 2.2.0 / FluentAssertions / NSubstitute / xUnit

## Global Constraints

来自 `docs/superpowers/specs/2026-07-17-w36-stats-view-model-god-class-refactor.md` + W20/W23 sister LESSONs：

- Public API 100% 保留（FpsSeries + LoadSeries + PlotModel + TotalFrames + ErrorFrames + InvalidatePlotCallCount + Push + StatsViewModel()）
- DI 注册不变（`services.AddSingleton<StatsViewModel>()` at `ViewModelsBatch2Flow.cs:181`）
- 测试不变（`StatsViewModelTests` 全部保留 + PASS）
- **W20 LESSON**：re-grep boundaries BEFORE each deletion script run；re-extract verbatim via `git show HEAD:src/...cs | sed -n '<range>p'`
- **W23 LESSON**：verify `BusStatistics` struct properties (`TotalFrames` / `ErrorFrames` / `FramesPerSecond` / `BusLoadPercent`)
- **W19 R1 LESSON**：verbatim re-extraction with boundaries; first-attempt PASS target
- **CA1861 sister pattern**：inline `new[]` literals extract to `private static readonly` if test triggers it
- **Test fixture reuse**：sister pattern at `tests/.../Replay/TraceViewerServiceTests.cs:19`
- 不引入新 public/internal API；不修改 tests 文件；不修改 DI 注册
- v3.52.0 + v3.52.1 ship files (Core/Analysis/* + AI 推理 partials) 不动
- pkm-capture 节流：仅在 ship-completion 时 dispatch

## File Structure

修改 + 新增：

| 文件 | LoC | 改动 |
|---|---|---|
| `src/PeakCan.Host.App/ViewModels/StatsViewModel.cs` | 263 → ~128 | -135 LoC (ctor body + Push + Apply 移到 partials) |
| `src/PeakCan.Host.App/ViewModels/StatsViewModel/PlotFlow.partial.cs` | ~85 | NEW (ctor body + PlotModel setup) |
| `src/PeakCan.Host.App/ViewModels/StatsViewModel/SamplingFlow.partial.cs` | ~50 | NEW (Push + Apply) |

净增 0 LoC（重新分配）。Subdirectory pattern: `StatsViewModel/` 包含 2 partials（sister of W34 DbcSendViewModel/ which has 3 partials）。

---

### Task 0: Branch + spec verify + baseline

**Files:**
- Branch from main at current HEAD `e0a8068`
- Verify spec at `docs/superpowers/specs/2026-07-17-w36-stats-view-model-god-class-refactor.md`

- [ ] **Step 1: Verify spec commit exists**

```bash
git log --oneline -3
git show --stat e0a8068
```
Expected: commit `e0a8068` is visible with spec file +135 insertions.

- [ ] **Step 2: Create feature branch**

```bash
git checkout -b feature/w36-stats-view-model-god-class main
```

- [ ] **Step 3: Verify baseline build is green**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
```
Expected: 0 errors, 0 warnings on touched code (pre-existing CS0169 in `SamplingTableFlow.cs:34` is NOT touched).

- [ ] **Step 4: Verify baseline test is green**

```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-build --nologo -c Debug --filter "FullyQualifiedName~StatsViewModelTests" --logger "console;verbosity=minimal"
```
Expected: existing `StatsViewModelTests` tests all PASS.

- [ ] **Step 5: Commit plan**

```bash
git add docs/superpowers/plans/2026-07-17-w36-stats-view-model-god-class-refactor.md
git commit -m "W36 plan: StatsViewModel god-class refactor (2 NEW partials + main -135 LoC -51%; TDD)"
```

---

### Task 1: PlotFlow.partial.cs extraction

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/StatsViewModel/PlotFlow.partial.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/StatsViewModel.cs` (remove ctor body L120-205)

**Interfaces:**
- Consumes: existing `_fpsLine` + `_loadLine` (private readonly LineSeries fields stay in main), `PlotModel` (public readonly property stays in main), `MaxPoints` (const stays in main)
- Produces: `public StatsViewModel()` ctor body in PlotFlow.partial.cs

- [ ] **Step 1: Read current ctor body verbatim**

```bash
git show HEAD:src/PeakCan.Host.App/ViewModels/StatsViewModel.cs | sed -n '120,205p'
```
Expected: ~85 lines of OxyPlot PlotModel setup + LineSeries creation + annotations.

- [ ] **Step 2: Create PlotFlow.partial.cs with verbatim ctor body**

Create `src/PeakCan.Host.App/ViewModels/StatsViewModel/PlotFlow.partial.cs`:

```csharp
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// W36 god-class refactor (21st overall): OxyPlot PlotModel + 2 LineSeries
/// setup extracted from main. Sister of W34 DbcSendViewModel/ subdirectory
/// pattern (D1: 2 partials; D4 upgrade: ctor-as-orchestrator moves to
/// PlotFlow because the ctor body IS the chart-construction logic,
/// not just DI wiring).
/// <para>
/// Per hard-boundary v1.2.7: LineSeries.ItemsSource binding to
/// ObservableCollection is broken on .NET 10 / OxyPlot 2.2.0. The
/// explicit Points rebuild happens in SamplingFlow.Apply.
/// </para>
/// </summary>
public sealed partial class StatsViewModel
{
    /// <summary>
    /// Build the VM and pre-populate the empty rolling-window series so
    /// the chart can render an empty axis range on first render.
    /// </summary>
    public StatsViewModel()
    {
        // Pre-fill the rolling windows with zeros so the chart's X axis
        // ... (verbatim from L122-205 of original file)
        // [implementer: copy full ctor body from `git show HEAD:... | sed -n '120,205p'`]
    }
}
```

The implementer MUST use the **verbatim** ctor body from Step 1. Do NOT rewrite. Do NOT change types. Do NOT change method signatures.

- [ ] **Step 3: Delete ctor body from main file (keep class declaration + field declarations)**

Modify `src/PeakCan.Host.App/ViewModels/StatsViewModel.cs`:
- Delete lines L120-205 (ctor body — both opening brace and closing brace)
- Keep L47 `public sealed partial class StatsViewModel : ObservableObject` declaration
- Keep L49-118 (fields + properties + const)
- File should now be ~178 LoC

W19 R1 LESSON: **re-grep the boundaries BEFORE running any deletion script.** Use `git show HEAD:src/...cs | wc -l` to confirm range.

W23 STRUCT-FABRICATION LESSON: After deletion, build immediately to catch any signature drift.

- [ ] **Step 4: Build verify (RED→GREEN)**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
```
Expected: 0 errors.

If errors: do NOT silently fix — BLOCKED with the specific error. Most likely cause is forgetting to keep `using OxyPlot.*` directives in main if any are now orphaned. Main still needs `using OxyPlot;` (for `PlotModel` property type) and `using OxyPlot.Series;` (for `LineSeries` field type).

- [ ] **Step 5: Test verify (no regression)**

```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-build --nologo -c Debug --filter "FullyQualifiedName~StatsViewModelTests" --logger "console;verbosity=minimal"
```
Expected: existing tests all PASS.

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/StatsViewModel.cs \
        src/PeakCan.Host.App/ViewModels/StatsViewModel/PlotFlow.partial.cs
git commit -m "W36 T1: PlotFlow.partial.cs — ctor body + PlotModel setup extracted (-85 LoC main)"
```

---

### Task 2: SamplingFlow.partial.cs extraction

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/StatsViewModel/SamplingFlow.partial.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/StatsViewModel.cs` (remove Push + Apply, L213-262)

**Interfaces:**
- Consumes: existing `_fpsLine` + `_loadLine` (private readonly LineSeries fields), `FpsSeries` + `LoadSeries` (ObservableCollection<double> properties), `TotalFrames` + `ErrorFrames` (source-gen properties), `MaxPoints` (const), `InvalidatePlotCallCount` (internal property), `PlotModel` (readonly property)
- Produces: `public void Push(BusStatistics s)` + `private void Apply(BusStatistics s)` in SamplingFlow.partial.cs

- [ ] **Step 1: Read current Push + Apply verbatim**

```bash
git show HEAD:src/PeakCan.Host.App/ViewModels/StatsViewModel.cs | sed -n '207,262p'
```
Expected: ~50 lines (Push 8 LoC + Apply 35 LoC + xmldoc comments).

- [ ] **Step 2: Create SamplingFlow.partial.cs with verbatim Push + Apply**

Create `src/PeakCan.Host.App/ViewModels/StatsViewModel/SamplingFlow.partial.cs`:

```csharp
using PeakCan.Host.Core.Dbc;  // for BusStatistics
using PeakCan.Host.App.Helpers;  // for RunOnUiPost extension

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// W36 god-class refactor (21st overall): Push dispatcher hop + Apply
/// rolling-window maintenance + LineSeries.Points rebuild extracted from
/// main. Sister of W34 DbcSendViewModel/SendFlow.partial.cs pattern.
/// <para>
/// Per hard-boundary v1.2.7: LineSeries.Points MUST be explicitly rebuilt
/// (not ItemsSource binding) due to OxyPlot 2.2.0 .NET 10 broken binding.
/// Per v1.2.9: rebuilt points use index 0..MaxPoints-1 as X (mirrors the
/// implicit ObservableCollection index-as-X semantics).
/// </para>
/// </summary>
public sealed partial class StatsViewModel
{
    /// <summary>
    /// Append a new bus-statistics sample to the rolling windows and
    /// refresh the totals. Marshals to the WPF UI thread when a
    /// dispatcher is available (production path); runs inline in test
    /// context (no <c>Application</c>).
    /// </summary>
    public void Push(BusStatistics s)
    {
        // ... verbatim from L213-220
    }

    /// <summary>
    /// Apply the snapshot inline. Always called on the UI thread (via
    /// the dispatcher hop above, or directly in test context). Maintains
    /// the rolling window at <see cref="MaxPoints"/> samples and
    /// refreshes the bound totals.
    /// </summary>
    private void Apply(BusStatistics s)
    {
        // ... verbatim from L228-262
    }
}
```

The implementer MUST use the **verbatim** Push + Apply bodies from Step 1.

- [ ] **Step 3: Delete Push + Apply from main file**

Modify `src/PeakCan.Host.App/ViewModels/StatsViewModel.cs`:
- Delete lines L207-262 (Push + Apply — both xmldoc comments + method bodies + closing braces)
- Keep L118-205 boundary intact (still has empty ctor in main? — NO, ctor is in PlotFlow now; L120 is the deleted ctor)
- File should now be ~128 LoC

W19 R1 LESSON: re-grep boundaries. W23 LESSON: build immediately after.

- [ ] **Step 4: Build verify**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
```
Expected: 0 errors.

Likely issue if error: `BusStatistics` namespace — verify `using PeakCan.Host.Core.Dbc;` is in SamplingFlow.partial.cs.

- [ ] **Step 5: Test verify**

```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-build --nologo -c Debug --filter "FullyQualifiedName~StatsViewModelTests" --logger "console;verbosity=minimal"
```
Expected: existing tests all PASS.

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/StatsViewModel.cs \
        src/PeakCan.Host.App/ViewModels/StatsViewModel/SamplingFlow.partial.cs
git commit -m "W36 T2: SamplingFlow.partial.cs — Push + Apply + LineSeries rebuild extracted (-50 LoC main)"
```

---

### Task 3: Full solution CI + coverage check

- [ ] **Step 1: Full solution build**

```bash
dotnet build PeakCan.Host.slnx -c Debug --no-restore
```
Expected: 0 errors. Warnings count may match baseline (pre-existing CS0169 etc.).

- [ ] **Step 2: Full solution test**

```bash
dotnet test PeakCan.Host.slnx --no-build --nologo -c Debug --logger "console;verbosity=minimal"
```
Expected: 1433 PASS / 0 FAIL / 5 SKIP (matches v3.52.1 ship baseline).

If failures appear that are timing-related (AscParser / SetSpeed wall-clock), retry with:
```bash
dotnet test PeakCan.Host.slnx --no-build --nologo -c Debug --logger "console;verbosity=minimal" -- xUnit.MaxParallelThreads=1
```
Expected: 1433 PASS / 0 FAIL.

- [ ] **Step 3: Verify LoC delta**

```bash
wc -l src/PeakCan.Host.App/ViewModels/StatsViewModel.cs \
      src/PeakCan.Host.App/ViewModels/StatsViewModel/PlotFlow.partial.cs \
      src/PeakCan.Host.App/ViewModels/StatsViewModel/SamplingFlow.partial.cs
```
Expected: main ≤ 130 LoC; subdirectory 2 partials totaling ~135 LoC; cumulative ≈ 263 LoC (no net change).

- [ ] **Step 4: Commit (no commit needed — verification only)**

If anything changed, commit; otherwise skip.

---

### Task 4: Release notes + tier-3 ship

**Files:**
- Create: `docs/release-notes-v3-53-0-minor.md`
- Modify: `src/Directory.Build.props` (version bump)
- Push + PR + squash + tag + GH release

- [ ] **Step 1: Write release notes**

Create `docs/release-notes-v3-53-0-minor.md` covering:
- W36 god-class refactor summary
- 2 NEW partials (PlotFlow + SamplingFlow)
- Main file 263 → ~128 LoC (-135 LoC, -51%)
- Public API + tests + DI unchanged
- 6 NEW 1/3 lesson candidates
- Sister of W3-W34 series (21st god-class SHIP)

- [ ] **Step 2: Bump version**

In `src/Directory.Build.props`:
```xml
<Version>3.53.0</Version>
<AssemblyVersion>3.53.0.0</AssemblyVersion>
<FileVersion>3.53.0.0</FileVersion>
<InformationalVersion>3.53.0</InformationalVersion>
```

- [ ] **Step 3: Tier-3 ship**

```bash
git add docs/release-notes-v3-53-0-minor.md src/Directory.Build.props
git commit -m "v3.53.0: version bump + release notes (StatsViewModel god-class refactor; 21st)"
git push -u origin feature/w36-stats-view-model-god-class
gh pr create --base main --title "v3.53.0 MINOR: StatsViewModel god-class refactor (21st overall; 2 NEW partials)" --body-file docs/release-notes-v3-53-0-minor.md
gh pr merge --squash --delete-branch
git reset --hard origin/main  # ensure main = squash (sister of v3.52.0/v3.52.1 force-correct pattern)
git tag -a v3.53.0 -m "v3.53.0 MINOR — StatsViewModel god-class refactor (21st overall)"
git push origin v3.53.0
gh release create v3.53.0 --notes-file docs/release-notes-v3-53-0-minor.md
```

---

## Sister-lesson candidates to monitor

| Lesson | Status | What T1-T3 might observe |
|---|---|---|
| `largest-method-can-move-when-flow-is-discrete-constructor-not-orchestrator` | NEW 1/3 (W36) | T1: ctor 整段搬到 PlotFlow — D4 upgrade验证 |
| `stats-vm-chart-rebuild-must-stay-coupled-with-rolling-window-maintenance` | NEW 1/3 (W36) | T2: LineSeries.Points rebuild 与 FpsSeries.Add 不能分 |
| `oxyplot-itemssource-path-on-net10-still-broken-requires-explicit-points-rebuild` | NEW 1/3 (W36) | T2: 验证仍 relevant (sister of v1.2.7 LESSON) |
| `2-partial-subdirectory-pattern-empirical-w11-w36` | NEW 1/3 (W36) | T1+T2: 2 partials deployment; DbcParser 4, DbcSendViewModel 3, StatsViewModel 2 |
| `internal-test-counter-pattern-empirical-w23-w36` | NEW 1/3 (W36) | T1: InvalidatePlotCallCount sister |
| `dispatcher-hop-fire-and-forget-can-stay-isolated-from-apply-body` | NEW 1/3 (W36) | T2: Push (hop) + Apply (body) 同 partial |

## Verification

- `dotnet build PeakCan.Host.slnx`: 0 errors
- `dotnet test PeakCan.Host.slnx`: 1433 PASS / 0 FAIL / 5 SKIP
- `wc -l src/.../StatsViewModel.cs`: ≤ 130 LoC
- 2 NEW partials exist in `StatsViewModel/` subdirectory
- Public API surface unchanged (FpsSeries + LoadSeries + PlotModel + TotalFrames + ErrorFrames + InvalidatePlotCallCount + Push + StatsViewModel())
- DI registration unchanged (still `services.AddSingleton<StatsViewModel>()` at `ViewModelsBatch2Flow.cs:181`)
- All `StatsViewModelTests` tests pass without modification
- Tag v3.53.0 + GH release published

## Out of scope (YAGNI)

- W37+ god-class refactor (RecordService / AscLocator / etc.) — separate work
- P1 LLM Provider PATCH — separate work
- v3.53.1 cleanup PATCH — separate work
- Refactor of ctor's internal helpers (`InitializeOxyPlotModel` etc.) — not needed at this size
- Tests file changes — existing tests pass without modification