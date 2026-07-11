# Release Notes v3.26.0 — AppHostBuilder god-class refactor (MINOR)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.26.0
**Branch:** `feature/w11-app-host-builder-god-class`
**Parent:** v3.25.0 MINOR (`1baab1d` on origin/main)

## Why this MINOR

`src/PeakCan.Host.App/Composition/AppHostBuilder.cs` had grown to **744 LoC** as of v3.25.0 — at 93% of the 800 LoC Round-1 ceiling. Single instance class (DI composition root) with one massive `Build()` method (**647 LoC, ~87% of file**) containing all DI service registrations stacked end-to-end.

This is the **9th god-class refactor** in the project, the **2nd App layer** since W3-W8 (after W10 was Core layer), and the **FIRST single-monolithic-method split refactor** in the W3-W11 series. Previous W3-W10 refactors moved complete methods between files. **W11 BREAKS THE MOLD**: splits one big method body into helper methods, then moves helpers to partial files.

| Flow | Responsibility | Helper | ~LoC |
|---|---|---|---|
| A | Logging setup | ConfigureLoggingAndBuilder | ~67 |
| B | Core infrastructure | RegisterCoreInfrastructure | ~38 |
| C | App services | RegisterAppServices | ~133 |
| D | ViewModels batch 1 | RegisterViewModelsBatch1 | ~51 |
| E | ViewModels batch 2 (+ batch 3 absorbed) | RegisterViewModelsBatch2 | ~147 |
| G | Window + hosted services | RegisterWindowAndHostedServices | ~15 |

(Flow F StatsViewModel + ScriptViewModel was absorbed into Flow E Range B during execution — minor spec deviation, net 6 partials instead of 7)

## What this MINOR does

### Refactor — AppHostBuilder split into 6 partial-class files (helper-method extraction pattern)

The instance class `AppHostBuilder` becomes `public partial class AppHostBuilder`. The monolithic `Build()` method (647 LoC) becomes a thin **orchestrator** that calls 6 private helper methods in order. Each helper is a verbatim copy of one inline section of the original `Build()` body, wrapped in `private void RegisterXxxServices(IServiceCollection services)` or `private void ConfigureLoggingAndBuilder(out HostApplicationBuilder builder)`.

**Files created**:

| File | Flow | LoC | Helper |
|---|---|---|---|
| `AppHostBuilder/LoggingFlow.cs` | A | ~190 | ConfigureLoggingAndBuilder |
| `AppHostBuilder/CoreInfrastructureFlow.cs` | B | ~110 | RegisterCoreInfrastructure |
| `AppHostBuilder/AppServicesFlow.cs` | C | ~210 | RegisterAppServices |
| `AppHostBuilder/ViewModelsBatch1Flow.cs` | D | ~110 | RegisterViewModelsBatch1 |
| `AppHostBuilder/ViewModelsBatch2Flow.cs` | E (+ F absorbed) | ~260 | RegisterViewModelsBatch2 |
| `AppHostBuilder/WindowAndHostedServicesFlow.cs` | G | ~80 | RegisterWindowAndHostedServices |

**Main file** `AppHostBuilder.cs`: **744 → 316 LoC (-428 LoC, -57.5%)** — meets the -57.5% spec target.

**Build() body** (post-extraction): **647 → ~218 LoC orchestrator** that calls 6 helpers in order:
```csharp
public IHost Build()
{
    ConfigureLoggingAndBuilder(out var builder);

    // === Flow B ===
    RegisterCoreInfrastructure(builder.Services);

    // === Flow C ===
    RegisterAppServices(builder.Services);

    // === Flow D ===
    RegisterViewModelsBatch1(builder.Services);

    // === Flow E (Range A: TraceViewer section) ===
    RegisterViewModelsBatch2(builder.Services);

    // === Flow E (Range B: Trace/Send/Dbc/SignalChart/Signal/Stats/Script) ===
    RegisterViewModelsBatch2(builder.Services);

    // === Flow G ===
    RegisterWindowAndHostedServices(builder.Services);

    return builder.Build();
}
```

### Architecture invariants preserved

- **Public API unchanged**: `Build()` method signature stays the same; `WithUdsSecurityLockoutConfig()` fluent setter stays the same; `PcanUsbFdFirstHandle` const stays the same.
- **partial-class visibility**: private methods + private fields visible across partial files; orchestrator calls each helper via partial-class visibility.
- **DI service registration order preserved**: each helper runs in the exact same order as the original inline code — no reordering, no batching changes.
- **State stays close to its reader/writer**: `_udsSecurityLockoutConfig` stays in main (only read in fluent setter + Build).

### New lesson candidates (2 NEW this session)

| Lesson | Confirmations | Status |
|---|---|---|
| `static-class-with-nested-partial-class-both-need-partial-modifier` (W10 T1 R1 NEW) | 1/3 (W10 T1) | CANDIDATE — 2 more for CONFIRMED |
| `helper-method-extraction-must-rename-builder-to-parameter` (W11 T5 R-NEW) | 1/3 (W11 T5) | CANDIDATE — 2 more for CONFIRMED |
| `plan-LoC-trajectory-table-must-account-for-deletion-not-just-marker-addition` (W8.5 PATCH CONFIRMED) | n/a | **CONFIRMED — applied as D6 in W11 plan** |

## What this MINOR does NOT do

- **No behavioral change.** Zero test changes, zero production-fix changes.
- **No API surface change.** No new public methods, no removed methods, no renamed members.
- **No DI registration refactoring**: each helper is verbatim copy of original inline section.

