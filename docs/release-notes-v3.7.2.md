# peakcan-host v3.7.2 PATCH — defensive clean-up of 3 bare `Substitute.For<IAutoSavePrefsStore>()` mocks

## Summary

v3.7.2 closes the v3.7.1 release-notes §"Non-scope" follow-up: the 3 remaining bare `Substitute.For<IAutoSavePrefsStore>()` calls in `AppLifecycleShutdownTests` are replaced with the shared `InMemoryPrefsStore` helper from v3.7.1. Pure test-only defense-in-depth — same NSubstitute-vs-real-fake time-bomb pattern as the v3.7.1 [Skip] fix, but pre-emptively rather than after a latent test failure surfaces.

## Why this PATCH

- v3.7.1 release-notes §"Non-scope" explicitly deferred these 3 sites because they currently pass — `App.RunShutdownAsync` does not call `ApplyAutoSnapshotAsync` / `_prefs.LoadAsync`, so NSubstitute's `Task.FromResult(default)` auto-stub is never dereferenced.
- The pattern is the same time-bomb as the v3.7.1 [Skip] fix: if a future test (or a future code change to `App.RunShutdownAsync`) ever drives the `ApplyAutoSnapshotAsync` path through the lifecycle hooks, the bare mock would NRE on `prefs.NeverRestore`, get caught, return `RestoreAnswer.ApplyFailed`, and silently mask the actual test intent. v3.7.1's `nsubstitute-task-t-default-record-null-silent-failure` lesson (#153 candidate) explicitly called out this category.
- Same principle as v3.7.1: replace the bare mock with the real fake BEFORE the bug can manifest, not after.

## Why currently passes (why this isn't a "fix a bug" but a "lock the boundary")

`App.RunShutdownAsync` invokes `TrySaveAutoSnapshotAsync` (which calls `_vmProvider.GetCurrent()` + `vm.BuildSnapshot()` + `_library.Save(dto, path)`). It does NOT call `ApplyAutoSnapshotAsync` (which is the only path that touches `_prefs.LoadAsync`). So the bare `Substitute.For<IAutoSavePrefsStore>()` mocks — which auto-stub `LoadAsync` to `Task.FromResult(default(AutoSavePrefs))` where `default(AutoSavePrefs) == null` — are never dereferenced, never NRE, never produce `RestoreAnswer.ApplyFailed`. All 8 `AppLifecycleShutdownTests` pass with the bare mocks today.

But the **surface area is one refactor away** from exposing the bug. Defensive clean-up now locks the boundary.

## What changed

1 commit, 1 file modified (+ new release-notes):

| Path | Δ | Fix |
|------|---|-----|
| `tests/PeakCan.Host.App.Tests/AppLifecycleShutdownTests.cs` | +4 / -3 | Added `using PeakCan.Host.App.Tests.Services.Trace;` for `InMemoryPrefsStore` visibility. Replaced 3 sites: `MakeTraceSaver` factory (`var prefs = Substitute.For<IAutoSavePrefsStore>()` → `new InMemoryPrefsStore()`), `MakeReplaySaver` factory (same), inline `badReplaySaver` ctor in `TraceSucceeds_ReplayThrows_StillStopsHost` (same). |
| `docs/release-notes-v3.7.2.md` | NEW | This file. |

**No production file changes. No schema changes. No IPC changes.** Reuses the `InMemoryPrefsStore` helper from v3.7.1 PATCH (`tests/PeakCan.Host.App.Tests/Services/Trace/InMemoryPrefsStore.cs`) verbatim.

## Fix-by-fix detail

### Fix 1 — `MakeTraceSaver` factory (line 162)

```diff
-        var prefs = Substitute.For<IAutoSavePrefsStore>();
+        var prefs = new InMemoryPrefsStore();
```

