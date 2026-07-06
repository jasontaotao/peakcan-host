# peakcan-host v3.7.1 PATCH — fix 2 [Skip]'d ReplaySessionAutoSaver prompt tests

## Summary

v3.7.1 closes the v3.7.0 release-notes §"Non-scope" follow-up: the two `[Fact(Skip = ...)]` prompt tests in `ReplaySessionAutoSaverTests` are now active. Root cause was NSubstitute's `Substitute.For<IAutoSavePrefsStore>()` auto-stub returning `default(AutoSavePrefs)` (a `null` record) for `LoadAsync`, causing the prompt path to NRE → catch → report `RestoreAnswer.ApplyFailed` — masking test intent. Fix: extract `InMemoryPrefsStore` (already used by `TraceSessionAutoSaverTests`) to a shared `internal sealed` helper, switch Replay tests to it.

## Why this PATCH

- v3.7.0 release-notes §"Non-scope" promised this follow-up.
- The [Skip]'d tests are the only prompt-path coverage for `ReplaySessionAutoSaver.ApplyAutoSnapshotAsync` — without them, the "user said No" + "already NeverRestore" branches were only transitively covered by `TraceSessionAutoSaverTests` (mirror).
- Pure test-only change — 0 production behavior change, 0 IPC surface change, 0 schema change.

## What changed

1 commit, 3 file overlay (1 new + 2 modified):

| Path | Δ | Fix |
|------|---|-----|
| `tests/PeakCan.Host.App.Tests/Services/Trace/InMemoryPrefsStore.cs` | NEW | Extracted from `TraceSessionAutoSaverTests.cs:100-110`; `internal sealed class` for assembly visibility. |
| `tests/PeakCan.Host.App.Tests/Services/Trace/TraceSessionAutoSaverTests.cs` | -11 / +4 | Deleted private nested `InMemoryPrefsStore`; existing tests resolve to the new shared type via same-namespace lookup. |
| `tests/PeakCan.Host.App.Tests/Services/Trace/ReplaySessionAutoSaverTests.cs` | +60 / -10 | Added `MakeSaver(path, prefs, prompt)` 3-arg overload + `MakeSaver(path)` 1-arg delegating; replaced 2 `[Fact(Skip=...)]` tests with real bodies; added `using System.Windows;` for `MessageBoxResult` + `Window?`. |

## Fix-by-fix detail

### Fix 1 — extract `InMemoryPrefsStore` helper

`TraceSessionAutoSaverTests.cs:100-110` had a `private sealed class InMemoryPrefsStore` (in-memory fake of `IAutoSavePrefsStore`). Extracted verbatim to `tests/.../Services/Trace/InMemoryPrefsStore.cs` with accessibility changed from `private` (nested) → `internal sealed` (top-level). The Trace tests now reference the shared type via same-namespace resolution; no behavior change.

### Fix 2 — switch Replay tests to use `InMemoryPrefsStore`

`ReplaySessionAutoSaverTests.MakeSaver` previously used `Substitute.For<IAutoSavePrefsStore>()` — NSubstitute auto-stubs `Task<AutoSavePrefs> LoadAsync(ct)` to a completed Task whose `.Result` is `default(AutoSavePrefs)` = `null` (record is reference type). The 2 prompt tests would have hit `_prefs.LoadAsync(ct) → prefs.NeverRestore → NRE → catch → RestoreAnswer.ApplyFailed`. Replaced with `new InMemoryPrefsStore()` (returns `Task.FromResult(Current)` deterministically). Added a 3-arg `MakeSaver(path, prefs, prompt)` overload so prompt tests can inject explicit `prompt.ShowAsync(...).Returns(MessageBoxResult.No)` stubs (mirroring `TraceSessionAutoSaverTests.cs:212-213, 242-243`).

### Fix 3 — replace `[Fact(Skip = ...)]` with real bodies

The 2 prompt tests now run:

- `ApplyAutoSnapshotAsync_UserSaysNo_PersistsNeverRestoreFlag` — asserts `outcome.Answer == RestoreAnswer.No` and `prefs.Current.NeverRestore == true` after the prompt returns `No`.
- `ApplyAutoSnapshotAsync_AfterNeverRestore_NoPrompt` — asserts `outcome.Answer == RestoreAnswer.NeverRestore`, `PromptShown == false`, and `prompt.DidNotReceiveWithAnyArgs().ShowAsync(...)` confirms the prompt was suppressed.

## Test delta

| Suite | v3.7.0 | v3.7.1 | Δ |
|-------|--------|--------|---|
| App | 664 + 5 SKIP | **666 + 3 SKIP** | +2 active / -2 SKIP |
| Core | 416 | 416 | 0 |
| Infrastructure | 84 + 2 SKIP | 84 + 2 SKIP | 0 |
| **Total** | **1164 + 7 SKIP** | **1166 + 5 SKIP** | **+2 / -2 SKIP** |

All 12 AutoSaver tests pass (`dotnet test --filter "FullyQualifiedName~ReplaySessionAutoSaverTests|FullyQualifiedName~TraceSessionAutoSaverTests"` → 12/12 pass).

## Process notes

- Single commit, no per-chunk review (PATCH scope is too small to warrant 2-stage review).
- Plan mode used (not a chunked MINOR); plan file at `C:\Users\13777\.claude\plans\serialized-popping-jellyfish.md`.
- Out-of-scope reminder: `AppLifecycleShutdownTests.cs:162, 179, 391` still use bare `Substitute.For<IAutoSavePrefsStore>()` — they currently pass because they don't call `LoadAsync`. Leave for a future PATCH (YAGNI — not on the critical path).

## Pre-ship review

| Severity | Count | Notes |
|----------|-------|-------|
| CRITICAL | 0 | — |
| HIGH     | 0 | — |
| MEDIUM   | 0 | — |
| LOW      | 0 | Single-commit PATCH; mirrors the existing Trace pattern verbatim |
| **Verdict** | — | **APPROVE** |

## Tier 3 ship

- **Parent**: v3.7.0 MINOR on origin/main (`fb2f6c63c81ddc3380e24d7700d9d38f7349eeea`)
- **Overlay**: 3 files (1 new + 2 modified)
- **Tag**: `v3.7.1` (PATCH, non-breaking)

## Non-scope (still deferred)

- v1.6.0 MINOR OEM `IKeyDerivationAlgorithm` concrete — 64th consecutive deferred list, crypto review needed
- `AppLifecycleShutdownTests` bare `Substitute.For<IAutoSavePrefsStore>()` callers — defensive-only future hardening (currently pass)

## Closest cousins / related

- [[peakcan-host-v3-7-0-minor-shipped]] — parent MINOR (this PATCH closes its §"Non-scope" promise)
- [[peakcan-host-v3-6-0-minor-shipped]] — grandparent MINOR (`TraceSessionAutoSaver` pattern that v3.7.0 Replay mirrors)