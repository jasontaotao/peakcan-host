# Release Notes v3.9.2 — multi-agent review cleanups (PATCH)

**Released:** 2026-07-07
**Parent:** v3.9.1 PATCH (`63f5c207`)
**Tag:** v3.9.2
**Source:** 38-finding multi-agent review at `.review/00-integrated-findings.md`
**Files:** 8 production modified + 4 test modified + 1 ship script (also: 1 file renamed out of `src/`)
**Tests:** 1239 → 1242 + 5 SKIP / 0 fail (+4 active: 2 H1 + 1 H10 + 0 L-series; -1 L11 test deletion; net +3)
**Code stats:** 8 files / +182 / -523 = **-341 LoC net** (mostly dead-code removal)

## Origin: multi-agent review

v3.9.2 PATCH is the ship slice of a 6-lens code review of the entire peakcan-host codebase (~352 files / ~57k LOC). Six parallel sub-agents produced **69 raw findings**; integration deduped to **38 unique actionable items** (by file:line + root cause). Severity escalation when ≥2 lenses flagged the same root cause.

Lens distribution:

| Lens | CRITICAL | HIGH | MEDIUM | LOW |
|------|----------|------|--------|-----|
| factual | 0 | 1 | 1 | 2 |
| senior-engineer | 3 | 6 | 7 | 5 |
| security | 0 | 4 | 7 | 5 |
| consistency | 0 | 1 | 5 | 6 |
| redundancy | 0 | 4 | 2 | 2 |
| bug-verify (v3.9.1) | 0 | 1 gap | 0 | 0 |
| **Raw total** | **3** | **17** | **22** | **20** |
| **Dedup unique** | **3** | **10** | **13** | **12** |

The v3.9.2 PATCH ships the **7 highest-bang-for-buck fixes**. The 3 CRITICAL findings (god class, MessageBox VM leak, autosaver dedup) and the remaining 28 MEDIUM/LOW items are **deferred to v3.10.0 MINOR** as design work, not PATCH work.

## Bug verification (v3.9.1 PATCH — already shipped)

Adversarial verification confirms both user-reported bugs are FIXED:

| Bug | Verdict | Evidence |
|-----|---------|----------|
| #1 Trace Viewer survives main window close | **FIXED** | `App.xaml.cs:135` `Application.Current.MainWindow = shell` + `AppShellViewModel.cs:644` `_traceViewerView.Owner = owner`. Pattern mirrors `OpenMultiFrame`/`OpenMultiFrameSend`. |
| #2 .asc import hangs | **FIXED** | 5 fix sites + 6 new tests cover all reported scenarios. |

