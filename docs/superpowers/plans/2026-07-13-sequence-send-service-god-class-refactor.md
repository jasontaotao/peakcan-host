# W30 Implementation Plan — SequenceSendService god-class refactor (26th overall, 6th App/Services)

**Goal**: Extract 2 NEW partial-class files (SendFlow + RowBuildFlow) from `src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService.cs` 266 → ~82 LoC (-184 LoC, -69%). Public API + existing tests unchanged.

**Architecture**: Sister pattern of W22 + W23 + W27 + W28 + W29 (subdirectory + non-suffix `.partial.cs` filenames). 26th god-class refactor. **6th App/Services layer** + **1st App/Services/MultiFrame subdirectory** + **20th subdirectory-pattern deployment**.

**Tech Stack**: C# .NET 10 partial-class split pattern + W25 D5 deviation (LARGEST method moves) + W19 R1 first-correction + W20 verbatim re-extraction + W23 STRUCT-FABRICATION LESSON

**Spec**: [2026-07-13-sequence-send-service-god-class-refactor.md](../specs/2026-07-13-sequence-send-service-god-class-refactor.md)

---

## Global Constraints

- 1 partial class with 2 partials in subdirectory `src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService/`
- `public sealed partial class SequenceSendService` (D2 adds `partial` modifier at L31 in T0)
- All public API surface unchanged (DI registration + tests + XAML bindings)
- LoC formula `main_after = main_before - delete_count` (W8.5 D7 32-locked, no +1 offset)
- W17 wc-l-splitlines CONFIRMED + cp1252 binary read+write pattern (since file is Windows-1252 encoded)
- W19 R1 first-correction: re-grep boundaries BEFORE each deletion script
- W20 LESSON: verbatim re-extraction via `git show HEAD:src/...cs | sed -n '<range>p'`
- W23 STRUCT-FABRICATION LESSON: verify `CanId(raw, FrameFormat format)` 2-arg + `CanFrame(canId, payload, FrameFlags.None, ChannelId.None, default)` 5-arg signatures
- W25 D5 deviation: LARGEST method `SendAsync` 91 LoC MOVES to SendFlow.partial.cs (≥ 60 LoC + discrete flow boundary = concurrent vs sequential dispatcher)
- W26.5 D2 + W21 3/3 CONFIRMED: add `partial` modifier to monolithic class before extraction

---

## Task T0: Branch + partial-keyword add + spec + plan commits

**Files:**
- Modify: `src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService.cs:31` (add `partial` modifier)
- Create: `docs/superpowers/specs/2026-07-13-sequence-send-service-god-class-refactor.md`
- Create: `docs/superpowers/plans/2026-07-13-sequence-send-service-god-class-refactor.md`

**Interfaces:**
- Produces: `SequenceSendService` with `partial` modifier at L31

- [ ] **Step 1: Branch + partial-keyword add**

```bash
git checkout -b feature/w30-sequence-send-service-god-class main
# T0-D2: add partial keyword at L31 (per W26.5 3/3 CONFIRMED + W21 3/3 CONFIRMED sister)
python -c "
from pathlib import Path
p = Path('src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService.cs')
text = p.read_text(encoding='cp1252')
text = text.replace('public sealed class SequenceSendService', 'public sealed partial class SequenceSendService', 1)
p.write_text(text, encoding='cp1252')
"
grep -n "public sealed" src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService.cs
```

Expected: `31:public sealed partial class SequenceSendService`

