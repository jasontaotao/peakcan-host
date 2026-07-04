# v3.0.8 PATCH — Close A4 orphan (2026-07-04)

## Summary

Closes **A4 orphan** (`RateLimitedSendService.RejectedFrameCount`
internal counter never exposed to UI), deferred since v2.1.7
PATCH (`a3b00b48` 2026-07-02). The counter has been accumulating
since v1.6.5 PATCH shipped the rate-limit decorator, but only
tests consumed it; UI operators could not see when their burst
exceeded `MaxFramesPerSecond`.

A small yellow chip appears next to the existing `Status`
TextBlock in the single-shot Send section:

> **rate limit rejected: N**

Hidden when N = 0 (no rejections, or rate-limit decorator absent).
Visible (yellow background, amber text) when N > 0. Polls every
200 ms via the existing DispatcherTimer, same path as the
`CyclicSuccessCount` / `CyclicFailureCount` chips.

Zero new files. 4 files modified (+202 / −10). Zero public API
additions (the `Func<long>? rateLimitRejectedCountProvider`
parameter on `SendViewModel` is optional and internal).

## Files modified

- `src/PeakCan.Host.App/ViewModels/SendViewModel.cs` (+96 / −2)
- `src/PeakCan.Host.App/Views/SendView.xaml` (+18 / −0)
- `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` (+24 / −1)
- `tests/PeakCan.Host.App.Tests/ViewModels/SendViewModelTests.cs`
  (+73 / −7)

## Architecture

**Data flow:**

```
RateLimitedSendService.RejectedFrameCount  [Interlocked.Read]
        ▲
        │ every 200ms (DispatcherTimer.Tick → Poll)
        │
SendViewModel.RateLimitRejectedCount  [ObservableProperty]
        ▲
        │ PropertyChanged → re-evaluates computed
        │
SendViewModel.RateLimitRejectedVisibility  [Visibility enum]
        ▲
        │ Binding
        │
SendView.xaml Border.Visibility  [Visible / Collapsed]
```

**Production DI wiring** (`AppHostBuilder.cs`):

```csharp
builder.Services.AddSingleton<SendViewModel>(sp =>
{
    var sendSvc = sp.GetRequiredService<SendService>();
    Func<long>? rejectedCountProvider = sendSvc is RateLimitedSendService rateLimited
        ? () => rateLimited.RejectedFrameCount
        : null;
    return new SendViewModel(sendSvc, ..., rateLimitRejectedCountProvider: rejectedCountProvider);
});
```

The pattern-match always succeeds in production (the current DI
always wraps `SendService` in a `RateLimitedSendService` factory,
even when `MaxFramesPerSecond=0` makes the decorator's gate inert).
The null branch is defensive-only, future-proofing against a DI
refactor that might bypass the decorator entirely.

## Test count

| Suite | v3.0.7 | v3.0.8 | Δ |
|-------|--------|--------|---|
| App | 520 | 524 | **+4** (`RateLimitRejectedCount_*`) |
| Core | 393 | 393 | 0 |
| Infrastructure | 84 | 84 | 0 |
| **Total** | **997 + 6 SKIP** | **1001 + 6 SKIP** | **+4 net** |

4 new tests in `SendViewModelTests`:

1. `RateLimitRejectedCount_Defaults_To_Zero_When_Provider_Null`
2. `RateLimitRejectedCount_Updates_From_Provider_After_Poll`
3. `RateLimitRejectedCount_Stays_Zero_When_Provider_Returns_Zero`
4. `RateLimitRejectedCount_Raises_PropertyChanged_Only_When_Count_Changes`

Pre-ship code review (opus agent): 0C / 0H / 2M / 2L. **Both
MEDIUMs fixed before commit** (corrected misleading DI comment +
added defensive try/catch around the `Func<long>` provider to
match the surrounding `SendCommand` / `SaveCurrentToLibrary`
error-handling convention).

## Non-scope / follow-up items (intentional)

| Item | Why deferred |
|------|--------------|
| Per-rejection visualization (drill into which frames were rejected) | Out of scope — current chip shows aggregate count only; per-rejection telemetry would require a separate audit log |
| Notify on reject (e.g., flash the chip when N increments) | Visual noise — operators watching the chip will see it change at the 200 ms poll cadence; explicit flash would be redundant |
| Wire the same counter to DBC cyclic / Multi-frame windows | Those VMs also dispatch via `SendService`; if rate-limit is active, they share the same counter. Surfacing it there too would be a separate UI decision. |
| v1.6.0 MINOR OEM `IKeyDerivationAlgorithm` concrete | 38th consecutive list; crypto review needed |
| v2.2 🔜 ODX CONDITIONAL / ECU-VARIANT (closed in §14.2b wording v3.0.4) | Implementation still pending |

## Process notes

- **Branch:** `feature/a4-rejected-frame-count` (1 commit: `3ee93f2`).
- **Pre-ship review:** code-reviewer (sonnet) ran via Agent tool.
  0C / 0H / 2M / 2L; both MEDIUMs fixed before commit.
- **Build verification:** `dotnet build PeakCan.Host.slnx -c Debug`
  → 0 warnings, 0 errors.
- **Test verification:** `dotnet test PeakCan.Host.slnx` → 1001 +
  6 SKIP / 0 fail. (`CyclicDbcSendServiceRaceTests` flake pattern
  observed — passes in isolation, documented pre-existing flake
  per MEMORY race-test-flake pattern; no new race-prone code.)
- **Ship mechanism:** 7-call Tier 3 — `tier3_v308.py` is a clone of
  `tier3_v307.py` with updated `PARENT_SHA` (`5e2768d7` instead
  of `106368d5`). Same `encoding='utf-8'` fix carried forward.
- **Applied v3.0.6 + v3.0.7 lessons:** Tier 3 overlay blob will be
  retrieved from `gh api .../contents/?ref=5e2768d7` (parent-
  aligned) + base64 decode + LF normalization + new commit.