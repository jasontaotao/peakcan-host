# W14 Plan — ScriptEngine god-class refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mechanically split `src/PeakCan.Host.App/Services/Scripting/ScriptEngine.cs` (548 LoC) into 3 partial-class files + ~160 LoC main file. Zero behavioral change.

**Architecture:** Sister pattern to W3-W13 partial-class split. Order: A (largest, lifecycle cluster) → B (CreateEngine) → C (Helpers).

**Tech Stack:** C# .NET 10 + ClearScript 7.4.5, App layer.

**Spec:** [`../specs/2026-07-12-script-engine-god-class-refactor.md`](../specs/2026-07-12-script-engine-god-class-refactor.md)
**Branch:** `feature/w14-script-engine-god-class` (created from `main` @ `ee6d7fa` spec commit)

## Global Constraints (carried verbatim from spec)

- Public API unchanged.
- partial-class visibility on private fields + private methods.
- Test coverage unchanged. No xmldoc-grep tests for ScriptEngine per W12 D8 sister.
- LF line endings.
- No behavioral change.
- No version bump until Task 4. Tasks 1-3 keep `Directory.Build.props` at v3.28.0.
- Outer class already `partial` at line 26 — no CS0260 mitigation needed.

## LoC trajectory table (per W8.5 PATCH D7 CONFIRMED + W13 T1 2/3 loose-assertion fix)

Formula: `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`. Use **loose assertion** to handle un-trailing-newline off-by-one (per W13 T1 lesson).

| Task | Flow | Range (1-indexed inclusive) | LoC deleted | Markers | LoC main after |
|---|---|---|---|---|---|
| T1 | A — ExecutionLifecycle | 111-358 (RunAsync + Stop + InterruptEngine + ExecuteScript — contiguous block) | ~225 | 1 | ~324 (548-225+1) |
| T2 | B — CreateEngine | 372-449 | ~78 | 1 | ~247 |
| T3 | C — ScriptHelpers | 454-475 + 478-482 (EmitOutput + IsResourceLimit + Dispose) | ~28 | 1 | ~220 |
| T4 | version bump + release notes | (no source LoC) | 0 | 0 | ~220 |
| **T5** | ship | -- | -- | -- | **~220** |

Cumulative checkpoint: ~220 LoC main + 3 partials ≈ 548 LoC total. Sister of W12 UdsClient (174 main + 5 partials) but with tighter scope per spec D4.

Note: T1's range estimation starts as 111-358. After T1 deletion, T2's range shifts accordingly; must re-grep post-T1.

---

## Task 0: Branch + plan commit

**Files:**
- Create: `docs/superpowers/plans/2026-07-12-script-engine-god-class-refactor.md` (this file)

**Step 1**: Verify branch:
```bash
git checkout -b feature/w14-script-engine-god-class main
```

**Step 2**: Baseline build:
```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
```
Expected: 0 errors.

**Step 3**: Baseline test count for ScriptEngine:
```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~ScriptEngine" --logger "console;verbosity=minimal"
```
Expected: all ScriptEngine-related tests pass, capture count for after-comparison.

**Step 4**: Commit plan:
```bash
git add docs/superpowers/plans/2026-07-12-script-engine-god-class-refactor.md
git commit -m "W14 plan: ScriptEngine god-class refactor (3 partials + 5-task roll-out)"
```

---

## Task 1: Extract Flow A — ExecutionLifecycleFlow (largest, ~225 LoC)

**Files:**
- Modify: `src/PeakCan.Host.App/Services/Scripting/ScriptEngine.cs:111-358` (delete RunAsync + Stop + InterruptEngine + ExecuteScript — contiguous block 248 LoC)
- Create: `src/PeakCan.Host.App/Services/Scripting/ScriptEngine/ExecutionLifecycleFlow.cs`

**Step 1**: Write `scripts/w14_task1_delete_executionlifecycleflow.py` using post-T0 ranges. Apply W13 T1 2/3 loose-assertion pattern (accept ±1 LoC tolerance).

**Step 2**: Run deletion. Expected: ~225 LoC out, main reduces from 548 to ~324 (off by ±2 LoC tolerance).

**Step 3**: Create `ExecutionLifecycleFlow.cs` with verbatim extracted code (RunAsync + Stop + InterruptEngine + ExecuteScript — 4 methods).

**Step 4**: `dotnet build` + ScriptEngine tests. Expected: 0 errors, test count unchanged.

**Step 5**: Commit: `W14 Task 1: extract Flow A (ExecutionLifecycle: RunAsync+Stop+InterruptEngine+ExecuteScript) to partial`.

---

## Task 2: Extract Flow B — CreateEngineFlow (~90 LoC)

**Files:**
- Modify: `src/PeakCan.Host.App/Services/Scripting/ScriptEngine.cs:372-449` (delete CreateEngine)
- Create: `src/PeakCan.Host.App/Services/Scripting/ScriptEngine/CreateEngineFlow.cs`

**Step 1**: Re-grep post-T1 ranges. (Line numbers shift after T1.)

**Step 2**: Write deletion script, run, verify LoC math.

**Step 3**: Create `CreateEngineFlow.cs` with verbatim CreateEngine.

**Step 4**: Build + test. Commit.

---

## Task 3: Extract Flow C — ScriptHelpersFlow (~55 LoC)

**Files:**
- Modify: `src/PeakCan.Host.App/Services/Scripting/ScriptEngine.cs:454-475 + 478-482` (delete EmitOutput + IsResourceLimit + Dispose — 2 contiguous ranges, leaving the 3 LoggerMessage partials in main)
- Create: `src/PeakCan.Host.App/Services/Scripting/ScriptEngine/ScriptHelpersFlow.cs`

**Step 1**: Re-grep post-T2 ranges.

**Step 2**: Write deletion script. **CRITICAL**: the 3 LoggerMessage partials at lines 484-491 (post-T2) stay in main. Script must delete ONLY lines for EmitOutput, IsResourceLimit, Dispose.

**Step 3**: Create `ScriptHelpersFlow.cs` with verbatim EmitOutput + IsResourceLimit (Dispose stays in main per D2).

Wait — D2 says "Dispose stays in main". Re-read: D2 says "Dispose + fields + ctors stay in main". So **Dispose goes in main, not Flow C.** Flow C only has EmitOutput + IsResourceLimit.

Let me update the plan. Flow C scope is just EmitOutput + IsResourceLimit (~25 LoC), not Dispose.

Updated task: T3 deletes only 25 LoC (EmitOutput at 454-457 + IsResourceLimit at 469-475).

**Step 4**: Build + test. Final main ~245 LoC. Commit.

---

## Task 4: Bump version v3.28.0 → v3.29.0 + write release notes

**Files:**
- Modify: `src/Directory.Build.props` (v3.28.0 → v3.29.0)
- Create: `docs/release-notes-v3.29.0.md`

Mirror W12/W13 release notes format.

---

## Task 5: Tier-3 push + tag + GH release

Standard flow: push branch, gh pr create, --squash --delete-branch, gh release create v3.29.0.

---

## Acceptance Criteria

- [ ] ScriptEngine.cs ≤ 250 LoC (target ~245)
- [ ] 3 partial files in `ScriptEngine/` directory
- [ ] All 4 sibling types remain in main file
- [ ] `dotnet build src/PeakCan.Host.App/`: 0 errors
- [ ] ScriptEngine tests: count unchanged, 0 new fails
- [ ] Full solution `dotnet test`: 0 new fails
- [ ] Tag v3.29.0 + GH release published
- [ ] Branch deleted post-merge
- [ ] MEMORY.md updated
