# W18 Plan — PeakCanChannel god-class refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mechanically split `src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs` (389 LoC) into 2 partial-class files + ~250 LoC main file. Zero behavioral change.

**Architecture:** Sister of W3-W17. 1st Infrastructure layer + PEAK native binding. Order: A (ReadLoopFlow) → B (NativeBindings).

**Tech Stack:** C# .NET 10, Infrastructure layer + PEAK PCANBasic P/Invoke.

**Spec:** [`../specs/2026-07-12-peak-can-channel-god-class-refactor.md`](../specs/2026-07-12-peak-can-channel-god-class-refactor.md)
**Branch:** `feature/w18-peak-can-channel-god-class` (created from `main` @ `77dae98` spec commit)

## Global Constraints

- Public API unchanged (`ICanChannel` implementation preserved).
- partial-class visibility on private fields + private methods.
- Test coverage unchanged.
- LF line endings.
- No behavioral change.
- No version bump until Task 3.
- Outer class already `public sealed partial class PeakCanChannel : ICanChannel` at line 65 — no CS0260 mitigation.

## LoC trajectory (W8.5 D7 16-locked + W13 T1 2/3 loose assertion + W17 wc-l-splitlines CONFIRMED)

| Task | Flow | Range (1-indexed) | LoC deleted | Markers | LoC main after |
|---|---|---|---|---|---|
| T1 | A — ReadLoopFlow | 229-332 (ReadLoopAsync 229-303 + SafeEmitReadLoopError 317-332 with 304-316 separator) | ~100 | 1 | ~290 |
| T2 | B — NativeBindings | 336-389 | ~54 | 1 | ~237 |
| T3 | v3.31.1 -> v3.32.0 | (no source) | 0 | 0 | ~237 |
| T4 | ship | -- | -- | -- | ~237 |

Cumulative: 389 -> ~290 -> ~237 main.

---

## Task 0: Branch + plan commit

```bash
git checkout -b feature/w18-peak-can-channel-god-class main
dotnet build src/PeakCan.Host.Infrastructure/PeakCan.Host.Infrastructure.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~Channel" --logger "console;verbosity=minimal"
git add docs/superpowers/plans/2026-07-12-peak-can-channel-god-class-refactor.md
git commit -m "W18 plan: PeakCanChannel god-class refactor (2 partials + 4-task roll-out)"
```

---

## Task 1: Extract Flow A — ReadLoopFlow.cs (~85 LoC)

**Files:**
- Modify: `src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs:229-332` (delete ReadLoopAsync + SafeEmitReadLoopError + interim comments)
- Create: `src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel/ReadLoopFlow.cs`

**Step 1**: Write `scripts/w18_task1_delete_readloopflow.py` with W13 T1 2/3 loose assertion + W17 wc-l-splitlines CONFIRMED pattern. Re-grep post-T0 ranges. Range: lines 229-332 (~104 LoC including blank lines + xmldoc).

**Step 2**: Run deletion. Expected: 389 - 104 + 1 ≈ 286 LoC post-marker.

**Step 3**: Create `ReadLoopFlow.cs` with verbatim extracted code (ReadLoopAsync + SafeEmitReadLoopError + 3-emit-call sites). Required usings: `Peak.Can.Basic` for `TPCANMsg`/`TPCANTimestamp` types via the `IPcanReader` P/Invoke wrapper.

**Step 4**: Build + tests.

**Step 5**: Commit: `W18 Task 1: extract Flow A (ReadLoopFlow: ReadLoopAsync + SafeEmitReadLoopError) to partial`.

---

## Task 2: Extract Flow B — NativeBindings.cs (~50 LoC)

**Files:**
- Modify: `src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs:336-389` (delete EmitClassic + EmitFd + MakeError + ResolveClassicCode)
- Create: `src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel/NativeBindings.cs`

**Step 1**: Re-grep post-T1 ranges.

**Step 2**: Write `scripts/w18_task2_delete_nativebindings.py`.

**Step 3**: Run deletion. Expected: ~286 - 54 + 1 ≈ 233 LoC post-marker.

**Step 4**: Create `NativeBindings.cs` with verbatim code (4 static + instance helpers). Required usings: `Peak.Can.Basic` for `TPCAN*` types.

**Step 5**: Build + tests + full solution. Commit.

---

## Task 3: Bump version v3.31.1 → v3.32.0 + release notes

Mirror W12/W16 release notes format.

---

## Task 4: Tier-3 push + tag + GH release

Standard: `gh pr create` → `--squash --delete-branch` → `git tag v3.32.0` → `gh release create`.

---

## Acceptance Criteria

- [ ] `PeakCanChannel.cs` ≤ 280 LoC (target ~237)
- [ ] 2 NEW partial files in `PeakCanChannel/` directory
- [ ] Outer class stays `public sealed partial class PeakCanChannel : ICanChannel`
- [ ] `dotnet build src/PeakCan.Host.Infrastructure/`: 0 errors
- [ ] `dotnet test --filter "~Channel"`: count unchanged, 0 new fails
- [ ] Full solution `dotnet test`: 0 new fails
- [ ] Tag v3.32.0 + GH release published
- [ ] Branch deleted post-merge

## Lesson Promotions to Monitor During W18

| Lesson | Status | What W18 might observe |
|---|---|---|
| `peak-can-channel-infrastructure-layer-native-binding-survives-partial-extraction` | 1/3 (NEW W18 candidate) | W18 T1 first observation if verbatim extraction of `_reader.Dispose()` + handle-cleanup preserves PEAK SDK lifecycle |
| `execution-lifecycle-cluster-must-not-be-split-across-partials` | 2/3 (W3 + W14) | Awaits W18 if ReadLoopAsync + DisposeAsync lifecycle coupling aligns |
| `xmldoc-grep-test-breaks-when-partial-class-split-moves-the-overloaded-method-xmldoc-into-different-file` | 1/3 (W12 T4) | Awaits W18 if any source-path grep test breaks |
| `internal-sealed-partial-class-modifier-doesnt-constrain` | 1/3 (W15) | N/A (W18 is `public`, not `internal`) |
| `replay-vm-manual-properties-with-partial-class-visibility-into-service-field` | 1/3 (W16) | N/A (W18 is not VM) |
| `sibling-file-pattern-vs-subdirectory` | 1/3 (W16) | N/A (W18 uses subdirectory, not sibling) |
