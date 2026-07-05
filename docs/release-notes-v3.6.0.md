# peakcan-host v3.6.0 MINOR — `.tmtrace` UX completeness

## Summary

v3.6.0 MINOR closes three v3.5.0 YAGNI-deferred items on the `.tmtrace` Trace Viewer session bundle format and one v3.5.0 loose end discovered during exploration:

1. **T1 (loose end)** — `ApplySnapshotAsync` now restores `Color` (ARGB) and `DisplayName` from the bundle, replacing the v3.5.0 "v3.5.x follow-up" comment. `BuildSnapshot` reads the assembly's `InformationalVersion` instead of a hardcoded `"3.5.0"`. Bundle schema remains `tmtrace/v1` (no breaking change, no migration).
2. **T2 (auto-save)** — new `TraceSessionAutoSaver` flushes the current session to `%APPDATA%/PeakCan.Host/trace-session.tmtrace` on `App.OnExit` (best-effort, 5s cap). On `App.OnStartup`, the app offers to restore the auto-saved session via an owner-bound MessageBox. A "don't ask again" preference persists in `%APPDATA%/PeakCan.Host/auto-save-prefs.json`.
3. **T3 (AppShell menu + Recent[5])** — new `RecentSessionsService` MRU list (max 5) persists to `%APPDATA%/PeakCan.Host/recent-sessions.json`. AppShell File menu gains **Open Session…** (Ctrl+O), **Save Session…** (Ctrl+S), **Open Recent** (templated submenu), and **Clear Recent**. The Trace Viewer toolbar's Save/Open buttons are removed (single source of action).

The three sub-features together complete the multi-trace session UX envisioned in v3.5.0: the user can save/load bundle files from anywhere in the app, recover from a crash via auto-save, and quickly reopen recently-used sessions.

## What changed

**7 commits** (5 feat/refactor + 2 review-fixup). 3 new files (services) + 1 new test file + 5 modified production files + 1 modified test file + 1 modified README + 1 new release-notes file. Net ~+850 LOC production + ~+450 LOC test.

