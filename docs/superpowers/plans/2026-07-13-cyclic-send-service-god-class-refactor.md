# W34 Implementation Plan — CyclicSendService god-class refactor (30th overall, 8th App/Services layer)

**Goal**: Extract 2 NEW partial-class files (Lifecycle + TimerTick) from `src/PeakCan.Host.App/Services/CyclicSendService.cs` 243 → ~93 LoC (-150 LoC, -61.7%). Public API + existing tests unchanged.

**Architecture**: Sister pattern of W22 + W23 + W27 + W28 + W29 + W30 + W31 + W32 + W33 (subdirectory + non-suffix `.partial.cs` filenames). 30th god-class refactor. **8th App/Services layer** (sister of W22 + W23 + W27 + W28 + W29 + W30 + W31 + W32 + W33) + **24th subdirectory-pattern deployment** + **2nd multi-interface partial-class extraction** (sister of W31 ReplayService `IReplayService + IDisposable`).

**Tech Stack**: C# .NET 10 partial-class split pattern + W22+W23 sister "orchestration-loop stays inline" pattern (W25 D5 deviation NOT applied since `OnTimerTick` 61 LoC IS state-machine dispatch loop) + W19 R1 first-correction ENHANCED (pre-flight prevention + post-failure recovery) + W20 verbatim re-extraction + W23 STRUCT-FABRICATION LESSON

**Spec**: [2026-07-13-cyclic-send-service-god-class-refactor.md](../specs/2026-07-13-cyclic-send-service-god-class-refactor.md)

---

## Global Constraints

- 1 partial class with 2 partials in subdirectory `src/PeakCan.Host.App/Services/CyclicSendService/`
- `public sealed partial class CyclicSendService : ICyclicSendService, IDisposable` (already partial at L33; no D2 application needed per W21 + W26.5 + W30 + W31 + W32 + W33 sister precedent)
- All public API surface unchanged (DI registration + tests + interface contracts)
- LoC formula `main_after = main_before - delete_count` (W8.5 D7 32-locked, no +1 offset)
- W17 wc-l-splitlines CONFIRMED + cp1252 binary read+write pattern (since file is Windows-1252 encoded)
- W19 R1 first-correction ENHANCED: re-grep boundaries BEFORE each deletion script + post-failure recovery procedure (git checkout + re-grep + corrected offsets + re-run)
- W20 LESSON: verbatim re-extraction via `git show main:src/.../CyclicSendService.cs | sed -n '<range>p'`
- W23 STRUCT-FABRICATION LESSON: verify `Interlocked.Exchange` 2-arg + `Interlocked.Read` 1-arg + `_timerFactory.CreateCyclicTimer` 3-arg + `CancellationTokenSource` 0-arg + `lock(this)` statement signatures
- W25 D5 deviation NOT applied: `OnTimerTick` 61 LoC ≥ 60 LoC threshold BUT orchestration-loop shape (sister of W22 + W23 stays) → STAYS INLINE per W25 D5 deviation criteria #3

---

## Task T0: Branch + SPEC + PLAN commits

**Files:**
- Create: `docs/superpowers/specs/2026-07-13-cyclic-send-service-god-class-refactor.md`
- Create: `docs/superpowers/plans/2026-07-13-cyclic-send-service-god-class-refactor.md`

**Interfaces:**
- Produces: SPEC + PLAN committed to feature branch

- [ ] **Step 1: Branch + verify already partial**

```bash
git checkout -b feature/w34-cyclic-send-service-god-class main
grep -n "public sealed" src/PeakCan.Host.App/Services/CyclicSendService.cs | head -1
```

Expected: `33:public sealed partial class CyclicSendService : ICyclicSendService, IDisposable` (already partial per W21 + W26.5 + W30 + W31 + W32 + W33 sister precedent; no D2 application needed).

