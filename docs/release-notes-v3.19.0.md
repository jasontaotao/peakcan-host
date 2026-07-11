# Release Notes v3.19.0 — AppShellViewModel god-class refactor (MINOR)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.19.0
**Branch:** `feature/w4-app-shell-god-class`
**Parent:** v3.18.0 PATCH (`133055c` on origin/main)

## Why this MINOR

`src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs` had grown to
**1019 LoC** as of v3.18.0 — too large to hold in context for editing,
review, or diff. Single sealed partial class owned 4 distinct
responsibilities stacked end-to-end:

| Flow | Responsibility | Methods | ~LoC |
|---|---|---|---|
| A | Channel lifecycle (enumerate/connect/disconnect/error) | 4 commands + 2 partial void | ~280 |
| B | View navigation (Show* + OpenMultiFrame + ShowTraceViewer) | 13 methods | ~240 |
| C | Session open/save (OpenDbc, Open/SaveSession, recent) | 6 methods | ~130 |
| D | Log helpers (LoggerMessage partial void) | 10 helpers | ~40 |

The 800 LoC Round-1 ceiling from `automotive-coding-standards-file-size.md`
was exceeded by **+27%**.

## What this MINOR does

### Refactor — AppShellViewModel split into 4 partial-class files

The god-class is split into 4 partial files in the same namespace. Main
file keeps ctor, [ObservableProperty] fields, DI-injected services,
partial void change handlers, and Dispose.

**Files created**:

| File | Flow | LoC | Methods |
|---|---|---|---|
| `AppShellViewModel/ChannelFlow.cs` | A | ~345 | EnumerateChannels + ConnectAsync + DisconnectAsync + OnReadLoopError + OnIsFdChanged + OnSelectedChannelChanged + LogReadLoopError + 3 CanXxx |
| `AppShellViewModel/ViewSwitchFlow.cs` | B | ~301 | 9 Show* + OpenMultiFrame + ShowTraceViewer + GetOrCreateDbcView |
| `AppShellViewModel/SessionFlow.cs` | C | ~186 | OpenDbc + OpenSessionAsync + SaveSessionAsync + OpenRecentSessionAsync + ClearRecentSessions + RefreshRecentEntries |
| `AppShellViewModel/LogFlow.cs` | D | ~50 | 9 LoggerMessage partial void (LogReadLoopError stays in ChannelFlow with its caller) |

**Main file** `AppShellViewModel.cs`: **1019 → 352 LoC (-667 LoC, -65.5%)** — well below 800 LoC threshold.

### Architecture invariants preserved

- **Public API unchanged**: all [RelayCommand] attributes stay with their methods. XAML bindings are not affected.
- **partial-class visibility**: private methods visible across partial files; cross-flow calls stay as plain invocations.
- **State and DI**: all state fields and DI-injected services stay in the main file. Partial files consume them transparently.

### Helper distribution decisions

- `GetOrCreateDbcView` (Flow B helper, 1-line) — stays with `ShowDbc` in ViewSwitchFlow.cs. Only called by `ShowDbc`.
- `LogReadLoopError` (10th log helper, Flow D) — **stays with its caller** `OnReadLoopError` in ChannelFlow.cs (Flow A). Co-location principle: helpers live with their callers, even if that splits the log helper group. The other 9 LogFlow helpers stay in LogFlow.cs.

## What this MINOR does NOT do

- **No behavioral change.** Zero test changes, zero production-fix changes.
- **No API surface change.** No new public methods, no removed methods, no renamed members.
- **No state ownership change.** All mutable state still lives on the singleton partial class.
- **No new dependencies.**

## Verification

- **`dotnet build`** (Debug, warn-as-error): 0 errors. Pre-existing `CS8602` nullable warning in `DbcService.cs:157` (unrelated).
- **`dotnet test --filter AppShellViewModel`**: 47/47 PASS, 0 fail, 3 SKIP (hardware-dependent).
- **`dotnet test --filter ViewSwitcher`**: 6/6 PASS, 0 fail, 0 skip.
- **Combined AppShellViewModel + ViewSwitcher**: **53/53 PASS, 0 fail, 3 SKIP**.
- **Pre-existing parallel-runner flakes** (per v3.16.9.0 release notes): unchanged. Out of scope.
- **Main file LoC reduction**: 1019 → 352 LoC (-667 LoC, **-65.5%**).

## Risk notes

- **R1 (mitigated)**: Missing `using` directives — per W3 lesson `partial-class-using-directives-are-file-scoped-not-class-scoped` (now CONFIRMED at 5+ confirmations). Hit twice during W4 (Task 2 missing `using CommunityToolkit.Mvvm.Input;`, Task 4 missing `using PeakCan.Host.Infrastructure.Channel;`). Always pre-scan partial file bodies for type references and add corresponding usings before first build.
- **R2 (mitigated)**: Line-range deletion script assertion off-by-1 (Task 3 expected 861 LoC, actual was 862 due to inserted Flow C marker line). Adjusted assertions in subsequent tasks.
- **R3 (YAGNI applied)**: `GetOrCreateDbcView` is a 1-line helper. Could be inlined into `ShowDbc`. **Rejected** — keeping the helper as a separate method is the established pattern in the codebase (other Show* methods use `ViewSwitcher.Show` which is also extracted; consistency matters).

## Files in this ship

### Source code changes (4 commits)

```
978ac6c refactor(asvm): extract Flow A (Channel lifecycle) to partial class
e9272fa refactor(asvm): extract Flow B (View navigation) to partial class
43727ab refactor(asvm): extract Flow C (session open/save) to partial class
948b0b4 refactor(asvm): extract Flow D (Log helpers) to partial class
```

### Scripts (4 commits — included in task commits)

```
scripts/w4_task1_delete_logflow.py
scripts/w4_task2_delete_sessionflow.py
scripts/w4_task3_delete_viewflow.py
scripts/w4_task4_delete_channelflow.py
```

### Docs (2 commits)

```
932d508 docs(spec): AppShellViewModel god-class refactor design
<TBD>    docs(plan): AppShellViewModel god-class refactor — 6-task execution plan
```

## For the next session

- W4 plan is fully executed (6 of 6 tasks done — including this ship).
- 5 confirmations of `partial-class-using-directives-are-file-scoped-not-class-scoped` — lesson now CONFIRMED for any future partial-class work.
- `feature/w4-app-shell-god-class` branch is the W4 MINOR branch — consider whether to merge to `main` or keep as a long-lived feature branch.
- 2 NEW 1-of-1 lessons captured during W4: helper co-location principle (LogReadLoopError stays with caller), line-count assertion off-by-1 (marker line shifts expected count by 1).
- Next MINOR candidates: other god-class ViewModels (SignalViewModel 601 LoC, SendViewModel 533 LoC, MultiFrameSendViewModel 513 LoC — all approaching 800 LoC threshold).