# Release Notes v3.21.0 — SendViewModel god-class refactor (MINOR)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.21.0
**Branch:** `feature/w6-send-view-model-god-class`
**Parent:** v3.20.0 MINOR (`833cd85` on origin/main)

## Why this MINOR

`src/PeakCan.Host.App/ViewModels/SendViewModel.cs` had grown to **533 LoC** as of v3.20.0 — at 67% of the 800 LoC Round-1 ceiling from `automotive-coding-standards-file-size.md`. Single sealed partial class (which also implements `IHostedService, IDisposable`) owned 4 distinct responsibilities stacked end-to-end:

| Flow | Responsibility | Methods | ~LoC |
|---|---|---|---|
| A | FrameSend (one-shot send + validation + 5 log helpers) | 2 + 5 helpers | ~200 |
| B | Cyclic (scheduled send loop) | 2 | ~30 |
| C | Library (frame persistence + multi-frame opener + 2 log helpers) | 6 | ~140 |
| D | Lifecycle (Dispose) | 1 | ~10 |

This is the **4th god-class refactor** in the project (after TraceViewerViewModel v3.17.0, AppShellViewModel v3.19.0, SignalViewModel v3.20.0). The pattern is now PROVEN across 4 distinct VMs.

## What this MINOR does

### Refactor — SendViewModel split into 4 partial-class files

The god-class is split into 4 partial files in the same namespace. Main file keeps constructor, [ObservableProperty] fields, DI-injected services, public properties.

**Files created**:

| File | Flow | LoC | Methods |
|---|---|---|---|
| `SendViewModel/FrameSendFlow.cs` | A | ~169 | SendAsync + OnRateLimitRejectedCountChanged + 5 log helpers |
| `SendViewModel/LifecycleFlow.cs` | D | ~20 | Dispose |
| `SendViewModel/CyclicFlow.cs` | B | ~60 | StartCyclic + StopCyclic |
| `SendViewModel/LibraryFlow.cs` | C | ~210 | RefreshLibrary + SaveCurrentToLibrary + LoadFromLibrary + DeleteFromLibrary + OpenMultiFrameSend + 2 log helpers + _openMultiFrameWindow field |

**Main file** `SendViewModel.cs`: **533 → 257 LoC (-276 LoC, -51.8%)** — well below 800 LoC threshold.

### Architecture invariants preserved

- **Public API unchanged**: all [RelayCommand] attributes stay with their methods. XAML bindings are not affected.
- **partial-class visibility**: private methods visible across partial files; cross-flow calls stay as plain invocations.
- **State and DI**: all DI fields, state properties, and helpers stay in the main file.

### Architectural decision — non-contiguous deletion ranges

Unlike W3/W4 where methods were physically grouped by flow, SendViewModel's methods are **interleaved** (e.g., OnRateLimitRejectedCountChanged at line 119, Dispose at line 211, SendAsync at line 213). Each W6 task uses **multiple non-contiguous deletion ranges** with bottom-up slicing.

### Helper co-location decisions

- 7 LoggerMessage helpers stay with their callers (5 FrameSend + 2 Library) per W5 helper-co-location principle.
- `ParseHex` + `BuildFlags` + `_svc` field stay in main (used by both SendAsync + StartCyclic — could be moved in future cleanup).
- `IHostedService.StartAsync/StopAsync` no-op implementations stay in main (entry-point contract).

## What this MINOR does NOT do

- **No behavioral change.** Zero test changes, zero production-fix changes.
- **No API surface change.** No new public methods, no removed methods, no renamed members.

## Verification

- **`dotnet build`** (Debug, warn-as-error): 0 errors. Pre-existing `CS8602` nullable warning in `DbcService.cs:157` (unrelated).
- **`dotnet test --filter SendViewModel`**: **67/67 PASS, 0 fail, 0 skip**.
- **Pre-existing parallel-runner flakes** (per v3.16.9.0 release notes): unchanged. Out of scope.
- **Main file LoC reduction**: 533 → 257 LoC (-276 LoC, **-51.8%**). Better than 250 LoC target.

## Risk notes

- **R1 (mitigated)**: Missing `using` directives — per W3/W4/W5 lesson (CONFIRMED at 7+ confirmations). Hit twice during W6 (Task 3 missing `using PeakCan.Host.App.Services;` for `SendFrameLibrary.SavedFrame`, Task 4 no issues thanks to pre-scan).
- **R2 (mitigated)**: Deletion script line-count assertion off-by-1 — per W4/W5 lesson (CONFIRMED at 3+ confirmations). Hit once per W6 task.

## Files in this ship

### Source code changes (4 commits)

```
94119a4 refactor(svm): extract Flow A (FrameSend) to partial class
7f7f813 refactor(svm): extract Flow C (Library) to partial class
ebc4462 refactor(svm): extract Flow B (Cyclic) to partial class
ae14eaf refactor(svm): extract Flow D (Lifecycle) to partial class
```

### Scripts (4 commits — included in task commits)

```
scripts/w6_task1_delete_lifecycleflow.py
scripts/w6_task2_delete_cyclicflow.py
scripts/w6_task3_delete_libraryflow.py
scripts/w6_task4_delete_framesendflow.py
```

### Docs (2 commits)

```
3c60ad1 docs(spec): SendViewModel god-class refactor design (W6 brainstorm output)
<TBD>    docs(plan): SendViewModel god-class refactor — 6-task execution plan
```

## For the next session

- W6 plan is fully executed (6 of 6 tasks done — including this ship).
- **4 god-class refactors completed in 1 session** — pattern PROVEN across 4 distinct VMs (TraceViewerViewModel + AppShellViewModel + SignalViewModel + SendViewModel, all achieved ~52-66% reduction).
- `feature/w6-send-view-model-god-class` branch is the W6 MINOR branch — consider whether to merge to `main` or keep as a long-lived feature branch.
- Next MINOR candidates: MultiFrameSendViewModel.cs (513 LoC), TraceChartViewModel.cs (435 LoC) — both approaching 800 LoC threshold.