- [ ] **Step 2: Build + tests baseline (no source change yet)**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore 2>&1 | tail -5
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~CyclicSendService" --logger "console;verbosity=minimal" 2>&1 | tail -5
```

Expected: 0 errors, CyclicSendService tests pass (baseline).

- [ ] **Step 3: Commit SPEC + PLAN**

```bash
git add docs/superpowers/specs/2026-07-13-cyclic-send-service-god-class-refactor.md
git commit -m "W34 spec: CyclicSendService god-class refactor (2 partials + 5-task roll-out, 30th overall, 8th App/Services, 24th subdirectory-pattern deployment, 2nd multi-interface partial extraction)"
git add docs/superpowers/plans/2026-07-13-cyclic-send-service-god-class-refactor.md
git commit -m "W34 plan: CyclicSendService god-class refactor (2 partials: Lifecycle + TimerTick)"
```

---

## Task T1: Lifecycle partial extraction (~89 LoC)

**Files:**
- Create: `src/PeakCan.Host.App/Services/CyclicSendService/Lifecycle.partial.cs` (~120 LoC)
- Modify: `src/PeakCan.Host.App/Services/CyclicSendService.cs` (delete L97-L130 + L133-L140 + L141-L157 + L219-L230 = 89 LoC, processed in reverse order)

**Interfaces:**
- Consumes: `CyclicSendService` partial-class visibility (private fields + ctor + properties)
- Produces: 4 methods extracted to Lifecycle.partial.cs (`Start` + `Stop` + `StopInner` + `Dispose`)

- [ ] **Step 1: Re-grep boundaries BEFORE running deletion script (W19 R1 first-correction ENHANCED pre-flight prevention)**

```bash
grep -n "public void Start\|public void Stop\|private void StopInner\|public void Dispose" src/PeakCan.Host.App/Services/CyclicSendService.cs
```

Expected:
```
99:    public void Start(CanFrame frame, TimeSpan interval)
133:    public void Stop()
141:    private void StopInner()
219:    public void Dispose()
```

- [ ] **Step 2: Verbatim re-extract 4 methods from main HEAD (W20 LESSON 44th application)**

```bash
git show main:src/PeakCan.Host.App/Services/CyclicSendService.cs | sed -n '97,130p'   # Start xmldoc + body
git show main:src/PeakCan.Host.App/Services/CyclicSendService.cs | sed -n '133,140p'   # Stop body
git show main:src/PeakCan.Host.App/Services/CyclicSendService.cs | sed -n '141,157p'   # StopInner body
git show main:src/PeakCan.Host.App/Services/CyclicSendService.cs | sed -n '219,230p'   # Dispose body
```

Expected: 34 + 8 + 17 + 12 = 71 LoC for methods + 18 LoC xmldoc = **89 LoC total**.

- [ ] **Step 3: Write Lifecycle.partial.cs (~120 LoC)**

```csharp
// CyclicSendService/Lifecycle.partial.cs — W34 T1 (Flow A, 89 LoC)
// Lifecycle methods: Start (Start timer + reset counters) + Stop + StopInner
// (private helper) + Dispose (cleanup CTS). All 4 touch _timer + _cts +
// _isRunning state. Sister of W22 RecordService Lifecycle + W23
// CyclicDbcSendService TickLifecycle + W27 RecentSessionsService
// PersistenceOps + W28 DbcService LoadLifecycle + W29 SendFrameLibrary
// PersistenceFlow + W30 SequenceSendService SendFlow + W31 ReplayService
// FileIoLifecycle + W32 DbcApi LoadFlow + W33 SequenceLibrary
// PersistenceFlow file-IO/state-management sister-pattern.
//
// Cross-partial caller pattern: Start (in Lifecycle partial) creates _timer
// via _timerFactory.CreateCyclicTimer(OnTimerTick, ...) — OnTimerTick (in
// TimerTick partial) passed as delegate. Partial-class cross-partial
// visibility handles this automatically (sister of W22+W23+W24+W25+W26+W27
// +W28+W29+W30+W31+W32+W33 cross-partial helper pattern).
//
// W23 STRUCT-FABRACTION LESSON: Interlocked.Exchange 2-arg +
// Interlocked.Read 1-arg + CancellationTokenSource 0-arg + lock(this)
// statement signatures verified during verbatim re-extraction.
//
// 4 [LoggerMessage] declarations (LogCyclicStarted + LogCyclicStopped +
// LogCyclicSendFailed + LogCyclicSendThrew) stay on CyclicSendService.cs
// per W18+W22+W23+W25+W26+W27+W28+W29+W30+W31+W32+W33+W34 sister precedent
// (CS8795 mitigation). Called from Start (this partial) + OnTimerTick (in
// TimerTick partial).
//
// W34 T1 verbatim re-extracted via `git show main:src/.../CyclicSendService.cs | sed -n '97,130p;133,140p;141,157p;219,230p'`
// per W20 T2 R1 fabrication LESSON (44th application).

