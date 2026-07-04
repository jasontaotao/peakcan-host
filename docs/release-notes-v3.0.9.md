# v3.0.9 PATCH — Extend rate-limit chip to DBC Send + Multi-frame (2026-07-04)

## Summary

Extends the v3.0.8 PATCH yellow "rate limit rejected: N" chip from the
single-shot Send section to the two highest-throughput call sites:
**DBC Send** (one frame per encode) and **Multi-frame Send** (iterations ×
frames per second). All 3 VMs share the same `SendService` →
`RateLimitedSendService` singleton, so all 3 chips show the same
counter.

The single-shot Send chip stays where it was (v3.0.8 PATCH). Two new
chips appear:

1. **SendView.xaml DBC Mode expander** — after the existing red
   `ErrorMessage` TextBlock, before the DBC cyclic sub-panel.
2. **MultiFrameSendWindow.xaml status bar** — left of the existing
   gray `StatusText` TextBlock.

Same yellow chip design (`#FFF8E1` background, `#D4A72C` border,
`#7D4E00` text). Hidden when N = 0.

Zero code changes outside the existing pattern. Zero new files.
**7 files modified (+351 / −19)**. **8 new tests** (4 per VM).

## Files modified

- `src/PeakCan.Host.App/ViewModels/DbcSendViewModel.cs` (+86 / −2)
- `src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs`
  (+85 / −4)
- `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` (+25 / −1)
- `src/PeakCan.Host.App/Views/SendView.xaml` (+11 / −0)
- `src/PeakCan.Host.App/Windows/MultiFrameSendWindow.xaml`
  (+12 / −0)
- `tests/PeakCan.Host.App.Tests/ViewModels/DbcSendViewModelTests.cs`
  (+76 / −0)
- `tests/PeakCan.Host.App.Tests/ViewModels/MultiFrameSendViewModelTests.cs`
  (+75 / −7)

## Pattern (replicated 3× now)

```
RateLimitedSendService.RejectedFrameCount  [Interlocked.Read]
        ▲
        │ every 200ms (SendViewModel) /
        │   200ms (DbcSendViewModel) / 100ms (MultiFrameSendViewModel)
        │
[VM].RateLimitRejectedCount  [ObservableProperty]
        ▲
        │ PropertyChanged → re-evaluates computed
        │
[VM].RateLimitRejectedVisibility  [Visibility enum]
        ▲
        │ Binding
        │
SendView.xaml / MultiFrameSendWindow.xaml Border.Visibility
```

**Production DI wiring** (identical 3-arg pattern in
`AppHostBuilder.cs`):

```csharp
Func<long>? rejectedCountProvider = sendSvc is RateLimitedSendService rateLimited
    ? () => rateLimited.RejectedFrameCount
    : null;
```

Each VM factory passes the provider to the ctor. Defensive try/catch
+ `[LoggerMessage]` for thrown providers matches the v3.0.8
SendViewModel convention.

## Test count

| Suite | v3.0.8 | v3.0.9 | Δ |
|-------|--------|--------|---|
| App | 524 | 532 | **+8** (`RateLimitRejectedCount_*` × 2 VMs) |
| Core | 393 | 393 | 0 |
| Infrastructure | 84 | 84 | 0 |
| **Total** | **1001 + 6 SKIP** | **1009 + 6 SKIP** | **+8 net** |

8 new tests:

- 4 × `DbcSendViewModelTests.RateLimitRejectedCount_*`
- 4 × `MultiFrameSendViewModelTests.RateLimitRejectedCount_*`

Each VM: default 0 / updates / stays 0 / idempotent PropertyChanged
guard (matching the v3.0.8 SendViewModel test shape).

Pre-ship code review (sonnet agent): **APPROVE — clean, ship as-is**.
Findings:
- **MEDIUM (deferrable)**: Rate-limit chip pattern triplicated across
  3 VMs (~90 lines of identical plumbing). Third copy is the DRY
  threshold per CLAUDE.md. **Action**: factor into a `RateLimitStatus`
  mixin in v3.1.0 MINOR; add to v3.1.0 candidates list.
- **LOW**: MultiFrameSendViewModel test uses reflection on
  `_getRejectedCount` private field — acceptable per project
  precedent (mirrors `DbcService` event reflection).
- **LOW**: `DockPanel.Dock="Right"` ordering — chip appears left
  of StatusText (existing convention). Acceptable.

## Non-scope / follow-up items (intentional)

| Item | Why deferred |
|------|--------------|
| Factor `RateLimitStatus` mixin (DRY v3.0.9 MEDIUM) | v3.1.0 MINOR candidate — third copy is the DRY threshold |
| Per-rejection visualization (which frame IDs were rejected) | Out of scope — current chip shows aggregate count only |
| Notify on reject (flash on reject) | Visual noise — chip change at 200ms poll cadence is sufficient |
| v1.6.0 MINOR OEM `IKeyDerivationAlgorithm` concrete | 39th consecutive list; crypto review needed |
| v2.2 🔜 ODX CONDITIONAL / ECU-VARIANT (closed in §14.2b wording v3.0.4) | Implementation still pending |

## Process notes

- **Branch:** `feature/v3-0-9-patch` (1 commit: `fb685d3`).
- **Pre-ship review:** code-reviewer (sonnet) ran via Agent tool.
  0C / 0H / 1M (deferrable) / 2L (non-blocking). **APPROVE — ship
  as-is.**
- **Build verification:** `dotnet build PeakCan.Host.slnx -c Debug`
  → 0 warnings, 0 errors.
- **Test verification:** `dotnet test PeakCan.Host.slnx` →
  1009 + 6 SKIP / 0 fail. (`CyclicSendServiceRaceTests` flake
  pattern observed — passes in isolation, documented pre-existing
  flake per MEMORY; no new race-prone code added.)
- **Ship mechanism:** 7-call Tier 3 — `tier3_v309.py` is a clone
  of `tier3_v308.py` with updated `PARENT_SHA` (`5f451435`
  instead of `5e2768d7`). Same `encoding='utf-8'` fix carried
  forward.
- **Applied v3.0.6 + v3.0.7 lessons:** Tier 3 overlay blob will
  be retrieved from `gh api .../contents/?ref=5f451435` (parent-
  aligned) + base64 decode + LF normalization + new commit.
- **DRY debt introduced**: 3-way duplication now totals ~90 lines
  (SendViewModel 30 + DbcSendViewModel 30 + MultiFrameSendViewModel 30).
  Worth factoring in v3.1.0 MINOR.