- [ ] **Step 2: Build + tests to verify partial-keyword add is clean**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore 2>&1 | tail -10
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~SequenceSendService" --logger "console;verbosity=minimal" 2>&1 | tail -10
```

Expected: 0 errors, SequenceSendService tests pass without modification (partial-keyword add is binary-compatible).

- [ ] **Step 3: Commit partial-keyword add**

```bash
git add src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService.cs
git commit -m "W30 T0-D2: add partial modifier to SequenceSendService (per W26.5 3/3 CONFIRMED + W21 3/3 CONFIRMED sister)"
```

- [ ] **Step 4: Commit SPEC + PLAN**

```bash
git add docs/superpowers/specs/2026-07-13-sequence-send-service-god-class-refactor.md
git commit -m "W30 spec: SequenceSendService god-class refactor (2 partials + 5-task roll-out, 26th overall, 6th App/Services, 1st App/Services/MultiFrame, 20th subdirectory-pattern deployment)"
git add docs/superpowers/plans/2026-07-13-sequence-send-service-god-class-refactor.md
git commit -m "W30 plan: SequenceSendService god-class refactor (2 partials: SendFlow + RowBuildFlow)"
```

---

## Task T1: SendFlow partial extraction (~91 LoC)

**Files:**
- Create: `src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService/SendFlow.partial.cs` (~95 LoC)
- Modify: `src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService.cs` (delete L75-L165 inclusive)

**Interfaces:**
- Consumes: `SequenceSendService` partial-class visibility (private fields + ctors)
- Produces: 1 public method `SendAsync` extracted to SendFlow.partial.cs

- [ ] **Step 1: Re-grep boundaries BEFORE running deletion script (W19 R1 first-correction)**

```bash
grep -n "public async Task<Result> SendAsync\|private bool TryBuildRow\|public enum Mode\|public sealed record Result" src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService.cs
```

Expected:
```
56:    public enum Mode
65:    public sealed record Result(int SentCount, int FailureCount, int IterationsCompleted)
75:    public async Task<Result> SendAsync(
174:    private bool TryBuildRow(MultiFrameSequenceRow row, out CanFrame frame, out string? error)
```

- [ ] **Step 2: Verbatim re-extract SendAsync body from HEAD (W20 LESSON 35th application)**

```bash
git show HEAD:src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService.cs | sed -n '75,165p'
```

Expected: 91 LoC, complete SendAsync method body with `ArgumentNullException.ThrowIfNull` + `ArgumentOutOfRangeException.ThrowIfLessThan` + `ArgumentOutOfRangeException.ThrowIfNegative` + iteration loop + `TryBuildRow` call + `Task.WhenAll` concurrent dispatch + sequential dispatch with `Task.Delay` + progress reporting + `Result` construction.

- [ ] **Step 3: Write SendFlow.partial.cs (~95 LoC)**

```csharp
// SequenceSendService/SendFlow.partial.cs — W30 T1 (Flow A, LARGEST 91 LoC)
// SendAsync orchestration: dispatches the row sequence in concurrent
// (Task.WhenAll fan-out) or sequential (Task.Delay loop) mode for the
// requested iteration count. Per-row build errors count as row-level
// failures and continue with the remaining rows (v2.1.1 PATCH semantics).
//
// W25 D5 deviation APPLIED: SendAsync 91 LoC LARGEST method MOVES per the
// sharp discrete flow boundary criterion (concurrent vs sequential
// dispatcher = 2 distinct dispatching paths, NOT a single central
// orchestration loop). Sister of W25 OnChannelFrame 73 LoC + W26 OnFrame
// 62 LoC + W27 LoadAsync 60 LoC + W28 LoadAsync 79 LoC moves.
//
// Cross-partial helper pattern (W22+W23+W24+W25+W26+W27+W28+W29 sister):
// TryBuildRow + SendOneAsync private helpers live in RowBuildFlow.partial.cs
// and are called from this partial via partial-class visibility.
//
// W23 STRUCT-FABRICATION LESSON: CanId(raw, FrameFormat format) 2-arg +
// CanFrame(canId, payload, FrameFlags.None, ChannelId.None, default)
// 5-arg signatures verified during verbatim re-extraction from HEAD.
//
// W30 T1 verbatim re-extracted via `git show HEAD:src/.../SequenceSendService.cs | sed -n '75,165p'`
// per W20 T2 R1 fabrication LESSON (35th application).

using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.Services.MultiFrame;

public sealed partial class SequenceSendService
{
    /// <summary>
    /// Send the rows in <paramref name="rows"/> repeated
    /// <paramref name="iterations"/> times using the chosen
    /// <paramref name="mode"/>.
    /// </summary>
    public async Task<Result> SendAsync(
        IReadOnlyList<MultiFrameSequenceRow> rows,
        Mode mode,
        int delayMs,
        int iterations,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        // ... [verbatim content from HEAD L75-L165]
    }
}
```

- [ ] **Step 4: Write deletion script + run + verify**

```python
# scripts/w30_task1_delete_sendflow.py — W19 R1 first-correction + W17 wc-l-splitlines CONFIRMED
from pathlib import Path

p = Path("src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

START, END = 75, 165  # UPDATED per Step 1 grep result
before = len(lines)
new_lines = lines[:START - 1] + lines[END:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"SequenceSendService.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 91 (SendAsync body L75-L165 inclusive). Within ±2 LoC tolerance.")
assert 89 <= delta <= 93, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
```

- [ ] **Step 5: Build + tests to verify T1 extraction**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore 2>&1 | tail -10
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~SequenceSendService" --logger "console;verbosity=minimal" 2>&1 | tail -10
```

Expected: 0 errors, SequenceSendService tests pass without modification.

- [ ] **Step 6: Commit T1**

```bash
git add src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService.cs src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService/SendFlow.partial.cs scripts/w30_task1_delete_sendflow.py
git commit -m "W30 T1: extract SendFlow partial (SendAsync 91 LoC LARGEST method moves per W25 D5 deviation, -91 LoC main, 266 -> 175)"
```

---

## Task T2: RowBuildFlow partial extraction (~93 LoC)

**Files:**
- Create: `src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService/RowBuildFlow.partial.cs` (~95 LoC)
- Modify: `src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService.cs` (delete L174-L266 inclusive after re-grep)

**Interfaces:**
- Consumes: `SequenceSendService` partial-class visibility (private fields)
- Produces: 2 private helpers `TryBuildRow` + `SendOneAsync` extracted to RowBuildFlow.partial.cs

- [ ] **Step 1: Re-grep boundaries AFTER T1 deletion (W19 R1 first-correction)**

```bash
grep -n "private bool TryBuildRow\|private async Task<bool> SendOneAsync\|^}" src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService.cs
```

Expected: `TryBuildRow` at the new line corresponding to original L174 (post-T1 shift) + `SendOneAsync` + closing `}` of the class.

- [ ] **Step 2: Verbatim re-extract TryBuildRow + SendOneAsync from HEAD (W20 LESSON 36th application)**

```bash
git show HEAD:src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService.cs | sed -n '174,266p'
```

Expected: 93 LoC, complete TryBuildRow + SendOneAsync method bodies.

- [ ] **Step 3: Write RowBuildFlow.partial.cs (~95 LoC)**

```csharp
// SequenceSendService/RowBuildFlow.partial.cs — W30 T2 (Flow B, 93 LoC)
// Row-encoding helpers: TryBuildRow dispatches on MultiFrameSequenceRow.Kind
// (Raw vs Dbc) to build a CanFrame + per-row error string; SendOneAsync
// delegates to _sendService.SendAsync with exception-isolation (catches
// non-cancellation exceptions and returns false; re-throws OperationCanceledException).
//
// Called from SendFlow.partial.cs (Flow A) SendAsync via partial-class
// visibility (sister of W22+W23+W24+W25+W26+W27+W28+W29 cross-partial
// helper pattern).
//
// W23 STRUCT-FABRICATION LESSON: CanId(raw, FrameFormat format) 2-arg +
// CanFrame(canId, payload, FrameFlags.None, ChannelId.None, default)
// 5-arg signatures verified during verbatim re-extraction from HEAD
// (same signatures verified in W30 T1 SendFlow.partial.cs flow comment).
//
// W30 T2 verbatim re-extracted via `git show HEAD:src/.../SequenceSendService.cs | sed -n '174,266p'`
// per W20 T2 R1 fabrication LESSON (36th application).

using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.Services.MultiFrame;

public sealed partial class SequenceSendService
{
    /// <summary>
    /// v2.1.1 PATCH: build a <see cref="CanFrame"/> from a row,
    /// dispatching on <see cref="MultiFrameSequenceRow.Kind"/>.
    /// Returns false on any build error (bad hex, unknown DBC
    /// message, encoder exception) — caller treats as a row-level
    /// failure and continues with the rest of the sequence.
    /// </summary>
    private bool TryBuildRow(MultiFrameSequenceRow row, out CanFrame frame, out string? error)
    {
        // ... [verbatim content from HEAD L174-L249]
    }

    private async Task<bool> SendOneAsync(CanFrame frame, CancellationToken ct)
    {
        // ... [verbatim content from HEAD L251-L266]
    }
}
```

- [ ] **Step 4: Write deletion script + run + verify**

```python
# scripts/w30_task2_delete_rowbuildflow.py
from pathlib import Path

p = Path("src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

START, END = 174, 266  # UPDATED per Step 1 grep result (post-T1 shift)
before = len(lines)
new_lines = lines[:START - 1] + lines[END:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"SequenceSendService.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 93 (TryBuildRow + SendOneAsync L174-L266 inclusive). Within ±2 LoC tolerance.")
assert 91 <= delta <= 95, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
```

- [ ] **Step 5: Build + tests to verify T2 extraction**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore 2>&1 | tail -10
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~SequenceSendService" --logger "console;verbosity=minimal" 2>&1 | tail -10
```

Expected: 0 errors, SequenceSendService tests pass without modification.

- [ ] **Step 6: Commit T2**

```bash
git add src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService.cs src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService/RowBuildFlow.partial.cs scripts/w30_task2_delete_rowbuildflow.py
git commit -m "W30 T2: extract RowBuildFlow partial (TryBuildRow + SendOneAsync, -93 LoC main, 175 -> 82)"
```

---

## Task T3: v3.43.5 → v3.44.0 MINOR + release notes

**Files:**
- Modify: `src/Directory.Build.props` (4-field version bump)
- Create: `docs/release-notes-v3.44.0.md` (~120 LoC)

- [ ] **Step 1: Bump Directory.Build.props**

```bash
python -c "
from pathlib import Path
p = Path('src/Directory.Build.props')
text = p.read_text(encoding='cp1252')
text = text.replace('<Version>3.43.5</Version>', '<Version>3.44.0</Version>', 1)
text = text.replace('<AssemblyVersion>3.43.5.0</AssemblyVersion>', '<AssemblyVersion>3.44.0.0</AssemblyVersion>', 1)
text = text.replace('<FileVersion>3.43.5.0</FileVersion>', '<FileVersion>3.44.0.0</FileVersion>', 1)
text = text.replace('<InformationalVersion>3.43.5</InformationalVersion>', '<InformationalVersion>3.44.0</InformationalVersion>', 1)
p.write_text(text, encoding='cp1252')
"
grep -E "Version|FileVersion|InformationalVersion" src/Directory.Build.props
```

Expected: `<Version>3.44.0</Version>` + `<AssemblyVersion>3.44.0.0</AssemblyVersion>` + `<FileVersion>3.44.0.0</FileVersion>` + `<InformationalVersion>3.44.0</InformationalVersion>`.

- [ ] **Step 2: Write release notes**

Mirror W29 release-notes-v3.43.0.md format (~120 LoC).

- [ ] **Step 3: Build + full test suite to verify MINOR**

```bash
dotnet build PeakCan.Host.slnx -c Debug --no-restore 2>&1 | tail -5
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --logger "trx;LogFileName=w30-results.trx" 2>&1 | tail -10
```

Expected: 0 errors, full App.Tests pass.

- [ ] **Step 4: Commit T3**

```bash
git add src/Directory.Build.props docs/release-notes-v3.44.0.md
git commit -m "W30 T3: v3.43.5 -> v3.44.0 MINOR (SequenceSendService god-class refactor, 26th overall, 6th App/Services, 1st App/Services/MultiFrame)"
```

---

## Task T4: Tier-3 ship (PR + squash + tag + GH release)

- [ ] **Step 1: Push branch + create PR**

```bash
git push -u origin feature/w30-sequence-send-service-god-class
gh pr create --base main --head feature/w30-sequence-send-service-god-class --title "W30 MINOR: SequenceSendService god-class refactor (26th overall, 6th App/Services, 2-partial design, -184 LoC -69% main)" --body "[full PR body per W29 PR #59 sister precedent]"
```

- [ ] **Step 2: Wait for CI + squash-merge + tag + GH release**

```bash
gh pr checks <PR-number> --watch  # wait for CI (5-attempt MAX per W23.5-W29 sister pattern)
gh pr merge <PR-number> --squash --delete-branch
git tag -a v3.44.0 -m "v3.44.0 MINOR: SequenceSendService god-class refactor (26th overall, 6th App/Services, 1st App/Services/MultiFrame, -184 LoC -69% main, 2-partial design SendFlow+RowBuildFlow)" <squash-commit-sha>
git push origin v3.44.0
gh release create v3.44.0 --title "v3.44.0 — SequenceSendService god-class refactor" --notes "[release notes body]"
```

---

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings
- `dotnet test --filter "FullyQualifiedName~SequenceSendService"`: tests pass without modification
- `dotnet test` (full solution): 0 new fails
- `wc -l src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService.cs` ≤ 100 LoC (target ~82)
- 2 NEW partial files in `SequenceSendService/` directory
- 3 readonly fields + 2 ctors + 1 enum + 1 record remain in main
- Tag v3.44.0 + GH release published
- Branch deleted post-merge

## Out of scope (YAGNI)

- No public/internal API change.
- No test changes.
- No facade pattern (W3-W29 CONFIRMED direct partial-class visibility).
- No xmldoc-grep risk (verified — tests do not path-grep main file content).
- No W18 R1 mitigation (zero `[LoggerMessage]` partials → no CS8795 risk).
- No `MultiFrameSequenceRow` or `MultiFrameSequenceRow.Kind` inner-enum changes (stays in `Models` namespace).
- No `SendService.SendAsync` or `DbcEncodeService.Encode` cross-service API changes.