| Path | Δ | Fix |
|------|---|-----|
| `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` | +6 / −2 | T1.A: `AppVersion = GetAppVersion()`. T1.B: restore `Color` + `DisplayName` in `ApplySnapshotAsync` with v1 fallback (skip if ARGB all-zero / DisplayName matches filename). |
| `src/PeakCan.Host.App/Services/Trace/TraceSource.cs` | +14 / −6 | T1.B: `DisplayName` + `Color` setters `internal` (was `{ get; }`) so the VM can restore them; public read surface unchanged. |
| `src/PeakCan.Host.App/Services/Trace/TraceSessionAutoSaver.cs` | NEW (~290 LOC) | T2: `TrySaveAutoSnapshotAsync` + `TryLoadAutoSnapshotAsync` + `ApplyAutoSnapshotAsync` (loads prefs → load DTO → MessageBox Yes/No/Cancel → `OpenSessionAsync` → persist NeverRestore flag). |
| `src/PeakCan.Host.App/Services/Trace/AutoSavePrefsStore.cs` | NEW (~150 LOC) | T2: `AutoSavePrefs` record + `IAutoSavePrefsStore` interface + `FileAutoSavePrefsStore` impl (atomic write + corrupt-recovery-to-default). |
| `src/PeakCan.Host.App/Services/Trace/WpfMessageBoxPrompt.cs` | NEW (~60 LOC) | T2: WPF `IMessageBoxPrompt` impl — STA-aware via `Application.Current.Dispatcher.InvokeAsync`. |
| `src/PeakCan.Host.App/Services/Trace/RecentSessionsService.cs` | NEW (~240 LOC) | T3: `Add` (case-insensitive dedup, top insertion, cap at 5) + `Remove` + `Clear` + `LoadAsync` + `PropertyChanged`; atomic write + corrupt-recovery-to-empty; DTO co-located. |
| `src/PeakCan.Host.App/App.xaml.cs` | +30 / −3 | T2: `OnExit` pre-flush (5s cap, before `_host.StopAsync`). `OnStartup` dispatcher-chain restore-prompt AFTER `shell.Show()` (owner-bound modal). |
| `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` | +10 / 0 | T2 + T3: 5 new singleton registrations (`TraceSessionAutoSaver`, `ITraceViewerViewModelProvider`, `IAutoSavePrefsStore`, `IMessageBoxPrompt`, `RecentSessionsService`). |
| `src/PeakCan.Host.App/AppShell.xaml` | +14 / 0 | T3: File menu now has `Open Session…` (Ctrl+O) / `Save Session…` (Ctrl+S) / `Open Recent` (templated submenu) / `Clear Recent`. |
| `src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs` | +140 / −1 | T3: 4 new `[RelayCommand]`s + `ObservableCollection<RecentSessionVm>` + `RefreshRecentEntries` helper. |
| `src/PeakCan.Host.App/Views/TraceViewerView.xaml` | 0 / −13 | T3: remove Save/Open toolbar buttons. |
| `src/PeakCan.Host.App/Views/TraceViewerView.xaml.cs` | 0 / −58 | T3: remove `OnSaveSessionClick` + `OnOpenSessionClick` handlers (now wired through AppShell commands). |
| `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs` | +200 / 0 | T1: 4 new test helpers + 3 new tests + `using System.Reflection`. |
| `tests/PeakCan.Host.App.Tests/ViewModels/AppShellViewModelTests.cs` | +30 / −9 | T3: ctor signature adjustments (new `RecentSessionsService` / `TraceViewerViewModel` / `IFileDialogService` params). |
| `tests/PeakCan.Host.App.Tests/Services/Trace/TraceSessionAutoSaverTests.cs` | NEW (~250 LOC) | T2: 6 tests (write/lock/missing/round-trip/neverRestore flag/prompt-suppressed). |
| `tests/PeakCan.Host.App.Tests/Services/Trace/RecentSessionsServiceTests.cs` | NEW (~170 LOC) | T3: 6 tests (append/dedup/cap/remove/clear/persist). |
| `README.md` | +12 / −6 | Status line → v3.6.0; test counts → 1117 + 5 SKIP; new "Trace Viewer + session persistence" bullet. |

## Fix-by-fix detail

### Fix 1 (T1.A) — `AppVersion` from assembly metadata

**Root cause**: `TraceViewerViewModel.BuildSnapshot` line 343 hardcoded `AppVersion = "3.5.0"`. Bundles saved by v3.5.x (and the upcoming v3.6.0) were all stamped `"3.5.0"`, hiding the version that actually wrote them.

**Fix**: replace with `GetAppVersion()` — reads `AssemblyInformationalVersionAttribute` and strips a `+git<sha>` suffix that LocalBuilder appends. Pattern mirrors `AppShellViewModel.cs:60-62` (the existing `WindowTitle` source).

### Fix 2 (T1.B) — restore Color + DisplayName in `ApplySnapshotAsync`

**Root cause**: v3.5.0's `ApplySnapshotAsync` block (lines 405-414) explicitly skipped Color and DisplayName restoration with the comment "v3.5.0 ships path-reference only; color restoration is a v3.5.x follow-up." The bundle already stored the ARGB bytes + display name — the restore logic was just never wired.

**Fix**: extend the post-`_registry.LoadAsync(...)` block so that:
- If the bundle's `DisplayName` is non-empty AND differs from the filename-only default, set `loaded.DisplayName = bs.DisplayName`.
- If any of the bundle's `ColorA/R/G/B` bytes are non-zero (forward-compat with hand-edited v1 bundles that pre-date color capture), set `loaded.Color = OxyColor.FromArgb(...)`.

