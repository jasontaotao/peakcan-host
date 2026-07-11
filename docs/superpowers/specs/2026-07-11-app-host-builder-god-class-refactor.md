# W11 Spec — AppHostBuilder god-class refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` from 744 LoC to ~150 LoC by extracting 7 logical flow groups into partial-class files. Pure mechanical refactor — zero behavioral change, zero test changes, zero API surface change.

**Architecture:** Same W3-W10 partial-class split pattern, applied to a **DI composition root** (instance class with one large `Build()` method). Main file keeps state fields, fluent setter methods, constants, and the `Build()` orchestrator. Each partial file owns one logical service group as a **private helper method** that the orchestrator calls.

**Tech Stack:** C# .NET 10, Microsoft.Extensions.Hosting / DependencyInjection / Configuration. App layer (Composition root). Git with LF line endings.

## Global Constraints

- **Public API unchanged.** No method signatures, properties, or fluent setter behaviors move.
- **partial-class visibility.** All private methods + private fields visible across partial files; cross-partial calls stay as plain invocations.
- **Test coverage unchanged.** No tests added, removed, or modified.
- **Line-ending normalized to LF.** Per v3.18.0 PATCH `.gitattributes` enforcement.
- **No behavioral change.** Every service registration order, every method body, every xmldoc, every comment, and every whitespace moves verbatim.
- **No version bump until Task 8.** Tasks 1-7 keep `src/Directory.Build.props` at v3.25.0. Task 8 bumps to v3.26.0.
- **Branch**: `feature/w11-app-host-builder-god-class` (already created from `main` @ `1baab1d` v3.25.0).
- **Spec**: this file.

---

## Current state (744 LoC)

`src/PeakCan.Host.App/Composition/AppHostBuilder.cs` (v3.25.0 HEAD) has:
- 1 instance class `AppHostBuilder` (NOT static, NOT partial)
- 1 public const `PcanUsbFdFirstHandle`
- 1 private state field `_udsSecurityLockoutConfig`
- 1 fluent setter `WithUdsSecurityLockoutConfig()`
- 1 massive `Build()` method (~647 LoC, ~87% of file) containing all DI service registrations

Threshold per `automotive-coding-standards-file-size.md`: 800 LoC ceiling. AppHostBuilder is at **93%** of ceiling.

## Target state (~150 LoC main + 7 partials)

```
src/PeakCan.Host.App/Composition/AppHostBuilder.cs                       # main file, ~150 LoC after Task 7
src/PeakCan.Host.App/Composition/AppHostBuilder/                          # NEW directory
  LoggingFlow.cs                                                          # Task 1 — Serilog + appsettings + env vars + cmd line (~70 LoC)
  CoreInfrastructureFlow.cs                                               # Task 2 — ChannelRouter + BusStatistics + TimerFactory + ChannelProbe + ChannelEnumerator + ChannelFactory + PcanReader (~55 LoC)
  AppServicesFlow.cs                                                      # Task 3 — TraceService + RecordingService + other App services (~100 LoC)
  ViewModelsBatch1Flow.cs                                                 # Task 4 — First batch of view models (~100 LoC)
  ViewModelsBatch2Flow.cs                                                 # Task 5 — Second batch of view models (~100 LoC)
  ViewModelsBatch3Flow.cs                                                 # Task 6 — Third batch of view models (~100 LoC)
  WindowAndHostedServicesFlow.cs                                          # Task 7 — AppShell + hosted services (~50 LoC)
docs/superpowers/plans/2026-07-11-app-host-builder-god-class-refactor.md   # NEW
docs/release-notes-v3.26.0.md                                              # NEW in Task 8
```

**Net reduction**: 744 → ~150 LoC main file (-594 LoC, -79.8%); total lines unchanged.

## Flow boundaries

All flows are **service registration sections within the `Build()` method body**. The refactor extracts each section into a `private void RegisterXxxServices(IServiceCollection services)` helper method, then `Build()` calls each helper in order.

### Flow A — Logging setup (~70 LoC)