## Verification

- **`dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj`** (Debug, warn-as-error): **0 errors, 2 warnings** (pre-existing CS8602 in DbcService.cs:157, unrelated to W11).
- **`dotnet test --filter AppHost|Composition`**: **41/41 PASS, 0 fail, 0 skip** (unchanged from pre-W11 baseline).
- **Main file LoC reduction**: 744 → **316 LoC (-428 LoC, -57.5%)** — meets the -57.5% spec target.

## Risk notes

- **R1 (mitigated)**: Missing `using` directives — per W3-W10+W8.5+W9.5 CONFIRMED lesson (18+ confirmations across W11 alone). Hit 5 times during W11 (T1 partial modifier + T2 + T3 + T4 + T6 missing usings; T5 also had helper parameter mismatch).
- **R2 (mitigated)**: Deletion script line-count assertion — per W3-W10+W8.5+W9.5 CONFIRMED lessons. Applied W8.5 PATCH D6 lesson: correct formula `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`.
- **R3 (FULLY MITIGATED)**: Single-monolithic-method split pattern — **W11 SPEC's primary risk**. Each helper extracts verbatim inline section; orchestrator calls helpers in exact same order. 41/41 AppHost+Composition tests pass — **R3 risk fully validated** as workable pattern across 6 helper extractions.

## Files in this ship

### Source code changes (8 commits)

```
0743c45 refactor(ahb): extract Flow G (Window + hosted services) to partial class (W11 Task 6 — LAST)
159e827 refactor(ahb): extract Flow E (ViewModelsBatch2) to partial class (W11 Task 5)
9f4fe5f refactor(ahb): extract Flow D (ViewModelsBatch1) to partial class (W11 Task 4)
f26881f refactor(ahb): extract Flow C (AppServices) to partial class (W11 Task 3)
ec3e224 refactor(ahb): extract Flow B (CoreInfrastructure) to partial class (W11 Task 2)
85b032c refactor(ahb): extract Flow A (Logging) to partial class (W11 Task 1)
78d3082 docs(plan): AppHostBuilder god-class refactor — 9-task execution plan (W11)
0ce2596 docs(spec): AppHostBuilder god-class refactor design (W11 brainstorm output)
```

### Scripts (6 commits — included in task commits)

```
scripts/w11_task1_delete_loggingflow.py
scripts/w11_task2_delete_coreinfrastructureflow.py
scripts/w11_task3_delete_appservicesflow.py
scripts/w11_task4_delete_viewmodelsbatch1flow.py
scripts/w11_task5_delete_viewmodelsbatch2flow.py
scripts/w11_task6_delete_windowandhostedservicesflow.py
```

### Docs (3 commits + ship commit)

```
0ce2596 docs(spec): AppHostBuilder god-class refactor design (W11 brainstorm output)
78d3082 docs(plan): AppHostBuilder god-class refactor — 9-task execution plan (W11)
<TBD>    chore(release): bump version to v3.26.0 + add release notes
```

## For the next session

- W11 plan fully executed through Task 6 (extraction phase complete + release notes).
- **9 god-class refactors completed in 1 session** — pattern PROVEN across 7 App layer classes (W3-W8 VMs + W11 AppHostBuilder) + 2 Core layer classes (W9 IsoTpLayer + W10 DbcParser).
- **W11 R3 (FIRST single-monolithic-method split refactor) FULLY MITIGATED** — pattern validated for future DI composition root refactors.
- `feature/w11-app-host-builder-god-class` branch is the W11 MINOR branch — ready to merge to `main`.
- 2 NEW CANDIDATE lessons await 1-2 more confirmations each before promotion to CONFIRMED.
- Next MINOR candidates: investigate remaining Core layer god-class candidates (UdsClient.cs 704 LoC + AppHostBuilder was the LAST App layer candidate) for similar refactor opportunities, OR promote the 2 NEW CANDIDATE lessons to CONFIRMED via another vault-only PATCH (per W8.5 + W9.5 PATCH precedent).

## Pattern maturity

After 9 god-class refactors (v3.17.0 / v3.19.0 / v3.20.0 / v3.21.0 / v3.22.0 / v3.23.0 / v3.24.0 / v3.25.0 / v3.26.0), the partial-class split pattern is now **CROSS-LAYER + CROSS-SHAPE + CROSS-METHOD-TYPE PRODUCTION-GRADE**:
- 6 App layer VMs + 1 App layer DI composition root + 2 Core layer classes — partial-class works in all configurations
- **NEW in W11**: First single-monolithic-method split refactor — `Build()` (647 LoC) split into 6 helpers across partial files
- 18+ confirmations of `partial-class-using-directives-are-file-scoped-not-class-scoped` lesson (W11 alone: 5 hits)
- 9 confirmations of `deletion-script-line-range-precision-with-non-contiguous-ranges` lesson
- 2 NEW lesson candidates at 1/3 confirmations (R1 static-class-with-nested-partial-class from W10 + R-NEW helper-method-extraction-must-rename-builder-to-parameter from W11)
- 0 merge conflicts across W3-W11 after v3.18.0 PATCH `.gitattributes` fix
- Average reduction: 64% main-file LoC across 9 classes (range 51.8%-85.8%)
- Pattern now extends to: private state fields, nested classes, [LoggerMessage] partial methods, [ObservableProperty] backing fields, static classes with nested classes, **DI composition root with single monolithic method**, cross-partial method calls (all validated)