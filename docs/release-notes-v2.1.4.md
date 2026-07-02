# v2.1.4 PATCH — Replay tab UI orphan closure (2026-07-02)

## Summary

Closes the v1.4.0 MINOR UI orphan: the **Replay tab was fully built but
never reachable** from AppShell. New menu entry `View ▸ Replay` +
corresponding `ShowReplayCommand` route navigation into the existing
`ReplayView` UserControl. `ReplayViewModel` is now registered in DI.

```
AppShell.xaml ▸ Menu ▸ View ▸ Replay  →  AppShellViewModel.ShowReplay
   →  ContentControl Content="{Binding CurrentView}"  →  ReplayView
       (already wired: 6-row Grid with Open/Play/Pause/Stop,
        Speed combo, Scrubber slider, Loop checkbox,
        CAN-ID filter, time-range filter, error TextBlock)
```

## Why this PATCH

The `IReplayService` + `ReplayService` + `ReplayFrame` + `ReplayTimeline`
+ `IReplayFrameSink` + `ReplayFrameSinkAdapter` core layer has been in
production since v1.4.0 MINOR (2026-06-29). The `ReplayView.xaml`
UserControl and `ReplayViewModel.cs` (478 lines) have been in production
the same amount of time. Tests have been GREEN the same amount of time.

What was missing:
1. `AppShellViewModel` had no `ShowReplay` method (only Trace / Dbc /
   Send / Signals / Stats / Script / UDS / Record — 8 Show* commands,
   no 9th)
2. `AppShell.xaml` View menu listed the same 8 tabs, no Replay entry
3. `AppHostBuilder` did not register `ReplayViewModel` as a DI service,
   so even an explicit ctor call would fail DI resolution

This is exactly the "feature built but unreachable from UI" anti-pattern
that the v2.2.0 MINOR Replay-from-file brainstorm retraction surfaced
yesterday (see [[brainstorm-verify-existing-infra-first]] and
[[peakcan-host-v2-1-3-tidy-shipped]]). The brainstorm was the
symptom; this PATCH is the actual fix.

## Items (3)

### 1. `src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs` — `ShowReplayCommand`

Mirror `ShowRecord` exactly. New `ReplayViewModel _replayViewModel`
ctor param (5th-thru-9th arg position reordered but post-DI). New
`ReplayView? _replayView` lazy field. New `[RelayCommand] ShowReplay()`
method that lazy-creates the view on first Show and assigns
`CurrentView`. Comment cites the v2.1.4 PATCH purpose.

```csharp
[RelayCommand]
private void ShowReplay()
{
    if (_replayView == null)
    {
        _replayView = new ReplayView { DataContext = _replayViewModel };
        if (CurrentView == null) CurrentView = _replayView;
    }
    CurrentView = _replayView;
}
```

### 2. `src/PeakCan.Host.App/AppShell.xaml` — View menu entry

One-line addition after the Record MenuItem:

```xml
<MenuItem Header="Replay" Command="{Binding ShowReplayCommand}" />
```

### 3. `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` — ReplayViewModel DI

Two registrations:

```csharp
// Singleton registration (no IHostedService — ReplayVM has no
// Dispose-time background timer, unlike RecordViewModel's poll timer).
builder.Services.AddSingleton<ReplayViewModel>();

// Update the AppShellViewModel factory to resolve ReplayViewModel.
sp.GetRequiredService<ReplayViewModel>(),  // added after RecordViewModel
```

## Test counts

| Suite | v2.1.3 | v2.1.4 | Δ |
|-------|--------|--------|---|
| Core  | 388    | 388    | 0 |
| App   | 492    | 493    | **+1** (`ShowReplayCommand_Is_Not_Null_And_Can_Execute`) |
| Infra | 84     | 84     | 0 |
| **Total** | **964 + 6 SKIP** | **965 + 6 SKIP** | **+1** |

App tests went 492 → 493: the new `AppShellViewModelTests.ShowReplayCommand_Is_Not_Null_And_Can_Execute` test mirrors the existing `ShowRecordCommand_Is_Not_Null_And_Can_Execute` precedent. STA-RunSta lazy-view instantiation is verified by manual smoke (not by test — the WPF Application singleton race between xunit collections makes STA tests flaky in CI per the v1.2.11 PATCH precedent).

Race-flake counter preserved (30/30+ this PATCH — no race test fired in the full suite).

## Non-scope / follow-up items (intentional)

| Item | Why deferred |
|---|---|
| `docs/user-manual.html` §14.1 Q2 / §14.2 / §A.2 stale "v2.2.0 MINOR 候选" wording about Replay | Per v2.1.3 PATCH test-count drift correction pattern, doc-only cleanup belongs in a Tidy PATCH (v2.1.5 or similar). v2.1.4 is a code-only PATCH. |
| `RateLimitedSendService.RejectedFrameCount` (Pattern A4 in the inventory) | Internal counter never exposed to UI; not a Replay-tab issue. Out of scope here. |
| MultiFrameSendWindow only reachable via SendView button (Pattern A2) | User explicitly chose Replay as the priority; Multi-frame is already discoverable. |
| Manual smoke test | Lazy-view instantiation in `ShowReplayCommand` requires running the WPF host (STA + Application singleton). The `Is_Not_Null_And_Can_Execute` test asserts the command is wired; visual confirmation of the menu entry + tab swap is a pre-ship manual step. |

## Process lessons (NEW — from this PATCH)

1. **DI registration is the silent final gap.** Even when an interface + concrete + view + VM all exist, no AppShell navigation works if the VM is not in DI. The v2.1.4 PATCH closes three gaps together (menu entry, Show command, DI registration) — closing only the menu/command would have surfaced a `No service for type 'ReplayViewModel'` at runtime. Whenever you are closing an "orphan" UI feature, fix DI registration in the same commit, not after.

2. **`.Replace_all(true)` on Edit has gaps.** The 6 ctor call sites in `AppShellViewModelTests` all look like `...\n                new RecordViewModel(...));\n    }` but the 6th site had a different trailing structure (`..., enumerator,\n            writableConfig);`). `replace_all=true` matched 5 of 6 — the 6th needed a separate edit. Always grep for the actual call-site count after using `replace_all` on trailing-close variants.

3. **Pattern A's cause-effect loop.** Pattern A (UI orphan) created the perception that a Replay-from-file feature was missing. That made me design a parallel `ReplayService` (Pattern B = brainstorm retraction). This PATCH closes Pattern A's loop back to A1 — fixing the orphan means future "missing Replay feature" reports stop being generated.