**Contains**:
- `builder = Host.CreateApplicationBuilder(...)` — IHostBuilder creation
- Serilog configuration from appsettings.json
- Environment variables + command line args configuration
- Hardcoded smoke-test log writes (DEBUG-only)
- `builder.Logging.ClearProviders().AddSerilog(...)` — Serilog registration

**Helper signature**: `private void ConfigureLoggingAndBuilder(out IHostBuilder builder)` (out param since builder is needed by subsequent flows)

### Flow B — Core infrastructure (~55 LoC)

**Contains**:
- `ChannelRouter` registration
- `BusStatisticsCollector` registration
- `ITimerFactory` → `CyclicTimerFactory` registration
- `IChannelProbe` → `PeakChannelProbe` registration
- `IChannelEnumerator` → `PeakChannelEnumerator` registration
- `ICanChannelFactory` → `PeakCanChannelFactory` registration
- `IPcanReader` → `PcanReader` registration

**Helper signature**: `private void RegisterCoreInfrastructure(IServiceCollection services)`

### Flow C — App services (~100 LoC)

**Contains**:
- `TraceService` registration
- `RecordingService` + `StatisticsService` + `ReplayService` registration
- `RateLimitedSendService` decorator registration
- `CyclicSendService` + `CyclicDbcSendService` registration
- `ScriptingService` + `SendFrameLibrary` registration
- `PathSettingsService` registration

**Helper signature**: `private void RegisterAppServices(IServiceCollection services)`

### Flow D — ViewModels batch 1 (~100 LoC)

**Contains** (rough first half of view model registrations):
- `AppShellViewModel` registration
- `TraceSessionRegistry` + `RecentSessionsService` registration
- `ReplayViewModel` registration
- `TraceViewModel` registration
- `TraceViewerViewModel` registration (with sink wiring)

**Helper signature**: `private void RegisterViewModelsBatch1(IServiceCollection services)`

### Flow E — ViewModels batch 2 (~100 LoC)

**Contains** (second half):
- `MultiFrameSendViewModel` registration
- `SendViewModel` registration (with hosted service)
- `DbcViewModel` registration
- `SignalChartViewModel` registration
- `SignalViewModel` registration (with hosted service)

**Helper signature**: `private void RegisterViewModelsBatch2(IServiceCollection services)`

### Flow F — ViewModels batch 3 (~100 LoC)

**Contains** (third batch + remaining):
- `StatsViewModel` registration
- `ScriptViewModel` registration
- `RecordViewModel` registration (with hosted service)
- `AppShell` registration (factory-based for STA thread)
- `SinkWiringService` (hosted service)

**Helper signature**: `private void RegisterViewModelsBatch3(IServiceCollection services)`

### Flow G — Window + hosted services (~50 LoC)

**Contains**:
- AppShell STA factory registration
- SinkWiringService (hosted service) registration
- `return builder.Build()` — final orchestration

**Helper signature**: `private void RegisterWindowAndHostedServices(IServiceCollection services)`

### Main file — fields + fluent setters + Build orchestrator (~150 LoC)

**Stays in main**:
- All `using` directives
- Namespace + class declaration + xmldoc + remarks
- `public const ushort PcanUsbFdFirstHandle`
- Private state field `_udsSecurityLockoutConfig`
- Public fluent setter `WithUdsSecurityLockoutConfig(...)`
- **`Build()` orchestrator** — now thin: creates builder, calls `ConfigureLoggingAndBuilder()`, then calls 6 service-registration helpers in order, then `return builder.Build()`

**Moves to partials**: each flow's helper method + its xmldoc comments.

## Architecture invariants (per W3-W10 patterns)

1. **Public API unchanged**: `Build()` method signature stays the same; `WithUdsSecurityLockoutConfig()` fluent setter stays the same; `PcanUsbFdFirstHandle` const stays the same.
2. **partial-class visibility**: private methods visible across partial files; Build orchestrator calls each helper via partial-class visibility.
3. **Service registration order preserved**: each helper runs in the exact same order as the original inline code — no reordering, no batching changes.
4. **State stays close to its reader/writer**: `_udsSecurityLockoutConfig` stays in main (only read in fluent setter + Build).

