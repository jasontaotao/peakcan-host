# W11 AppHostBuilder god-class refactor — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` from 744 LoC to ~150 LoC by extracting 7 logical flow groups into partial-class files. Pure mechanical refactor — zero behavioral change, zero test changes, zero API surface change.

**Architecture:** Same W3-W10 partial-class split pattern, applied to a **DI composition root** (instance class with one large `Build()` method). Main file keeps state fields, fluent setter methods, constants, and the `Build()` orchestrator (thin: calls 7 helper methods in order). Each partial file owns one logical service group as a **private helper method** that the orchestrator calls via partial-class visibility.

**Tech Stack:** C# .NET 10, Microsoft.Extensions.Hosting / DependencyInjection / Configuration. App layer (Composition root). Git with LF line endings.

## Global Constraints

- **Public API unchanged.** No method signatures, properties, or fluent setter behaviors move.
- **partial-class visibility.** All private methods + private fields visible across partial files; cross-partial calls stay as plain invocations.
- **Test coverage unchanged.** No tests added, removed, or modified.
- **Line-ending normalized to LF.** Per v3.18.0 PATCH `.gitattributes` enforcement.
- **No behavioral change.** Every service registration order, every method body, every xmldoc, every comment, and every whitespace moves verbatim.
- **No version bump until Task 8.** Tasks 1-7 keep `src/Directory.Build.props` at v3.25.0. Task 8 bumps to v3.26.0.
- **Branch**: `feature/w11-app-host-builder-god-class` (already created from `main` @ `1baab1d` v3.25.0).
- **Spec**: `docs/superpowers/specs/2026-07-11-app-host-builder-god-class-refactor.md`.

---

## File Structure

```
src/PeakCan.Host.App/Composition/AppHostBuilder.cs                       # main file, ~150 LoC after Task 7
src/PeakCan.Host.App/Composition/AppHostBuilder/                          # NEW directory
  LoggingFlow.cs                                                          # Task 1 — Logging setup (~70 LoC)
  CoreInfrastructureFlow.cs                                               # Task 2 — Core infrastructure (~55 LoC)
  AppServicesFlow.cs                                                      # Task 3 — App services (~100 LoC)
  ViewModelsBatch1Flow.cs                                                 # Task 4 — ViewModels batch 1 (~100 LoC)
  ViewModelsBatch2Flow.cs                                                 # Task 5 — ViewModels batch 2 (~100 LoC)
  ViewModelsBatch3Flow.cs                                                 # Task 6 — ViewModels batch 3 (~100 LoC)
  WindowAndHostedServicesFlow.cs                                          # Task 7 — Window + hosted services (~50 LoC)
docs/superpowers/plans/2026-07-11-app-host-builder-god-class-refactor.md   # this file
docs/release-notes-v3.26.0.md                                              # NEW in Task 8
```

---

## Cumulative method-line ranges (anchors for all 7 extraction tasks)

Pre-Task-1 file: 744 LoC (commit `0ce2596`). All deletion scripts delete by line-range slicing per the W3-W7 proven pattern.

**W5 + W8.5 D7 lessons applied**: Each task inserts a "Flow X marker" comment line into the main file. Subsequent tasks must account for the +1 marker line per prior task. **CORRECT formula** (per W8.5 CONFIRMED `plan-LoC-trajectory-table-must-account-for-deletion-not-just-marker-addition` lesson):

```
LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker
```

NOT `LoC_base + n markers` (the wrong formula used in W8 plan).

