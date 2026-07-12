# W23 Plan — CyclicDbcSendService god-class refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mechanically split `src/PeakCan.Host.App/Services/CyclicDbcSendService.cs` (383 LoC) into 3 partial-class files. Class is already `partial` (no modifier edit). Zero behavioral change.

**Architecture:** Sister of W22 RecordService (subdirectory + non-suffix `.cs` filenames; same-named precedent). 19th god-class refactor. 2nd App/Services + 1st cyclic-send. Order: A (Lifecycle) → B (Cycling, LARGEST) → C (Logging).

**Tech Stack:** C# .NET 10, App/Services layer + ICyclicDbcSendService interface + IDisposable + Microsoft.Extensions.Logging.

**Spec:** [`../specs/2026-07-12-cyclic-dbc-send-service-god-class-refactor.md`](../specs/2026-07-12-cyclic-dbc-send-service-god-class-refactor.md)
**Branch:** `feature/w23-cyclic-dbc-send-service-god-class` (created from `main` @ `16f35a3` v3.36.0 HEAD; spec commit `f465f78`)

## Global Constraints

- Public API unchanged.
- partial-class visibility on private fields + private methods.
- Test coverage unchanged (16 CyclicDbcSendServiceTests + sister tests = 16+ instantiation sites pass without modification).
- LF line endings.
- No behavioral change.
- No version bump until Task 4.
- Outer class already `public sealed partial class CyclicDbcSendService : ICyclicDbcSendService, IDisposable` at line 57 — no CS0260 mitigation.
- All 7 `[LoggerMessage]` partials retain `private static partial` modifier (peakcan-host convention; no W18 R1 mitigation).

## LoC trajectory (W8.5 D7 29-locked + W19 R1 first-correction + W17 wc-l-splitlines CONFIRMED + W20 fabrication LESSON APPLIED 13+ times across W20+W21+W22)

| Task | Flow | Range (1-indexed) | LoC deleted | Markers | LoC main after |
|---|---|---|---|---|---|
| T1 | A — Lifecycle | TBD per Phase 1 exact grep (Lifecycle cluster) | ~110 | 1 | ~273 |
| T2 | B — Cycling | TBD per Phase 1 exact grep (around OnTimerTick 151 LoC) | ~151 | 1 | ~122 |
| T3 | C — Logging | L363-382 (7 [LoggerMessage] partials) | ~20 | 1 | ~102 |
| T4 | v3.36.0 -> v3.37.0 | (no source) | 0 | 0 | ~102 |
| T5 | ship | -- | -- | -- | ~102 |

Cumulative: 383 -> ~273 -> ~122 -> ~102 main. Re-grep + range verify after each task per W19 R1 + W17 CONFIRMED.

---

## Task 0: Branch + plan commit

```bash
git add docs/superpowers/plans/2026-07-12-cyclic-dbc-send-service-god-class-refactor.md
git commit -m "W23 plan: CyclicDbcSendService god-class refactor (3 partials: Lifecycle + Cycling + Logging)"
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~CyclicDbcSendService" --logger "console;verbosity=minimal"
```

---

## Task 1: Extract Flow A — Lifecycle.partial.cs (~110 LoC)

**Files:**
- Modify: `src/PeakCan.Host.App/Services/CyclicDbcSendService.cs` (delete 2 ctors + Start + Stop + StopInner + Dispose + 3 properties + interim xmldoc/comments)
- Create: `src/PeakCan.Host.App/Services/CyclicDbcSendService/Lifecycle.partial.cs`

**Step 1**: Re-grep post-T0 ranges (Phase 1 explore already done; verify with fresh grep before deletion).

**Step 2**: Write `scripts/w23_task1_delete_lifecycleflow.py` with W19 T1 2/3 loose assertion + W17 wc-l-splitlines CONFIRMED pattern.

**Step 3**: Run deletion. Expected: 383 - 110 + 1 ≈ 274 LoC post-marker. Loose assertion `abs(actual - expected) <= 2`.

**Step 4**: **W20 LESSON APPLIED (4th god-class, 14th application)**: Re-extract original code from HEAD via `git show HEAD:src/PeakCan.Host.App/Services/CyclicDbcSendService.cs | sed -n '<range>p'`. NEVER fabricate API.

Create `Lifecycle.partial.cs` with verbatim extracted code. Required usings:
- `System.Threading.Channels` (sister of W22)
- `Microsoft.Extensions.Logging` (ILogger for the methods that log via _logger)

Class declaration: `public sealed partial class CyclicDbcSendService`

The methods must travel together (sister of W14 D2 + W3 R3 mutable-state coupling principle).

**Step 5**: Build + tests (CyclicDbcSendService filter tests).

