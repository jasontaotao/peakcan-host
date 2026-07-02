# v2.1.7 PATCH — Multi-frame AppShell menu entry (Pattern A2 orphan) (2026-07-02)

## Summary

Closes the v2.1.0 MINOR Pattern A2 orphan: the **Multi-frame send window**
(`MultiFrameSendWindow` + `MultiFrameSendViewModel` + `SequenceSendService`)
was fully built and SendView held a button to open it, but the AppShell
View menu had no route. End-user effect: a feature was reachable only
from inside the Send tab; users who didn't visit Send couldn't discover
it.

This PATCH adds a `View ▸ Multi-frame...` menu entry that opens the
shared `MultiFrameSendWindow` against the singleton
`MultiFrameSendViewModel`.

```
AppShell.xaml ▸ Menu ▸ View ▸ Multi-frame...  →  AppShellViewModel.OpenMultiFrameCommand
   →  new MultiFrameSendWindow(_multiFrameSendViewModel)  →  Window.Show()
       (already wired: 6-row sequence list, mode (concurrent/sequential),
        iteration count, Raw + DBC row kinds, named sequence persistence)
```

## Items (4 files + 1 doc)

### 1. `src/PeakCan.Host.App/AppShell.xaml`

One-line addition after the Replay MenuItem:

```xml
<MenuItem Header="Multi-frame..." Command="{Binding OpenMultiFrameCommand}" />
```

### 2. `src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs`

Three changes:
- `using System.Windows;` + `using PeakCan.Host.App.Windows;` added
- Ctor parameter `MultiFrameSendViewModel multiFrameSendViewModel` added
  (required, matches ReplayViewModel precedent)
- Field `private readonly MultiFrameSendViewModel _multiFrameSendViewModel;`
- `[RelayCommand] private void OpenMultiFrame()` opens a fresh
  `MultiFrameSendWindow` against the shared VM, sets Owner to the main
  WPF window, and calls `Show()`

### 3. `src/PeakCan.Host.App/Composition/AppHostBuilder.cs`

`AppShellViewModel` factory: +1 arg
`sp.GetRequiredService<PeakCan.Host.App.ViewModels.MultiFrameSendViewModel>()`.
`MultiFrameSendViewModel` was already registered as DI singleton at the
top of the multi-frame section (since v2.1.0 MINOR).

### 4. `tests/PeakCan.Host.App.Tests/ViewModels/AppShellViewModelTests.cs`

- +`using PeakCan.Host.App.Services.MultiFrame;` (for SequenceSendService)
- All 6 ctor call sites updated:
  `new MultiFrameSendViewModel(new SequenceSendService(new SendService(NullLogger<SendService>.Instance)))`
  appended after the ReplayViewModel arg. Two notes:
  - `SequenceSendService` is `sealed`, so `Substitute.For<SequenceSendService>()`
    fails with `TypeLoadException: parent type is sealed`. Real instance
    with a real SendService(NullLogger) is used instead.
  - `SendService` has a required `(ILogger<SendService> logger)` ctor,
    so `Substitute.For<SendService>()` also fails. Real SendService
    instance with NullLogger is used.
- +1 new test `OpenMultiFrameCommand_Is_Not_Null_And_Can_Execute` mirroring
  `ShowReplayCommand_Is_Not_Null_And_Can_Execute` precedent.

### 5. `docs/release-notes-v2.1.7.md` — NEW

This file.

## Test counts

| Suite          | v2.1.6 | v2.1.7 | Δ |
|----------------|--------|--------|---|
| Core           | 387    | 387    | 0 |
| App            | 493    | 494    | **+1** (`OpenMultiFrameCommand_Is_Not_Null_And_Can_Execute`) |
| Infrastructure | 84     | 84     | 0 |
| **Total**      | **964 + 6 SKIP** | **965 + 6 SKIP** | **+1** |

Race-flake counter preserved (30/30+; `RecordServiceChannelTests.Writer_Flushes_Every_One_Second`
fired once in the full-suite run but passes in isolation, consistent with
the pre-existing flake pattern).

## Known minor gap (out of scope for this PATCH)

If the user opens Multi-frame via **both** routes (SendView button +
AppShell menu), two independent window instances coexist pointing at
the same singleton VM. SendViewModel's button has a single-window
guard (`_openMultiFrameWindow` field with `IsVisible` check + Activate);
AppShellViewModel does NOT have that guard. Window-state consolidation
is a separate refactor candidate (tracked for v2.2 MINOR if/when the
user prioritizes; this PATCH is intentionally scoped to "add the menu
route" — closing Pattern A2 — without coupling to SendViewModel's
existing window-state machinery).

## Non-scope / follow-up items (intentional)

| Item | Why deferred |
|---|---|
| Pattern A4 `RejectedFrameCount` exposure to UI | Internal counter never exposed to UI; unrelated to Pattern A2. |
| `docs/user-manual.html` Multi-frame 章节是否提及 View ▸ Multi-frame | Manual doc-only update — could be a v2.1.8 PATCH Tidy if user wants. Currently the manual only describes Multi-frame from the Send tab path. |
| Multi-frame window-state consolidation (single shared window instance across all entry points) | Out of scope; see "Known minor gap" above. |

## Process lessons (NEW — from this PATCH)

1. **`SequenceSendService` is sealed** (since v2.1.0 MINOR). NSubstitute
   / Castle.DynamicProxy can't mock sealed types. The existing
   `MultiFrameSendViewModelTests` pattern already works around this
   with `new SequenceSendService(realSendService)`. **When wiring a
   new test fixture that needs an instance of a sealed service,
   default to: `new ServiceType(realDeps)` over `Substitute.For<ServiceType>()`.**
   The failing test is the canary — same 1-line message
   (`Could not load type 'Castle.Proxies.<Name>Proxy' from assembly
   'DynamicProxyGenAssembly2' because the parent type is sealed`) every
   time.

2. **`SendService` has a required `(ILogger<SendService>)` ctor**
   (no parameterless). `Substitute.For<SendService>()` fails with
   `Could not find a parameterless constructor`. Same workaround
   applies: pass a real `SendService(NullLogger<SendService>.Instance)`.
   Both lessons are part of a broader pattern: **mocking .NET classes
   via NSubstitute needs the type to be (a) non-sealed AND (b)
   parameterless-constructible OR you must pass constructor arguments
   that match a real ctor.**

3. **Pre-ship code-review (or pre-test, in this case) catches NSubstitute
   sealed-type failures immediately.** v2.1.4 PATCH didn't trip on
   this because `ReplayViewModel` was the test seam, not
   `SequenceSendService`. The lesson: when adding a new VM ctor param,
   check whether any required dep is sealed/non-parameterless BEFORE
   writing the test fixture, to avoid the test-wiring rollback loop.