## Verification

- `dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore`: 0 errors
- `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~AppHost\|FullyQualifiedName~Composition"`: all tests pass without modification
- (Wider test suite: run full `dotnet test` to catch any cross-VM DI regressions)

## Risk notes

- **R1 (low)**: Missing `using` directives — per W3-W10+W8.5+W9.5 CONFIRMED lesson (15+ confirmations). Pre-scan method bodies for type references.
- **R2 (low)**: Deletion script line-count assertion — per W3-W10+W8.5+W9.5 CONFIRMED lessons. Apply correct formula `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker` per W8.5 PATCH D7.
- **R3 (medium)**: **This is the first W3-W11 refactor that splits a single monolithic method (Build, 647 LoC) into 7 helper methods**. Other W3-W10 refactors moved complete methods. Risk: refactoring Build's structure could subtly change DI registration order or break closures over local variables. **MITIGATION**: each extracted helper must be a **verbatim copy** of the inline section (no logic changes), and the orchestrator must call them in the EXACT same order. Build's flow-control structure (try/catch, IHostBuilder build) stays in main.

## Out of scope (explicit YAGNI)

- **No facade pattern**: W3-W10+W8.5+W9.5 confirmed direct partial-class visibility is sufficient.
- **No sub-class creation**: AppHostBuilder stays a single class.
- **No test changes**: All existing tests stay unmodified.
- **No public/internal API surface change**: No new public methods, no removed methods, no renamed members.
- **No DI registration refactoring**: Each helper is verbatim copy of original inline section.

## Tasks remaining (preview for the plan)

1. **Task 1**: Extract Flow A (Logging setup) — first section, validates extraction pattern on a small block.
2. **Task 2**: Extract Flow B (Core infrastructure) — 7 service registrations, all similar pattern.
3. **Task 3**: Extract Flow C (App services) — largest flow, 10+ service registrations.
4. **Task 4**: Extract Flow D (ViewModels batch 1) — first half of view models.
5. **Task 5**: Extract Flow E (ViewModels batch 2) — second half.
6. **Task 6**: Extract Flow F (ViewModels batch 3) — third batch.
7. **Task 7**: Extract Flow G (Window + hosted services) — last flow.
8. **Task 8**: Bump version v3.25.0 → v3.26.0 + write release notes (MINOR ship commit).
9. **Task 9**: Tier-3 push + tag + GH release.

Total: 9 tasks, ~8 source commits (1 per flow + 1 ship).

## Decision log

- **D1**: 7 partials with descriptive names (Logging/CoreInfrastructure/AppServices/ViewModelsBatch1/2/3/WindowAndHostedServices).
- **D2**: Same W3-W10 pattern (no facade, no sub-classes).
- **D3**: Branch name `feature/w11-app-host-builder-god-class`.
- **D4**: Order tasks F (smallest, 70 LoC) → B (55 LoC) → G (50 LoC) → C (100 LoC) → D (100 LoC) → E (100 LoC) → F (100 LoC). Extract in dependency order: Logging first (creates IHostBuilder needed by all), then Core infrastructure (independent services), then App services (depend on Core), then ViewModels batches (depend on App services + Core), then Window + hosted services (depend on ViewModels).
- **D5**: Helper method extraction (not verbatim section move) — each service registration section becomes a `private void RegisterXxxServices(IServiceCollection services)` helper.
- **D6**: Plan LoC-trajectory-table formula explicitly applied per W8.5 PATCH CONFIRMED lesson: `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`.

## Closing milestone context

This is the **9th god-class refactor** in the project. AppHostBuilder is the **9th distinct class** (6 App layer VMs + 2 Core layer classes + this 1 App layer composition root). This refactor is **structurally unique**: it's the **first W3-W11 refactor that splits a single monolithic method body** into multiple helper methods across partial files (vs the previous pattern of moving complete methods).