Used by 5 of the 8 tests (`RunShutdownAsync_AutoSaverRunsBeforeHostStop`, `RunShutdownAsync_NullHost_ThrowsArgumentNull`, `RunShutdownAsync_HostStopThrows_LogsError_DoesNotPropagate`, `RunShutdownAsync_AutoSaveThrows_LogsWarning_DoesNotBlockHostStop`, `RunShutdownAsync_BothAutoSaversRunBeforeHostStop`, `RunShutdownAsync_ReplayResolverReturnsNull_TraceStillRuns`). All continue to pass — `App.RunShutdownAsync` only calls `TrySaveAutoSnapshotAsync` on the saver, never `ApplyAutoSnapshotAsync`, so neither `LoadAsync` nor `SaveAsync` are exercised.

### Fix 2 — `MakeReplaySaver` factory (line 179)

Same swap. Used by `RunShutdownAsync_BothAutoSaversRunBeforeHostStop`.

### Fix 3 — inline `badReplaySaver` ctor (line 391, in `TraceSucceeds_ReplayThrows_StillStopsHost`)

```diff
-            Substitute.For<IAutoSavePrefsStore>(),
+            new InMemoryPrefsStore(),
             Substitute.For<IMessageBoxPrompt>(),
```

The `prompt` bare mock stays — `App.RunShutdownAsync` does NOT invoke `ApplyAutoSnapshotAsync`, so `prompt.ShowAsync` is also unreachable. Documented as a future v3.7.x follow-up if a test ever exercises the prompt path from `RunShutdownAsync`.

## Test delta

| Suite | v3.7.1 | v3.7.2 | Δ |
|-------|--------|--------|---|
| App | 666 + 3 SKIP | **666 + 3 SKIP** | 0 |
| Core | 416 | 416 | 0 |
| Infrastructure | 84 + 2 SKIP | 84 + 2 SKIP | 0 |
| **Total** | **1166 + 5 SKIP** | **1166 + 5 SKIP** | **0** |

Defensive clean-up, zero test count change. All 8 `AppLifecycleShutdownTests` continue to pass.

## Process notes

- 1-commit PATCH; no per-chunk review (PATCH scope too small to warrant 2-stage review)
- Plan mode used; plan file at `C:\Users\13777\.claude\plans\serialized-popping-jellyfish.md`
- 1 transient Core test flake observed during full-suite run (`dotnet test` reported 415 pass + 1 fail once; re-run confirmed 416 pass / 0 fail). Not related to v3.7.2 changes (we touched only `tests/.../AppLifecycleShutdownTests.cs`; Core suite is net10.0, separate from net10.0-windows App suite). Documenting per established logging convention.

## Pre-ship review

| Severity | Count | Notes |
|----------|-------|-------|
| CRITICAL | 0 | — |
| HIGH     | 0 | — |
| MEDIUM   | 0 | — |
| LOW      | 0 | Defensive clean-up; mirrors the v3.7.1 InMemoryPrefsStore pattern verbatim |
| **Verdict** | — | **APPROVE** |

## Tier 3 ship

- **Parent**: v3.7.1 PATCH on origin/main (`cb75b0cdf13bfc893d077d768e1a71229a2c2e3a`)
- **Overlay**: 2 files (1 modified test + 1 new release notes)
- **Tag**: `v3.7.2` (PATCH, non-breaking)

## Non-scope (still deferred)

- `prompt` mock at `AppLifecycleShutdownTests.cs:392` — bare `Substitute.For<IMessageBoxPrompt>()` would silently NRE → `ApplyFailed` if a future test exercises prompt path. Real `IMessageBoxPrompt` test fake needed (separate from `InMemoryPrefsStore`).
- v1.6.0 MINOR OEM `IKeyDerivationAlgorithm` concrete — 65th consecutive deferred list, crypto review needed

## Closest cousins / related

- [[peakcan-host-v3-7-1-patch-shipped]] — parent PATCH (this PATCH closes its §"Non-scope" promise)
- [[peakcan-host-v3-7-0-minor-shipped]] — grandparent MINOR (introduced the 3 bare mocks via `App.RunShutdownAsync` Replay flush)
- [[nsubstitute-task-t-default-record-null-silent-failure]] — v3.7.1 lesson (#153 candidate); v3.7.2 is the second installment of the same defensive pattern