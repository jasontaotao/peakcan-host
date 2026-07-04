# v3.1.0 MINOR — Factor `RateLimitStatus` helper (3-way DRY + W1 silent-log fix) (2026-07-04)

## Summary

Closes the MEDIUM (deferrable) flagged by the v3.0.9 PATCH code-reviewer:
the rate-limit chip plumbing was triplicated across `SendViewModel` /
`DbcSendViewModel` / `MultiFrameSendViewModel`. v3.1.0 factors the shared
try/catch + `[LoggerMessage]` + null-guard into a single
`RateLimitStatus.Refresh` helper, deleting ~30 lines of duplicated plumbing.

Also fixes **W1** — a silent-logging regression introduced in v3.0.9 PATCH:
`DbcSendViewModel` and `MultiFrameSendViewModel` were hardcoding
`NullLogger<X>.Instance` in their rate-limit refresh catch blocks, silently
dropping provider exceptions. v3.1.0 normalizes all 3 VMs to inject real
`ILogger<>` via DI, so any future provider failure surfaces as a real
Warning log entry.

Zero XAML changes. Zero DI-factory shape changes (`Func<long>?` still the
last optional ctor arg). Zero behavior changes (helper preserves all 3
original paths: null provider → no-op, throwing provider → keep last +
log warning, valid provider → return new value).

**8 files modified, 2 new files (+~310 / −~95 net).**

## Why a static helper (not a base class / interface / composition)

Documented in the helper's class-level XML doc (`RateLimitStatus.cs:1-46`),
but the short version:

- **CommunityToolkit.Mvvm 8.4.2 source-gen** requires `[ObservableProperty]`
  and `partial void OnXxxChanged` to live on the declaring class. They
  cannot move to a base class, interface, or composed helper.
- **XAML binding shape** `{Binding RateLimitRejectedVisibility}` is
  preserved by keeping the computed property on each VM. Composition
  would have required `{Binding RateLimit.RateLimitRejectedVisibility}` —
  3 XAML + 12 test updates for zero behavior gain.
- **`Func<long>?`** captures the singleton `RateLimitedSendService` via
  closure. Adding an `IRateLimitRejectedCountProvider` interface would
  require an extra DI registration for the 200 ms / 100 ms poll sites
  that aren't hot-pathed.

The remaining 3-way duplication (the `[ObservableProperty]` +
`RateLimitRejectedVisibility` computed + `OnRateLimitRejectedCountChanged`
hook triplet) is a known ceiling — cannot be factored without breaking
source-gen or XAML.

## Files modified

| File | Δ | Purpose |
|------|---|---------|
| `src/PeakCan.Host.App/ViewModels/RateLimitStatus.cs` | **NEW +118** | Static helper — `Refresh(Func<long>?, long, ILogger?) => long` + `[LoggerMessage]` source-gen + class-level "why not X" XML doc |
| `src/PeakCan.Host.App/ViewModels/SendViewModel.cs` | −20 / +1 | Replace try/catch block in `Poll()` with single `RateLimitStatus.Refresh()` call; remove local `[LoggerMessage]` declaration |
| `src/PeakCan.Host.App/ViewModels/DbcSendViewModel.cs` | −22 / +5 | Add `ILogger<DbcSendViewModel>` ctor param + field + assignment (**W1 fix**); replace try/catch with helper call; remove local `[LoggerMessage]` declaration |
| `src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs` | −27 / +9 | Add `ILogger<MultiFrameSendViewModel>` ctor param + field + assignment; update 1-arg + 2-arg ctor chains to pass `NullLogger<MultiFrameSendViewModel>.Instance` (**W1 fix**); replace try/catch with helper call; remove local `[LoggerMessage]` declaration |
| `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` | −0 / +2 | Inject `sp.GetRequiredService<ILogger<DbcSendViewModel>>()` and `sp.GetRequiredService<ILogger<MultiFrameSendViewModel>>()` into the 2 factories (`SendViewModel` factory already had it) |
| `tests/PeakCan.Host.App.Tests/ViewModels/DbcSendViewModelTests.cs` | −7 / +7 | Add `NullLogger<DbcSendViewModel>.Instance` to all 7 SUT construction calls |
| `tests/PeakCan.Host.App.Tests/ViewModels/DbcSendViewModelCyclicTests.cs` | −5 / +5 | Add `NullLogger<DbcSendViewModel>.Instance` to all 5 SUT construction calls |
| `tests/PeakCan.Host.App.Tests/ViewModels/MultiFrameSendViewModelTests.cs` | −0 / +1 | Add `logger:` named arg to the 1 SUT construction call that already used named args |
| `tests/PeakCan.Host.App.Tests/ViewModels/RateLimitStatusTests.cs` | **NEW +100** | 5 unit tests for the helper: null-provider, value-provider, throw-returns-current, throw-logs-warning, idempotent-equal-value |
| `docs/release-notes-v3.1.0.md` | **NEW +this** | Release notes |

## Test count

| Suite | v3.0.9 | v3.1.0 | Δ |
|-------|--------|--------|---|
| App | 532 | **537** | **+5** (`RateLimitStatusTests`) |
| Core | 393 | 393 | 0 |
| Infrastructure | 84 | 84 | 0 |
| **Total** | **1009 + 6 SKIP** | **1014 + 6 SKIP** | **+5 net** |

5 new tests in `RateLimitStatusTests.cs`:

1. `Refresh_ReturnsCurrentValue_WhenProviderIsNull` — no-op branch.
2. `Refresh_ReturnsNewValue_WhenProviderReturnsValue` — happy path.
3. `Refresh_ReturnsCurrentValue_WhenProviderThrows` — catch + keep-current.
4. `Refresh_LogsWarning_WhenProviderThrows` — verifies `[LoggerMessage]`
   source-gen emits exactly one Warning entry with the expected message.