One **defensive** gap (Bug #2 corner case 5) is closed by **H10** in this PATCH.

## Fixes shipped in v3.9.2 PATCH

### H1 — `IReplayService.LoopRewound` event contract fulfilled
**File:** `src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs`

The v3.9.0 MINOR P1 added `IReplayService.LoopRewound` with documented contract "UI subscribers (typically `ReplayViewModel`) use this to surface a status message ('Rewind: loop region X')". The v3.9.0 PATCH (commit `09794f0`) shipped the event raise + the IReplayService interface declaration but **never wired `ReplayViewModel`** to subscribe. Users saw the rewind happen but no status feedback.

**Fix:**
- Added `[ObservableProperty] private string _statusMessage = "Ready";`
- Subscribed `_service.LoopRewound += OnLoopRewound` in ctor (sibling to `FrameEmitted` + `PlaybackEnded`)
- New `OnLoopRewound` handler marshals to captured `SynchronizationContext` and sets `StatusMessage = $"Rewind: loop region ({Start:F2}s → {End:F2}s)"`

**Tests:** +2 (`LoopRewound_RaisesServiceEvent_SetsStatusMessage` + `StatusMessage_DefaultsTo_Ready`).

### H2 — Delete 3 dead RelayCommands + 1 dead event handler
**Files:**
- `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs`
- `src/PeakCan.Host.App/Views/TraceViewerView.xaml.cs`

Dead code left over from v3.9.x refactors:

| What | Why dead | Action |
|------|----------|--------|
| `OpenFileCommand` / `OpenFileAsync` | Legacy v3.0 alias, 0 callers (XAML uses `AddTraceCommand` since v3.2.0) | Method deleted |
| `[RelayCommand]` on `LoadDbcAsync` | XAML uses `Click="OnLoadDbcClick"` calling the method directly; source-gen `LoadDbcCommand` had 0 bindings | Attribute dropped; method kept as public API (11 tests + 1 code-behind caller) |
| `OnAddTraceClick` handler | XAML moved to `Command="{Binding AddTraceCommand}"` in v3.9.1 PATCH but handler body remained | Handler deleted |

**Tests:** +0 net (3 `TraceViewerViewModelTests` updated `OpenFileAsync` → `AddTraceAsync`, API-equivalent).

### H10 — `AddTraceAsync` defensive fallback catch (defensive gap from Bug #2 verification)
**File:** `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs`

The v3.9.1 PATCH bug-verify found that `AddTraceAsync` only catches `OperationCanceledException` and `ReplayException`. Any other exception (e.g. `ObjectDisposedException` from a disposed registry, registry hook throwing on `SourcesChanged`, or `InvalidOperationException` from a disposed VM) escapes the try/catch. Because the call site is `async void` (the source-gen `AddTraceCommand` is invoked via WPF `ICommand.Execute`), the exception propagates to WPF `DispatcherUnhandledException`, where `App.xaml.cs:332` deliberately does NOT mark Handled (per project policy: "Let WPF terminate the process — the log gives us the diagnostic info the user previously had no way to obtain."). So a non-Replay, non-OCE failure path **terminates the process**.

The practical likelihood is low (`TraceViewerService.LoadAsync` already wraps nearly every IO failure in `ReplayLoadException`), but the defensive gap was real.

**Fix:** Add a fallback `catch (Exception ex)` arm with the same shape as the `ReplayException` arm:
```csharp
catch (Exception ex)
{
    LogLoadFailed(_logger, ex, path);
    ErrorMessage = $"Unexpected error: {ex.Message}";
    StatusMessage = "Load failed";
}
```

**Tests:** +1 (`AddTraceAsync_RegistryThrowsUnexpectedException_SetsErrorMessageAndClearsIsLoading`).

### L1 — `TraceViewerViewModel.ApplySnapshotAsync` bundle DBC catch tightening
**File:** `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs`

Was a bare `catch { /* swallow — bundle stores path-reference only */ }`. Now typed:
```csharp
try { await _dbcService.LoadAsync(dto.DbcPath).ConfigureAwait(true); }
catch (FileNotFoundException) { /* bundle references a deleted DBC — acceptable */ }
catch (Exception ex)
{
    LogBundleDbcLoadFailed(_logger, dto.DbcPath, ex);
    StatusMessage = $"DBC load failed: {ex.Message}";
}
```

New `[LoggerMessage]` source-gen helper `LogBundleDbcLoadFailed`. Status feedback surfaces the failure on the toolbar so the operator can diagnose a malformed-vendor-DBC without losing visibility.

### L3 — `SendViewModel.OpenMultiFrameSend` Closed reset (mirror v3.9.1 PATCH B1)
**File:** `src/PeakCan.Host.App/ViewModels/SendViewModel.cs`

After `_openMultiFrameWindow.Show()`:
```csharp
_openMultiFrameWindow.Closed += (_, _) => _openMultiFrameWindow = null;
```

Mirrors `AppShellViewModel._traceViewerView.Closed` reset from v3.9.1 PATCH Bug #1. Without this, a closed-then-reopened Multi-frame window would stomp the closed instance reference and leak it from WPF's window list.

### L11 — `RecentSessionsService` unused API surface deleted
**File:** `src/PeakCan.Host.App/Services/Trace/RecentSessionsService.cs`

| Symbol | Why deleted |
|--------|-------------|
| `RecentSessionsPath` property | 0 callers — only the declaration site; the ctor's `_path` parameter is what tests inject |
| `Remove(path)` method | 0 prod callers; `Clear(string)` covers the UX need |

**Tests:** -1 (`Remove_DropsPath_AndPreservesOrderOfRest` deleted with the method).

### L12 — Stale review artifact moved out of `src/`
**File:** `src/_review_verification.json` → `.review/_review_verification-archive.json`

Was a 25 KB JSON review artifact not consumed by any `.csproj`. Moved to `.review/` for traceability without polluting `src/`.

## Files modified

**Production (5):**
- `src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs` — H1
- `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` — H2 + H10 + L1
- `src/PeakCan.Host.App/Views/TraceViewerView.xaml.cs` — H2
- `src/PeakCan.Host.App/ViewModels/SendViewModel.cs` — L3
- `src/PeakCan.Host.App/Services/Trace/RecentSessionsService.cs` — L11

**File moved:**
- `src/_review_verification.json` → `.review/_review_verification-archive.json` — L12

**Tests (4):**
- `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs` — H2 + H10 (+1 new test)
- `tests/PeakCan.Host.App.Tests/ViewModels/ReplayViewModelTests.cs` — H1 (+2 new tests)
- `tests/PeakCan.Host.App.Tests/ViewModels/AppShellViewModelTests.cs` — H2 reflection-only assertion tweak
- `tests/PeakCan.Host.App.Tests/Services/Trace/RecentSessionsServiceTests.cs` — L11 (-1 test)

**New ship script:**
- `scripts/tier3_v392.py` — Tier 3 ship for v3.9.2 (parent = v3.9.1 ship commit `63f5c207`)

## Verification

```bash
# Targeted
dotnet test --filter "FullyQualifiedName~ReplayViewModel|FullyQualifiedName~TraceViewerViewModel|FullyQualifiedName~RecentSessionsService" --nologo
# 138 passed, 0 failed

# Full suite
dotnet test PeakCan.Host.slnx --nologo
# 1242 + 5 SKIP / 0 fail (App 733 + Core 425 + Infra 84 / 2 SKIP)
```

Note: `AscParserTests.Parse_MalformedLines_LogsEachWithLineNumberAndReason` is the pre-existing parallel-runner transient flaky test first observed in v3.9.0; passes single-runner.

## Out of scope (deferred to v3.10.0 MINOR)

**CRITICAL (3):**
- **C1** `AppShellViewModel` calls `MessageBox.Show` directly (lines 388, 435) — bypasses existing `IMessageBoxPrompt` abstraction. Fix: inject `IMessageBoxPrompt`. -30 LoC mechanical.
- **C2** `ReplayViewModel` 1153-LoC god class mixing 6 responsibilities (transport / filters / bundle / MRU / bookmarks / loop-regions / frame-stepping). Fix: split into 3 VMs.
- **C3** `TraceSessionAutoSaver` + `ReplaySessionAutoSaver` are 95% duplicate (~250 LoC). Fix: extract generic `SessionAutoSaver<TVm>` base class.

**HIGH (remaining 7):**
- H3 ODX/PDX import path normalization (CWE-22)
- H4 `TraceSessionLibrary.Load` unbounded `.tmtrace` read (CWE-400 + CWE-22)
- H5 `AscParser` reads unlimited `.asc` into `List<string>` (CWE-400)
- H6 `asc-search-dirs.json` no allowlist (CWE-22 + CWE-400)
- H7 `BuildSnapshot()` duplication + sync-over-async on `_hasher.ComputeAsync`
- H8 `TraceViewerViewModel.RebuildSignalsCore` 145 LoC + double-loop
- H9 `ReplayService.EmitFrame` sync-over-async on `EmitFrameToSinkAsync` + `#pragma warning disable CA2012`

**MEDIUM (13) + LOW (12):** mechanical cleanups — naming, XAML keys, exception type hierarchy, [LoggerMessage] migration, dual-purpose `Status`, etc.

## NEW 1-of-1 lessons

1. `event-contract-without-subscriber-is-loud-contract-drift` — when interface XML doc says "UI subscribers (typically X) use this to surface Y" but X never subscribes, the contract silently rots. Add a smoke test or static analysis that greps the event name in the documented consumer class.

2. `relaycommand-attribute-without-binding-is-dead-code` — `[RelayCommand]` source-gen emits a public `XxxCommand` property. If no XAML `Command={Binding XxxCommand}` and no `.XxxCommand.Execute()` caller exists, the attribute is dead weight; the method should be plain public (or removed). Grep the attribute usage before deciding.

3. `async-void-vm-command-without-fallback-catch-risks-process-termination` — VM RelayCommand methods are async-void at the source-gen level. WPF `DispatcherUnhandledException` policy is "do not mark Handled". Without a fallback `catch (Exception)`, any exception outside the typed arms (ReplayException / OCE) terminates the app. The fix is symmetric with the typed arms: log + ErrorMessage + StatusMessage, never MessageBox.

## Next

- **v3.10.0 MINOR** — the 3 CRITICAL findings + remaining HIGH findings (C1-C3 + H3-H9). The god-class split will be the largest design work; estimate 1-2 days.
- **v3.9.x PATCH chain** — visual UI smoke testing of the v3.9.0 P1-P6 features + v3.9.1 bug fixes + v3.9.2 cleanup.