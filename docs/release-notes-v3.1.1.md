# v3.1.1 PATCH — Close v3.1.0 review LOWs + Plan agent B3 (2026-07-04)

## Summary

Closes the 3 v3.1.0 review LOWs (all deferred "ship-as-is, address in
follow-up"):

1. **LOW #1** (review): Missing test for the `logger == null` default path
   in `RateLimitStatus.Refresh`. Added
   `Refresh_DoesNotThrow_WhenLoggerOmitted` — pins the `logger ?? _defaultLogger`
   fallback to `NullLogger.Instance`.
2. **LOW #2** (review): `MultiFrameSendViewModel.cs` 2-arg ctor comment was
   imprecise about the v3.0.9 → v3.1.0 logger position history.
   Tightened to "the v3.0.9 ctor chain had no logger param at any level —
   the logger position is new in v3.1.0, not a pre-existing null default."
3. **LOW #3 / Plan agent B3**: Missing direct tests for the
   `RateLimitRejectedVisibility` computed property (was only exercised
   indirectly via the `OnRateLimitRejectedCountChanged` PropertyChanged
   event). Added 3 tests, one per VM
   (`RateLimitRejectedVisibility_Tracks_Count_Threshold`).

Plus a code-review LOW-of-LOWs: trailing newline added to
`MultiFrameSendViewModelTests.cs` (cosmetic, was the only file in the
patch lacking one).

**5 files modified, 1 new file (+96 / −4 net).**

Zero production behavior change. Zero new files in production code. Zero
XAML changes. Zero DI changes.

## Files modified

- `src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs` — XML doc
  tightening on the 2-arg ctor.
- `tests/PeakCan.Host.App.Tests/ViewModels/RateLimitStatusTests.cs` —
  +1 test (`Refresh_DoesNotThrow_WhenLoggerOmitted`).
- `tests/PeakCan.Host.App.Tests/ViewModels/SendViewModelTests.cs` —
  +1 test (`RateLimitRejectedVisibility_Tracks_Count_Threshold`).
- `tests/PeakCan.Host.App.Tests/ViewModels/DbcSendViewModelTests.cs` —
  +1 test (`RateLimitRejectedVisibility_Tracks_Count_Threshold`).
- `tests/PeakCan.Host.App.Tests/ViewModels/MultiFrameSendViewModelTests.cs` —
  +1 test (`RateLimitRejectedVisibility_Tracks_Count_Threshold`) +
  trailing newline.
- `docs/release-notes-v3.1.1.md` — NEW (+this).

## Test count

| Suite | v3.1.0 | v3.1.1 | Δ |
|-------|--------|--------|---|
| App | 537 | **541** | **+4** (`RateLimitStatusTests` +1, `*ViewModelTests` × 3 +1 each) |
| Core | 393 | 393 | 0 |
| Infrastructure | 84 | 84 | 0 |
| **Total** | **1014 + 6 SKIP** | **1018 + 6 SKIP** | **+4 net** |

4 new tests:

1. `RateLimitStatusTests.Refresh_DoesNotThrow_WhenLoggerOmitted` — pins
   the `logger ?? NullLogger.Instance` fallback. Without the fallback,
   `LogPollProviderThrew(null, ex)` would NRE inside the source-gen
   `IsEnabled` check.
2. `SendViewModelTests.RateLimitRejectedVisibility_Tracks_Count_Threshold`
   — count = 0 → Collapsed, count = 5 → Visible.
3. `DbcSendViewModelTests.RateLimitRejectedVisibility_Tracks_Count_Threshold`
   — same shape.
4. `MultiFrameSendViewModelTests.RateLimitRejectedVisibility_Tracks_Count_Threshold`
   — same shape.

Pre-ship code review (sonnet): **0C / 0H / 0M / 2L / 1INFO — APPROVE
ship-as-is**. Both LOWs and 1 trailing-newline nit fixed before ship.

## Non-scope / follow-up items (intentional)

| Item | Why deferred |
|------|--------------|
| `RateLimitRejectedVisibility` PropertyChanged propagation on 0→0 (review INFO) | Non-event by CommunityToolkit.Mvvm design — setter short-circuits on equal values; already indirectly covered by the existing `RateLimitRejectedCount_Raises_PropertyChanged_Only_When_Count_Changes` tests |
| Split each Visibility test into 2 (Collapsed + Visible) | Combined shape was the chosen test design (1 test pins both boundary states) |
| Factor a shared `Visibility` test factory across 3 VMs | VMs have different ctor shapes — factory would need conditional logic for marginal benefit |
| v1.6.0 MINOR OEM `IKeyDerivationAlgorithm` concrete | 41st consecutive list; crypto review needed |
| v2.2 🔜 ODX CONDITIONAL / ECU-VARIANT | Implementation still pending |

## Process notes

- **Branch:** `feature/v3-1-0-rate-limit-status-helper` (1 commit on top of
  v3.1.0 squash `5123e0f2`).
- **Pre-ship review:** code-reviewer (sonnet) — 0C / 0H / 0M / 2L / 1INFO.
  Both LOWs addressed before ship (trailing newline + comment precision).
  **APPROVE — ship as-is.**
- **Build verification:** `dotnet build PeakCan.Host.slnx -c Debug`
  → 0 warnings, 0 errors.
- **Test verification:** `dotnet test PeakCan.Host.slnx`
  → 1018 + 6 SKIP / 0 fail. (Race test did not flake on this run;
  pre-existing pattern from MEMORY still applies.)
- **Ship mechanism:** Tier 3 (`tier3_v311.py` — clone of `tier3_v310.py`,
  PARENT_SHA `5123e0f2`, 6 file overlays).
- **0 NEW lessons.** Pure test + doc cleanup; no new code patterns.