**Step 6**: Commit: `W23 Task 1: extract Flow A (Lifecycle: 2 ctors + Start + Stop + StopInner + Dispose + 3 properties) to partial`.

---

## Task 2: Extract Flow B — Cycling.partial.cs (~151 LoC)

**Files:**
- Modify: `src/PeakCan.Host.App/Services/CyclicDbcSendService.cs` (delete OnTimerTick + interim xmldoc/comments)
- Create: `src/PeakCan.Host.App/Services/CyclicDbcSendService/Cycling.partial.cs`

**Step 1**: Re-grep post-T1 ranges.

**Step 2**: Write `scripts/w23_task2_delete_cyclingflow.py`.

**Step 3**: Run deletion. Expected: ~274 - 151 + 1 ≈ 124 LoC post-marker.

**Step 4**: **W20 LESSON APPLIED (14th application)**: Re-extract verbatim from HEAD.

Create `Cycling.partial.cs` with verbatim extracted code. Required usings:
- `Microsoft.Extensions.Logging` (ILogger)

`OnTimerTick` 151 LoC stays inline per W12-W22 D5 sister.

**Step 5**: Build + tests + commit.

---

## Task 3: Extract Flow C — Logging.partial.cs (~20 LoC)

**Files:**
- Modify: `src/PeakCan.Host.App/Services/CyclicDbcSendService.cs` (delete 7 [LoggerMessage] partials + interim xmldoc/comments)
- Create: `src/PeakCan.Host.App/Services/CyclicDbcSendService/Logging.partial.cs`

**Step 1**: Re-grep post-T2 ranges.

**Step 2**: Write `scripts/w23_task3_delete_loggingflow.py`.

Range: L363-382 (20 LoC: 7 [LoggerMessage] attribute+method pairs + blanks).

**Step 3**: Run deletion. Expected: ~124 - 20 + 1 ≈ 105 LoC post-marker.

**Step 4**: **W20 LESSON APPLIED (14th application)**: Re-extract verbatim from HEAD.

Create `Logging.partial.cs` with verbatim extracted code. Required usings:
- `Microsoft.Extensions.Logging` (ILogger + LogLevel)

**CRITICAL**: Class declaration MUST be `public sealed partial class CyclicDbcSendService` to satisfy CS8795. All 7 partials retain `private static partial` modifier (peakcan-host convention per W20 Phase 1 explore + W22 D4).

**Step 5**: Build + tests + commit.

---

## Task 4: Bump version v3.36.0 → v3.37.0 + release notes

Mirror W22 release notes format. MINOR (3 NEW partial extractions = architectural change).

---

## Task 5: Tier-3 push + tag + GH release

Standard: `gh pr create` → `--squash --delete-branch` → `git tag v3.37.0` → `gh release create`.

---

## Acceptance Criteria

- [ ] `CyclicDbcSendService.cs` ≤ 130 LoC (target ~102)
- [ ] 3 NEW partial files in `CyclicDbcSendService/` directory
- [ ] Outer class stays `public sealed partial class CyclicDbcSendService : ICyclicDbcSendService, IDisposable`
- [ ] 16 existing CyclicDbcSendServiceTests pass without modification
- [ ] Sister tests (DbcSendViewModel) pass without modification
- [ ] `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings (no CS8795 risk if `private static partial` retained)
- [ ] Full solution `dotnet test`: 0 new fails
- [ ] Tag v3.37.0 + GH release published
- [ ] Branch deleted post-merge

## Lesson Promotions to Monitor During W23

| Lesson | Status | What W23 might observe |
|---|---|---|
| `partial-extraction-must-use-original-code-from-head-not-fabricated-api` | 3/3 CONFIRMED (W21) | W23 4th god-class application (T1+T2+T3) — 14th total application |
| `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` | 2/3 (W22) | W23 3rd observation (Logging partial with 7 [LoggerMessage] partials) — potential **3/3 CONFIRMED** |
| `on-timer-tick-largest-method-stays-inline-151-loc` | NEW W23 1/3 | W23 1st observation: `OnTimerTick` 151 LoC (single tightly-cohesive method) stays inline per W12-W22 D5 sister |
| `cyclic-send-service-vs-record-service-app-services-sister-pattern` | NEW W23 1/3 | W23 1st observation: 2 consecutive App/Services refactors (W22 RecordService + W23 CyclicDbcSendService) using identical subdirectory + Lifecycle/Cycling/Logging partial structure |
| `interfaceless-split-safe-via-idisposable-and-partial-class` | NEW W23 1/3 | W23 1st observation: all consumers use `ICyclicDbcSendService` interface so partial split is invisible to consumers |
| `subdirectory-partials-pattern-empirical-13-precedents` | 3/3 CONFIRMED (W20) | W23 13th deployment, sister-of-W22 |