5. `Refresh_AllowsProviderReturningSameValue_Repeatedly` — idempotency.

The 12 existing per-VM `RateLimitRejectedCount_*` tests are kept verbatim
(4 per VM × 3 VMs). They are integration tests for the VM shape — not
redundant with the new helper unit tests.

Pre-ship code review (sonnet): **0C / 0H / 0M / 3L — APPROVE ship-as-is.**

3 LOW findings (all non-blocking, deferred to v3.1.1 follow-up batch):

- **LOW**: No test for the `logger == null` default path (1-line test, deferred).
- **LOW**: `MultiFrameSendViewModel.cs:121-124` comment phrasing is slightly
  imprecise (cosmetic).
- **LOW**: Residual 3-way duplication of the `[ObservableProperty]` +
  computed Visibility + `OnRateLimitRejectedCountChanged` triplet is
  intentional (CommunityToolkit source-gen constraint). Documented in the
  helper's class-level XML doc.

## W1 silent-log fix

| Aspect | Before (v3.0.9) | After (v3.1.0) |
|--------|-----------------|----------------|
| `SendViewModel` catch logger | `_logger` (real `ILogger<SendViewModel>`) | `_logger` (unchanged) |
| `DbcSendViewModel` catch logger | `NullLogger<DbcSendViewModel>.Instance` (silent) | `_logger` (real `ILogger<DbcSendViewModel>` via DI) |
| `MultiFrameSendViewModel` catch logger | `NullLogger<MultiFrameSendViewModel>.Instance` (silent) | `_logger` (real `ILogger<MultiFrameSendViewModel>` via DI) |

The silent-log regression was introduced in v3.0.9 PATCH (when the DbcSend
and MultiFrame VMs were added with a minimal "no logger available" stub).
v3.1.0 fixes it as part of the refactor — strict net observability
improvement. No external behavior change visible to the user; only the
log sink gets the previously-dropped Warning entries.

## Serilog event-id consolidation (W3)

`[LoggerMessage]` source-generates an `EventId` based on the method's
declaration order within its file. v3.0.9 had 3 separate `LogPollProviderThrew`
methods, each in its own VM file (one auto-id per file). v3.1.0 collapses
them into a single `LogPollProviderThrew` in `RateLimitStatus.cs`, which
gets a single canonical auto-id.

This is a **net improvement** (one canonical id for one warning), but
operators with saved Serilog queries keyed on the per-VM auto-id should
migrate to the new single id. The warning message text is unchanged
(`"Rate-limit rejected-count provider threw during Poll; keeping last value"`)
and remains greppable.

## Pattern (post-refactor)

```
RateLimitedSendService.RejectedFrameCount  [Interlocked.Read]
        ▲
        │ every 200ms (SendViewModel, DbcSendViewModel) /
        │   100ms (MultiFrameSendViewModel)
        │
[VM]._getRejectedCount  [Func<long>? field]
        ▲
        │ RateLimitStatus.Refresh(_getRejectedCount,
        │   RateLimitRejectedCount, _logger)
        │
[VM].RateLimitRejectedCount  [ObservableProperty]
        ▲
        │ PropertyChanged → re-evaluates computed
        │
[VM].RateLimitRejectedVisibility  [Visibility enum]  ← stays on each VM
        ▲                                              (source-gen constraint)
        │ Binding
        │
SendView.xaml / MultiFrameSendWindow.xaml Border.Visibility
```

## Non-scope / follow-up items (intentional)

| Item | Why deferred |
|------|--------------|
| Factor `[ObservableProperty] + Visibility + OnXxxChanged` triplet (Plan agent B3) | **Cannot move** — CommunityToolkit source-gen requires field on declaring class; XAML binding shape would change |
| Add 3 small `RateLimitRejectedVisibility_*` tests (Plan agent B3) | Nice-to-have — Visibility computed property is indirectly tested via the OnXxxChanged PropertyChanged event in the existing 12 per-VM tests |
| `Refresh_DoesNotThrow_WhenLoggerOmitted` test (review LOW #1) | 1-line test, defer to v3.1.1 / next helper edit |
| Per-rejection visualization (which frame IDs were rejected) | Out of scope |
| Notify on reject (flash on reject) | Visual noise |
| v1.6.0 MINOR OEM `IKeyDerivationAlgorithm` concrete | 40th consecutive list; crypto review needed |
| v2.2 🔜 ODX CONDITIONAL / ECU-VARIANT | Implementation still pending |

## Process notes

- **Branch:** `feature/v3-1-0-rate-limit-status-helper` (1 commit on top of
  `de1d0b92` v3.0.9 squash).
- **Pre-ship review:** code-reviewer (sonnet) — 0C / 0H / 0M / 3L.
  **APPROVE — ship as-is.**
- **Build verification:** `dotnet build PeakCan.Host.slnx -c Debug`
  → 0 warnings, 0 errors.
- **Test verification:** `dotnet test PeakCan.Host.slnx`
  → 1013 + 6 SKIP / 1 fail (race flake). `CyclicSendServiceRaceTests`
  flake pattern observed — passes in isolation (6/6), documented
  pre-existing flake per MEMORY since v1.6.2.
- **Race-test counter:** 23-of-23+ (no new race-prone code added; the
  refactor touches polling + closure capture only, no new shared state).
- **Ship mechanism:** Tier 3 (`tier3_v310.py` — clone of `tier3_v309.py`,
  PARENT_SHA `de1d0b92`, 9 file overlays).
- **DRY debt closed:** ~30 lines deleted across 3 VMs, replaced by 1 line
  per VM call site + ~30 lines of helper (including the class-level XML
  doc).