**Initial estimates** (each task's deletion size is approximate, will be verified by reading the file before each task):

| Task | Estimated deletion | Cumulative LoC after |
|---|---|---|
| Pre-Task-1 | (base) | 744 |
| Task 1 (Logging) | ~70 | 675 |
| Task 2 (CoreInfra) | ~55 | 621 |
| Task 3 (AppServices) | ~100 | 522 |
| Task 4 (VMsBatch1) | ~100 | 423 |
| Task 5 (VMsBatch2) | ~100 | 324 |
| Task 6 (VMsBatch3) | ~100 | 225 |
| Task 7 (Window+Hosted) | ~50 | 176 |
| Task 8 (version bump) | 0 | 176 |

**Note**: The exact deletion line ranges need verification by reading the file before each task. Actual deletion size may differ from estimate by ±20 LoC.

---

### Task 1: Extract Flow A → `LoggingFlow.cs` (smallest first)

**Files:**
- Create: `src/PeakCan.Host.App/Composition/AppHostBuilder/LoggingFlow.cs`
- Modify: `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` (delete Logging setup section + replace with `ConfigureLoggingAndBuilder(out IHostBuilder builder)` helper call)
- Create: `scripts/w11_task1_delete_loggingflow.py`

**Helper signature**: `private void ConfigureLoggingAndBuilder(out IHostBuilder builder)` — out param since the IHostBuilder is needed by subsequent flow helpers.

**Pre-conditions**:
- Branch `feature/w11-app-host-builder-god-class` checked out
- `git status` clean
- `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` is 744 LoC

- [ ] **Step 1: Read main file lines 95-170 to capture exact verbatim content of Logging setup section**

Use Read tool with offset 95, limit 75.

- [ ] **Step 2: Create the partial file `LoggingFlow.cs`**

Header: `public partial class AppHostBuilder { ... }`. Content: verbatim Logging setup section (IHostBuilder.CreateApplicationBuilder + Serilog + appsettings + env vars + cmd line + hardcoded smoke-test logs + Serilog registration). Wrap in `private void ConfigureLoggingAndBuilder(out IHostBuilder builder)` helper.

Required usings: `System.IO`, `Microsoft.Extensions.Configuration`, `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Logging`, `Serilog`.

- [ ] **Step 3: Write the deletion script + replace Build() body section**

Pattern: similar to W9/W10 deletion scripts, but ALSO replace the Build() body section with a call to `ConfigureLoggingAndBuilder(out var builder)` + the rest of the Build() body verbatim. Insert `builder = ConfigureLoggingAndBuilder();` call at top of Build() body.

- [ ] **Step 4: Run + build + test**

```bash
python scripts/w11_task1_delete_loggingflow.py
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~AppHost|FullyQualifiedName~Composition"
```

Expected: 0 errors; tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/Composition/AppHostBuilder.cs src/PeakCan.Host.App/Composition/AppHostBuilder/LoggingFlow.cs scripts/w11_task1_delete_loggingflow.py
git commit -m "refactor(ahb): extract Flow A (Logging) to partial class (W11 Task 1)"
```

---

### Task 2: Extract Flow B → `CoreInfrastructureFlow.cs`

**Files:**
- Create: `src/PeakCan.Host.App/Composition/AppHostBuilder/CoreInfrastructureFlow.cs`
- Modify: `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` (delete Core infrastructure section + replace with `RegisterCoreInfrastructure(builder.Services)` helper call)
- Create: `scripts/w11_task2_delete_coreinfrastructureflow.py`

**Helper signature**: `private void RegisterCoreInfrastructure(IServiceCollection services)`

**Pre-conditions**:
- Task 1 committed. Main file at ~675 LoC.

- [ ] **Step 1**: Read main file around lines 165-220 (after Task 1, ranges shifted by +1 marker + builder helper call).

- [ ] **Step 2**: Create `CoreInfrastructureFlow.cs`. Required usings: `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Logging`.

- [ ] **Step 3**: Write the deletion script (single range covering Core infrastructure section, post-Task-1 expected LoC = 675).

- [ ] **Step 4**: Run + build + test. Assert `original_count == 675`.

- [ ] **Step 5**: Commit with message `refactor(ahb): extract Flow B (CoreInfrastructure) to partial class (W11 Task 2)`.

---

### Task 3: Extract Flow C → `AppServicesFlow.cs` (largest service group)

**Files:**
- Create: `src/PeakCan.Host.App/Composition/AppHostBuilder/AppServicesFlow.cs`
- Modify: `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` (delete App services section + replace with `RegisterAppServices(builder.Services)` helper call)
- Create: `scripts/w11_task3_delete_appservicesflow.py`

**Helper signature**: `private void RegisterAppServices(IServiceCollection services)`

**Pre-conditions**:
- Task 2 committed. Main file at ~621 LoC.

- [ ] **Step 1**: Read main file around lines 210-320 (after Task 2, ranges shifted by +2 markers + helper calls).

- [ ] **Step 2**: Create `AppServicesFlow.cs`. Required usings: `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Logging`.

- [ ] **Step 3**: Write the deletion script (single range, post-Task-2 expected LoC = 621).

- [ ] **Step 4**: Run + build + test. **Verify App services register correctly** — TraceService + RecordingService + StatisticsService + RateLimitedSendService + ScriptingService all instantiate.

- [ ] **Step 5**: Commit with message `refactor(ahb): extract Flow C (AppServices) to partial class (W11 Task 3)`.

---

### Task 4: Extract Flow D → `ViewModelsBatch1Flow.cs`

**Files:**
- Create: `src/PeakCan.Host.App/Composition/AppHostBuilder/ViewModelsBatch1Flow.cs`
- Modify: `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` (delete ViewModels batch 1 section + replace with `RegisterViewModelsBatch1(builder.Services)` helper call)
- Create: `scripts/w11_task4_delete_viewmodelsbatch1flow.py`

**Helper signature**: `private void RegisterViewModelsBatch1(IServiceCollection services)`

**Pre-conditions**:
- Task 3 committed. Main file at ~522 LoC.

- [ ] **Step 1**: Read main file around lines 320-430 (after Task 3).

- [ ] **Step 2**: Create `ViewModelsBatch1Flow.cs`. Required usings: `Microsoft.Extensions.DependencyInjection`, plus all App + ViewModel namespaces.

- [ ] **Step 3**: Write the deletion script (single range, post-Task-3 expected LoC = 522).

- [ ] **Step 4**: Run + build + test.

- [ ] **Step 5**: Commit with message `refactor(ahb): extract Flow D (ViewModelsBatch1) to partial class (W11 Task 4)`.

---

### Task 5: Extract Flow E → `ViewModelsBatch2Flow.cs`

**Files:**
- Create: `src/PeakCan.Host.App/Composition/AppHostBuilder/ViewModelsBatch2Flow.cs`
- Modify: `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` (delete ViewModels batch 2 section + replace with `RegisterViewModelsBatch2(builder.Services)` helper call)
- Create: `scripts/w11_task5_delete_viewmodelsbatch2flow.py`

**Helper signature**: `private void RegisterViewModelsBatch2(IServiceCollection services)`

**Pre-conditions**:
- Task 4 committed. Main file at ~423 LoC.

- [ ] **Step 1**: Read main file around lines 430-540 (after Task 4).

- [ ] **Step 2**: Create `ViewModelsBatch2Flow.cs`. Required usings: `Microsoft.Extensions.DependencyInjection`.

- [ ] **Step 3**: Write the deletion script (single range, post-Task-4 expected LoC = 423).

- [ ] **Step 4**: Run + build + test.

- [ ] **Step 5**: Commit with message `refactor(ahb): extract Flow E (ViewModelsBatch2) to partial class (W11 Task 5)`.

---

### Task 6: Extract Flow F → `ViewModelsBatch3Flow.cs`

**Files:**
- Create: `src/PeakCan.Host.App/Composition/AppHostBuilder/ViewModelsBatch3Flow.cs`
- Modify: `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` (delete ViewModels batch 3 section + replace with `RegisterViewModelsBatch3(builder.Services)` helper call)
- Create: `scripts/w11_task6_delete_viewmodelsbatch3flow.py`

**Helper signature**: `private void RegisterViewModelsBatch3(IServiceCollection services)`

**Pre-conditions**:
- Task 5 committed. Main file at ~324 LoC.

- [ ] **Step 1**: Read main file around lines 540-640 (after Task 5).

- [ ] **Step 2**: Create `ViewModelsBatch3Flow.cs`. Required usings: `Microsoft.Extensions.DependencyInjection`.

- [ ] **Step 3**: Write the deletion script (single range, post-Task-5 expected LoC = 324).

- [ ] **Step 4**: Run + build + test.

- [ ] **Step 5**: Commit with message `refactor(ahb): extract Flow F (ViewModelsBatch3) to partial class (W11 Task 6)`.

---

### Task 7: Extract Flow G → `WindowAndHostedServicesFlow.cs` (LAST extraction)

**Files:**
- Create: `src/PeakCan.Host.App/Composition/AppHostBuilder/WindowAndHostedServicesFlow.cs`
- Modify: `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` (delete Window + hosted services section + replace with `RegisterWindowAndHostedServices(builder.Services)` helper call, keeping the `return builder.Build()` line in Build)
- Create: `scripts/w11_task7_delete_windowandhostedservicesflow.py`

**Helper signature**: `private void RegisterWindowAndHostedServices(IServiceCollection services)`

**Pre-conditions**:
- Task 6 committed. Main file at ~225 LoC.

- [ ] **Step 1**: Read main file around lines 640-744 (after Task 6).

- [ ] **Step 2**: Create `WindowAndHostedServicesFlow.cs`. Required usings: `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Hosting`.

- [ ] **Step 3**: Write the deletion script (single range covering Window + hosted services section but keeping `return builder.Build();` line in main Build body, post-Task-6 expected LoC = 225).

- [ ] **Step 4**: Run + build + test. **Verify Build returns IHost correctly** — last chance to catch any DI registration order regression.

- [ ] **Step 5**: Commit with message `refactor(ahb): extract Flow G (WindowAndHostedServices) to partial class (W11 Task 7 — LAST)`.

**Final main file size after Task 7**: ~225 - 50 + 1 = **~176 LoC target** (exceeds -150 LoC spec target but in acceptable range).

---

### Task 8: Bump version v3.25.0 → v3.26.0 + write release notes

**Files:**
- Modify: `src/Directory.Build.props` (3.25.0 → 3.26.0 for Version + AssemblyVersion + FileVersion + InformationalVersion)
- Create: `docs/release-notes-v3.26.0.md` (modeled after `docs/release-notes-v3.25.0.md`)

**Pre-conditions**:
- Task 7 committed. Main file at ~176 LoC (target hit).

- [ ] **Step 1: Update `src/Directory.Build.props`**

Bump all 4 version fields.

- [ ] **Step 2: Write release notes**

Title: `# Release Notes v3.26.0 — AppHostBuilder god-class refactor (MINOR)`. Mirror W10 release notes structure: Why this MINOR, What this MINOR does (split into 7 helper methods with flow tables), What this MINOR does NOT do, Verification (dotnet build, dotnet test, LoC reduction), Risk notes (R1-R3), Files in this ship (8 commits), For the next session.

- [ ] **Step 3: Commit**

```bash
git add src/Directory.Build.props docs/release-notes-v3.26.0.md
git commit -m "chore(release): bump version to v3.26.0 + add release notes

W11 ships: 7 god-class extractions (Flows A, B, C, D, E, F, G).

Main file: 744 -> ~176 LoC (-568 LoC, -76.3%).
7 partial-class files in AppHostBuilder/ directory.
9th god-class refactor, FIRST single-monolithic-method split refactor.

Tests: AppHost pass; build clean."
```

---

### Task 9: Tier-3 ship (annotated tag + push + GH release)

Same 5-step Tier-3 script used for W3-W10.

- [ ] **Step 1**: Tag annotated at the version-bump commit
- [ ] **Step 2**: Push branch + tag to origin
- [ ] **Step 3**: Create GH release
- [ ] **Step 4**: Verify GH release
- [ ] **Step 5**: Final verification

---

## Verification summary

After Task 9 completes:

- `dotnet build` (Debug, warn-as-error): 0 errors
- `dotnet test --filter AppHost|Composition`: all pre-existing tests pass without modification
- `dotnet test` (full suite): catch any cross-VM DI regressions
- Main file `AppHostBuilder.cs`: 744 → ~176 LoC (-568 LoC, -76.3%)
- 7 partial-class files created in `AppHostBuilder/` directory
- Branch `feature/w11-app-host-builder-god-class` at version-bump commit
- Tag `v3.26.0` annotated and pushed
- GH release published

## Out of scope (explicit YAGNI)

- **No facade pattern**: W3-W10+W8.5+W9.5 confirmed direct partial-class visibility is sufficient.
- **No sub-class creation**: AppHostBuilder stays a single class.
- **No test changes**: All existing tests stay unmodified.
- **No public/internal API surface change**: No new public methods, no removed methods, no renamed members.
- **No DI registration refactoring**: Each helper is verbatim copy of original inline section. Service registration order is preserved.

## Decision log

- **D1**: 7 partials with descriptive names (Logging/CoreInfrastructure/AppServices/ViewModelsBatch1/2/3/WindowAndHostedServices).
- **D2**: Same W3-W10 pattern (no facade, no sub-classes).
- **D3**: Branch name `feature/w11-app-host-builder-god-class`.
- **D4**: Order A (70) → B (55) → G (50) → C (100) → D (100) → E (100) → F (100). Extract in dependency order.
- **D5**: Helper method extraction (not verbatim section move) — each service registration section becomes a `private void RegisterXxxServices(IServiceCollection services)` helper.
- **D6**: Plan LoC-trajectory-table formula explicitly applied per W8.5 PATCH CONFIRMED lesson: `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`.

## Closing milestone context

This is the **9th god-class refactor** in the project. AppHostBuilder is the **9th distinct class** (6 App layer VMs + 2 Core layer classes + this 1 App layer composition root). This refactor is **structurally unique**: it's the **first W3-W11 refactor that splits a single monolithic method body** into multiple helper methods across partial files (vs the previous pattern of moving complete methods).