Required adding `internal set` to `TraceSource.DisplayName` + `Color` properties (the public read surface is unchanged; only same-assembly callers + InternalsVisibleTo'd tests can write).

### Fix 3 (T2) — `TraceSessionAutoSaver` + App lifecycle

**Files**: new `TraceSessionAutoSaver.cs` (~290 LOC) + `AutoSavePrefsStore.cs` (~150 LOC) + `WpfMessageBoxPrompt.cs` (~60 LOC) + App.xaml.cs hooks + 4 new DI registrations.

**Behavior**:
- `App.OnExit` invokes `TrySaveAutoSnapshotAsync` with a 5s cap BEFORE `_host.StopAsync` so the live VM is still resolvable from the service provider. Best-effort — exceptions logged at Warning, never propagated (the process is exiting anyway).
- `App.OnStartup` resolves `TraceSessionAutoSaver` + `TraceViewerViewModel` and chains the restore-prompt to fire AFTER `shell.Show()` so the MessageBox has an owner window.
- `ApplyAutoSnapshotAsync` flow: load prefs (if `NeverRestore` → no-op) → load DTO (if missing/corrupt → no-op) → prompt via `WpfMessageBoxPrompt` ("Restore previous trace session?" Yes/No, with the auto-saved timestamp in the body) → on No, persist `NeverRestore=true` → on Yes, call `vm.OpenSessionAsync(path)` + show a separate missing-`.asc` warning if any.
- The prompt is a real WPF MessageBox (YesNo button set) dispatched to the WPF STA thread via `Application.Current.Dispatcher.InvokeAsync(...).Task.Unwrap`. The seam (`IMessageBoxPrompt`) makes it testable in xunit.

### Fix 4 (T3) — AppShell File menu + Recent[5] + remove toolbar

**Files**: new `RecentSessionsService.cs` (~240 LOC) + 5 modified files (AppShell.xaml, AppShellViewModel.cs, TraceViewerView.xaml + .cs, AppHostBuilder.cs) + 1 new test file.

**Behavior**:
- **File menu** (AppShell): `Open Session…` (Ctrl+O) / `Save Session…` (Ctrl+S) / `Open Recent` (templated submenu, max 5 entries with MRU ordering) / `Clear Recent` (empties the list + deletes the file).
- **`RecentSessionsService`** maintains a case-insensitive exact-path MRU list; `Add(path)` removes any existing entry with the same path (case-insensitive), inserts at top, caps at 5, persists, raises `PropertyChanged`. `Remove(path)` / `Clear` are symmetric. The list is bound to the menu via `ObservableCollection<RecentSessionVm>`; `RefreshRecentEntries` rebuilds on every PropertyChanged (5 Clear+Add ops per rebuild; bounded by `MaxEntries=5` so no perf concern).
- **Trace Viewer toolbar** Save/Open buttons removed. The handlers in `TraceViewerView.xaml.cs` are deleted. `TraceViewerViewModel.SaveSessionAsync` / `OpenSessionAsync` remain public — they're now invoked from `AppShellViewModel` commands via DI.

The persistence JSON shape (mirror `TraceSessionBundle` envelope):

```json
{
  "version": 1,
  "recent": [
    {"path": "...", "label": "highway.tmtrace", "savedAt": "2026-07-05T10:00:00Z"}
  ]
}
```

Atomic write (`tmp` + `File.Move(..., overwrite: true)` with UTF-8 BOM) + corrupt-recovery-to-empty, same pattern as `TraceSessionLibrary.cs:96-116`.

## Test delta

| Suite | v3.5.8 | v3.6.0 | Δ |
|-------|--------|--------|---|
| App | 614 + 3 SKIP | **629 + 3 SKIP** | +15 (3 T1 + 6 T2 + 6 T3) |
| Core | 404 | 404 | 0 |
| Infrastructure | 84 + 2 SKIP | 84 + 2 SKIP | 0 |
| **Total** | **1102 + 5 SKIP** | **1117 + 5 SKIP** | **+15** |

All new tests are deterministic (no `Task.Delay` / wall-clock waits; temp-file fixtures via `Path.GetTempPath() + Guid.NewGuid()`; NSubstitute for `ILogger<T>` and the new `IMessageBoxPrompt` / `ITraceViewerViewModelProvider` seams).

## Process notes (3 rounds of review-fixup, all closed)

- **T1 review** found 1 actionable LOW (`AddFakeTraceSource` default DisplayName accidentally matched its own path basename → would silently skip the restore branch on future test reuse). Fix: change default to `"non_default_fake"` against a fixed `C:/fake.asc` path. Commit `86a5220`.
- **T2 review** found 1 HIGH (YAGNI: `BuildSnapshot` was split into `BuildSnapshot()` + `BuildSnapshotForAutoSave()` with a speculative "will diverge later" docstring). Fix: roll back to single `public BuildSnapshot()` — `SaveSessionAsync` and the auto-saver both call it. Commit `2fc1a0b`.
- **T3 review** found 2 MEDIUMs (`ContinueWith` after `LoadAsync` was a double-fire of `RefreshRecentEntries` AND a latent `TaskScheduler.Default` race if `LoadAsync` ever became truly async). Fix: drop the `ContinueWith`, rely on the `PropertyChanged` subscription alone. Commit `5c7a775`.

All three review-fixup commits are appended to the same `feature/v3-6-0-minor` branch.

## Pre-ship review

| Severity | Count | Notes |
|----------|-------|-------|
| CRITICAL | 0 | — |
| HIGH     | 0 | — |
| MEDIUM   | 0 | — |
| LOW      | 0 | (T1/T2/T3 review HIGHs + MEDIUMs are all closed; remaining LOWs accepted as deferred) |
| **Verdict** | — | **APPROVE** (3 task reviews + 1 final whole-branch review, all clean) |

## Tier 3 ship

- **Branch**: `feature/v3-6-0-minor` (local; never pushed — Tier 3 handles ship)
- **Parent**: v3.5.8 PATCH on origin/main (`bade928def70bc5357dc140eb6f1069c0bb31e76`)
- **Tier 3 force-update** via `gh api` JSON payloads — `scripts/tier3_v360.py`
- **Tag**: `v3.6.0` (MINOR, non-breaking)

## Closest cousins / related

- [[peakcan-host-v3-5-8-patch-shipped]] — last PATCH (v3.5.8 review-residual stale-task race). Sets the test-counters baseline.
- [[peakcan-host-v3-5-0-minor-shipped]] — parent MINOR (`.tmtrace` bundle format first shipped here, v3.5.0 introduced `BuildSnapshot` / `ApplySnapshotAsync`).
- [[peakcan-host-v3-5-7-patch-shipped]] — ChannelRouter fence + ScriptEngine `_engine` race; same `Volatile.Read` / `Interlocked.Exchange` patterns used by `RecentSessionsService`'s thread-affinity awareness.

## Non-scope (still deferred)

- `ITimerFactory` refactor for RecordService + StatisticsService — v3.5.x PATCH on observed-failure basis (none observed).
- `ReplayTimeline` cursor-walking tests (lower priority; longer `Task.Delay` budgets).
- Hash-based `.asc` relocation; Replay tab session save; `.tmtrace` JSON Schema doc.
- v1.6.0 MINOR OEM `IKeyDerivationAlgorithm` concrete (61st consecutive deferred list, crypto review needed).

## Permanently retired (no longer in deferral list)

- ~~**Bundle v1→v2 migration**~~ — no breaking change needed; the bundle schema is stable. The "v3.5.x follow-up" color/DisplayName restore was a restore-only fix, not a schema change.
- ~~**Auto-save on app close**~~ — shipping in v3.6.0 (T2).
- ~~**`.tmtrace` AppShell File menu**~~ — shipping in v3.6.0 (T3).