using Microsoft.Extensions.Logging;

namespace PeakCan.Host.App.Services;

public sealed partial class CyclicSendService
{
    /// <summary>
    /// Start sending <paramref name="frame"/> every <paramref name="interval"/>.
    /// ... [verbatim xmldoc + body from main HEAD L97-L130]
    /// </summary>
    public void Start(CanFrame frame, TimeSpan interval)
    {
        // ... [verbatim content]
    }

    public void Stop()
    {
        // ... [verbatim content from main HEAD L133-L140]
    }

    private void StopInner()
    {
        // ... [verbatim content from main HEAD L141-L157]
    }

    public void Dispose()
    {
        // ... [verbatim content from main HEAD L219-L230]
    }
}
```

- [ ] **Step 4: Write deletion script + run + verify**

```python
# scripts/w34_task1_delete_lifecycle.py — W19 R1 first-correction ENHANCED + W17 wc-l-splitlines CONFIRMED
from pathlib import Path

p = Path("src/PeakCan.Host.App/Services/CyclicSendService.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

# W34 has 4 non-contiguous regions to delete (Dispose + StopInner + Stop + Start)
# Process in REVERSE ORDER (highest line first) to keep line numbers stable

# Verify boundaries first
print(f"Total lines: {len(lines)}")

# First pass: delete Dispose (L219-L230)
new_lines = lines[:218] + lines[230:]
# Second pass: delete StopInner (now at L141-L157 after first pass)
new_lines = new_lines[:140] + new_lines[157:]
# Third pass: delete Stop (now at L133-L140 after second pass)
new_lines = new_lines[:132] + new_lines[140:]
# Fourth pass: delete Start (now at L97-L130 after third pass)
new_lines = new_lines[:96] + new_lines[130:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

before = 243
after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"CyclicSendService.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 89 (Start 34 + Stop 8 + StopInner 17 + Dispose 12 + xmldoc 18). Within ±2 LoC tolerance.")
assert 87 <= delta <= 91, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
```

- [ ] **Step 5: Build + tests to verify T1 extraction**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore 2>&1 | tail -5
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~CyclicSendService" --logger "console;verbosity=minimal" 2>&1 | tail -5
```

Expected: 0 errors, CyclicSendService tests pass without modification.

- [ ] **Step 6: Commit T1**

```bash
git add src/PeakCan.Host.App/Services/CyclicSendService.cs src/PeakCan.Host.App/Services/CyclicSendService/Lifecycle.partial.cs scripts/w34_task1_delete_lifecycle.py
git commit -m "W34 T1: extract Lifecycle partial (Start 34 + Stop 8 + StopInner 17 + Dispose 12 LoC, -89 LoC main, 243 -> 154)"
```

---

## Task T2: TimerTick partial extraction (~61 LoC)

**Files:**
- Create: `src/PeakCan.Host.App/Services/CyclicSendService/TimerTick.partial.cs` (~80 LoC)
- Modify: `src/PeakCan.Host.App/Services/CyclicSendService.cs` (delete L158-L218 = 61 LoC, 1 contiguous region)

**Interfaces:**
- Consumes: `CyclicSendService` partial-class visibility (private fields + ctor + properties)
- Produces: 1 private async method `OnTimerTick` extracted to TimerTick.partial.cs

- [ ] **Step 1: Re-grep boundaries AFTER T1 deletion (W19 R1 first-correction ENHANCED pre-flight prevention)**

```bash
grep -n "private async void OnTimerTick\|public sealed partial class\|^}" src/PeakCan.Host.App/Services/CyclicSendService.cs
```

Expected: `OnTimerTick` + closing `}` of class.

- [ ] **Step 2: Verbatim re-extract OnTimerTick from main HEAD (W20 LESSON 45th application)**

```bash
git show main:src/PeakCan.Host.App/Services/CyclicSendService.cs | sed -n '158,218p'
```

Expected: 61 LoC.

- [ ] **Step 3: Write TimerTick.partial.cs (~80 LoC)**

```csharp
// CyclicSendService/TimerTick.partial.cs — W34 T2 (Flow B, 61 LoC)
// Private async timer-tick handler: OnTimerTick dispatches the cyclic
// frame send on each timer tick. Sister of W23 CyclicDbcSendService.
// OnTimerTick (151 LoC STAYED INLINE per W22+W23 orchestration-loop stay pattern).
//
// W25 D5 deviation NOT applied: OnTimerTick 61 LoC LARGEST method
// ≥ 60 LoC threshold BUT orchestration-loop shape (timer fires → state-machine
// dispatch loop) → fails W25 D5 deviation criteria #3 → STAYS INLINE
// per W22+W23 sister precedent.
//
// Cross-partial caller pattern: Lifecycle partial creates _timer via
// _timerFactory.CreateCyclicTimer(OnTimerTick, ...). OnTimerTick (in
// this partial) reads _frame + _sendService + _generation + _cts + _logger
// + _sendSuccessCount + _sendFailureCount. Partial-class cross-partial
// visibility handles this automatically.
//
// 4 [LoggerMessage] declarations stay on CyclicSendService.cs (CS8795
// mitigation per W18+W22+W23+W25+W26+W27+W28+W29+W30+W31+W32+W33+W34
// sister precedent). Called from OnTimerTick (this partial).
//
// W23 STRUCT-FABRACTION LESSON: Interlocked.Increment(ref long) 1-arg
// + SendAsync(CanFrame, CancellationToken) 2-arg signatures verified.
//
// W34 T2 verbatim re-extracted via `git show main:src/.../CyclicSendService.cs | sed -n '158,218p'`
// per W20 T2 R1 fabrication LESSON (45th application).

using Microsoft.Extensions.Logging;

namespace PeakCan.Host.App.Services;

public sealed partial class CyclicSendService
{
    private async void OnTimerTick(object? state)
    {
        // ... [verbatim content from main HEAD L158-L218]
    }
}
```

- [ ] **Step 4: Write deletion script + run + verify**

```python
# scripts/w34_task2_delete_timertick.py — W19 R1 first-correction ENHANCED (boundary verification + recovery procedure documented)
from pathlib import Path

p = Path("src/PeakCan.Host.App/Services/CyclicSendService.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

# Verify post-T1 boundaries first
print(f"Total lines: {len(lines)}")

START, END = 153, 213  # UPDATE per Step 1 grep result (OnTimerTick post-T1)
before = len(lines)
new_lines = lines[:START - 1] + lines[END:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"CyclicSendService.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 61 (OnTimerTick body). Within ±2 LoC tolerance.")
assert 59 <= delta <= 63, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
```

- [ ] **Step 5: Build + tests to verify T2 extraction**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore 2>&1 | tail -5
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~CyclicSendService" --logger "console;verbosity=minimal" 2>&1 | tail -5
```

Expected: 0 errors, CyclicSendService tests pass without modification.

- [ ] **Step 6: Commit T2**

```bash
git add src/PeakCan.Host.App/Services/CyclicSendService.cs src/PeakCan.Host.App/Services/CyclicSendService/TimerTick.partial.cs scripts/w34_task2_delete_timertick.py
git commit -m "W34 T2: extract TimerTick partial (OnTimerTick 61 LoC LARGEST method stays inline per W22+W23 sister orchestration-loop stay pattern, -61 LoC main, 154 -> 93)"
```

---

## Task T3: v3.47.0 → v3.48.0 MINOR + release notes

**Files:**
- Modify: `src/Directory.Build.props` (4-field version bump)
- Create: `docs/release-notes-v3.48.0.md` (~120 LoC)

- [ ] **Step 1: Bump Directory.Build.props**

```bash
python -c "
from pathlib import Path
p = Path('src/Directory.Build.props')
text = p.read_text(encoding='cp1252')
text = text.replace('<Version>3.47.0</Version>', '<Version>3.48.0</Version>', 1)
text = text.replace('<AssemblyVersion>3.47.0.0</AssemblyVersion>', '<AssemblyVersion>3.48.0.0</AssemblyVersion>', 1)
text = text.replace('<FileVersion>3.47.0.0</FileVersion>', '<FileVersion>3.48.0.0</FileVersion>', 1)
text = text.replace('<InformationalVersion>3.47.0</InformationalVersion>', '<InformationalVersion>3.48.0</InformationalVersion>', 1)
p.write_text(text, encoding='cp1252')
"
grep -E 'Version|FileVersion|InformationalVersion' src/Directory.Build.props
```

Expected: `<Version>3.48.0</Version>` + `<AssemblyVersion>3.48.0.0</AssemblyVersion>` + `<FileVersion>3.48.0.0</FileVersion>` + `<InformationalVersion>3.48.0</InformationalVersion>`.

- [ ] **Step 2: Write release notes**

Mirror W33 release-notes-v3.47.0.md format (~120 LoC).

- [ ] **Step 3: Build + full test suite to verify MINOR**

```bash
dotnet build PeakCan.Host.slnx -c Debug --no-restore 2>&1 | tail -5
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~CyclicSendService" --logger "console;verbosity=minimal" 2>&1 | tail -5
```

Expected: 0 errors, all CyclicSendService tests pass.

- [ ] **Step 4: Commit T3**

```bash
git add src/Directory.Build.props docs/release-notes-v3.48.0.md
git commit -m "W34 T3: v3.47.0 -> v3.48.0 MINOR (CyclicSendService god-class refactor, 30th overall, 8th App/Services, 24th subdirectory-pattern deployment, -150 LoC -61.7% main)"
```

---

## Task T4: Tier-3 ship (PR + squash + tag + GH release)

- [ ] **Step 1: Push branch + create PR**

```bash
git push -u origin feature/w34-cyclic-send-service-god-class
gh pr create --base main --head feature/w34-cyclic-send-service-god-class --title "W34 MINOR: CyclicSendService god-class refactor (30th overall, 2nd multi-interface partial extraction, -150 LoC -61.7% main)" --body "[full PR body per W33 PR #67 sister precedent]"
```

- [ ] **Step 2: Wait for CI + squash-merge + tag + GH release**

```bash
gh pr checks <PR-number> --watch  # wait for CI (5-attempt MAX per W23.5-W33 sister pattern)
gh pr merge <PR-number> --squash --delete-branch
git tag -a v3.48.0 -m "v3.48.0 MINOR: CyclicSendService god-class refactor (30th overall, 8th App/Services, 2nd multi-interface partial extraction, -150 LoC -61.7% main, 2-partial design Lifecycle+TimerTick, multi-interface ICyclicSendService+IDisposable, OnTimerTick 61 LoC stays inline per W22+W23 sister orchestration-loop stay pattern)" <squash-commit-sha>
git push origin v3.48.0
gh release create v3.48.0 --title "v3.48.0 — CyclicSendService god-class refactor" --notes "[release notes body]"
```

---

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings
- `dotnet test --filter "FullyQualifiedName~CyclicSendService"`: tests pass without modification
- `dotnet test` (full solution): 0 new fails
- `wc -l src/PeakCan.Host.App/Services/CyclicSendService.cs` ≤ 105 LoC (target ~93)
- 2 NEW partial files in `CyclicSendService/` directory
- 11 fields + 3 delegating properties + 2 ctors + 4 `[LoggerMessage]` partials remain in main
- Tag v3.48.0 + GH release published
- Branch deleted post-merge

## Out of scope (YAGNI)

- No public/internal API change.
- No test changes.
- No facade pattern (W3-W33 CONFIRMED direct partial-class visibility).
- No xmldoc-grep risk (verified — tests do not path-grep main file content).
- No W18 R1 mitigation needed (D4 keeps all 4 `[LoggerMessage]` partials on main partial declaration).
- No `ICyclicSendService.cs` interface partial changes (stays in Core layer).
- No `SendService.cs` partial changes (consumed by CyclicSendService but not modified).
- No `ITimerFactory.cs` or `ICyclicTimer.cs` partial changes.
