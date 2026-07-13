# W27 PLAN — RecentSessionsService god-class refactor (23rd overall)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract 3 NEW partial-class files from `src/PeakCan.Host.App/Services/Trace/RecentSessionsService.cs` (334 → ~117 LoC) in line with the W3-W26 god-class refactor series.

**Architecture:** Sister pattern of W22 RecordService (App/Services JSON persistence) + W23 CyclicDbcSendService (App/Services). 23rd god-class refactor. 3rd App/Services layer + 17th subdirectory-pattern deployment. Order largest-first: A (PersistenceOps, 87 LoC) → B (Mutators, 98 LoC) → C (StaticHelpers, 32 LoC).

**Tech Stack:** C# .NET 10 partial-class split pattern (W3-W26 series), `System.Text.Json` for JSON persistence, `System.ComponentModel.INotifyPropertyChanged` for WPF binding.

## Global Constraints

- **LoC formula**: W8.5 D7 ±2 LoC tolerance at each task boundary (W13 T1 2/3 loose-assertion sister). Re-grep + range verify after each task per W19 R1 first-correction.
- **Verbatim re-extraction**: W20 T2 R1 fabrication LESSON — every partial's content MUST come from `git show HEAD:src/...cs | sed -n '<range>p'`. NEVER hand-reconstruct method bodies.
- **Struct-ctor verification**: W23 LESSON — verify `JsonSerializer.Serialize<T>(T value, JsonSerializerOptions?)` 2-arg + `JsonSerializer.Deserialize<T>(string, JsonSerializerOptions?)` 2-arg + `File.WriteAllText(string, string?, Encoding)` 3-arg + `File.Delete(string)` + `File.Move(string, string)` + `Environment.GetFolderPath(SpecialFolder.ApplicationData)` 1-arg signatures.
- **No `[LoggerMessage]` partials** in RecentSessionsService (zero occurrences).
- **Branch name**: `feature/w27-recent-sessions-service-god-class`.
- **Target version**: v3.41.0 MINOR.

---

## File structure

- NEW: `src/PeakCan.Host.App/Services/Trace/RecentSessionsService/PersistenceOps.partial.cs` (T1, ~87 LoC)
- NEW: `src/PeakCan.Host.App/Services/Trace/RecentSessionsService/Mutators.partial.cs` (T2, ~98 LoC)
- NEW: `src/PeakCan.Host.App/Services/Trace/RecentSessionsService/StaticHelpers.partial.cs` (T3, ~32 LoC)
- MODIFY: `src/PeakCan.Host.App/Services/Trace/RecentSessionsService.cs` (T1+T2+T3 deletions + final state ~117 LoC)
- MODIFY: `src/Directory.Build.props` (v3.40.5 → v3.41.0, T4)
- NEW: `docs/release-notes-v3.41.0.md` (T4, ~110 LoC)
- NEW: `scripts/w27_task1_delete_persistenceops.py`
- NEW: `scripts/w27_task2_delete_mutators.py`
- NEW: `scripts/w27_task3_delete_statichelpers.py`
- NEW: `docs/superpowers/capture-decisions/2026-07-13-w27-recent-sessions-service-god-class-ship.md` (post-PR docs commit)

---

## Task 1: W27 T0.5 — Branch + baseline verification

**Files**: SPEC already on branch + SPEC commit `67b9d62`. PLAN = this file.

- [ ] **Step 1: Verify baseline build + filter tests**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~RecentSessions|FullyQualifiedName~Trace" --logger "console;verbosity=minimal"
```

Expected: 0 build errors. Filter tests pass.

- [ ] **Step 2: Commit PLAN**

```bash
git add docs/superpowers/plans/2026-07-12-recent-sessions-service-god-class-refactor.md
git commit -m "W27 plan: RecentSessionsService god-class refactor (3 partials: PersistenceOps + Mutators + StaticHelpers, A->B->C order, W20+W23 LESSONS)"
```

---

## Task 2: W27 T1 — Extract PersistenceOps partial (~87 LoC, LARGEST)

**Files:**
- NEW: `src/PeakCan.Host.App/Services/Trace/RecentSessionsService/PersistenceOps.partial.cs`
- MODIFY: `src/PeakCan.Host.App/Services/Trace/RecentSessionsService.cs` (delete `LoadAsync` + `Persist` + `Raise` methods, currently at L215-L301 with xmldoc/blank lines)

**Interfaces:**
- Consumes: `RecentSessionsService.cs` at lines 215-301 from HEAD.
- Produces: `RecentSessionsService/PersistenceOps.partial.cs` containing `partial class RecentSessionsService` with `LoadAsync` + `Persist` + `Raise` methods.

- [ ] **Step 1: Re-grep boundaries of PersistenceOps region**

```bash
grep -n "public Task LoadAsync\|private void Persist\|private void Raise" src/PeakCan.Host.App/Services/Trace/RecentSessionsService.cs
```

Expected: LoadAsync L215, Persist L276, Raise L300. End at L301 (closing `}` of Raise).

- [ ] **Step 2: Re-extract verbatim from HEAD**

```bash
START_LINE=215  # update from Step 1
END_LINE=301    # update from Step 1
git show HEAD:src/PeakCan.Host.App/Services/Trace/RecentSessionsService.cs | sed -n "${START_LINE},${END_LINE}p"
```

Expected: exact verbatim PersistenceOps region.

- [ ] **Step 3: Write PersistenceOps.partial.cs**

```csharp
// RecentSessionsService/PersistenceOps.partial.cs — W27 T1 (Flow A, LARGEST 87 LoC)
// File I/O lifecycle: LoadAsync (async load from JSON file) +
// Persist (atomic temp+rename save) + Raise (PropertyChanged
// trigger). Sister of W22 RecordService.Lifecycle partial.
//
// LoadAsync 60 LoC LARGEST method moves here per W25 D5 deviation
// (file-I/O lifecycle = sharp discrete flow, sister of W25
// OnChannelFrame + W26 OnFrame(CanFrame) 2 prior moves).
//
// Zero [LoggerMessage] partials in RecentSessionsService — W22 +
// W23 + W24 + W25 + W26 + W27 23rd cumulative W20 LESSON
// application. W23 STRUCT-FABRICATION LESSON: verify
// JsonSerializer.Serialize/Deserialize 2-arg +
// File.WriteAllText 3-arg + File.Delete/Move 1-arg +
// Environment.GetFolderPath 1-arg signatures.

using System.Text;
using System.Text.Json;

namespace PeakCan.Host.App.Services.Trace;

public sealed partial class RecentSessionsService
{
    // <verbatim LoadAsync + Persist + Raise bodies from Step 2, including xmldoc>
}
```

- [ ] **Step 4: Write `scripts/w27_task1_delete_persistenceops.py`** with W13 T1 2/3 loose assertion + W17 wc-l-splitlines CONFIRMED + W25 cp1252 binary pattern.

```python
"""W27 T1 deletion script — remove PersistenceOps region (L215-301) from RecentSessionsService.cs.

Per W19 R1 first-correction: re-grep boundaries BEFORE running this script.
Per W20 T2 R1 fabrication LESSON: verbatim re-extract from HEAD before deleting.
Per W17 wc-l-splitlines CONFIRMED + W25 cp1252 binary: use binary read+write with cp1252.
"""
from pathlib import Path

p = Path("src/PeakCan.Host.App/Services/Trace/RecentSessionsService.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

START, END = 215, 301  # UPDATE per Step 1 grep result
before = len(lines)
new_lines = lines[:START - 1] + lines[END:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"RecentSessionsService.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: ~87 (Flow A PersistenceOps LoadAsync+Persist+Raise range L215-L301). Within +/-2 LoC tolerance.")
assert 85 <= delta <= 89, f"FAIL: delta {delta} outside +/-2 tolerance"
print("PASS.")
```

- [ ] **Step 5: Run deletion script**

```bash
python scripts/w27_task1_delete_persistenceops.py
wc -l src/PeakCan.Host.App/Services/Trace/RecentSessionsService.cs
```

Expected: main file = 247 LoC ±2 (delta = 87).

- [ ] **Step 6: Build + filter test**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~RecentSessions|FullyQualifiedName~Trace"
```

Expected: 0 errors, all filter tests pass. W20 LESSON APPLIED 27th time. Add using directives as needed.

- [ ] **Step 7: Commit T1**

```bash
git add src/PeakCan.Host.App/Services/Trace/RecentSessionsService/PersistenceOps.partial.cs
git add src/PeakCan.Host.App/Services/Trace/RecentSessionsService.cs
git add scripts/w27_task1_delete_persistenceops.py
git commit -m "W27 T1: extract PersistenceOps partial (LoadAsync 60 LoC LARGEST + Persist + Raise, 334->247 main)"
```

---

## Task 3: W27 T2 — Extract Mutators partial (~98 LoC)

**Files:**
- NEW: `src/PeakCan.Host.App/Services/Trace/RecentSessionsService/Mutators.partial.cs`
- MODIFY: `src/PeakCan.Host.App/Services/Trace/RecentSessionsService.cs` (delete 4 mutator methods + xmldoc, currently at ~L108-L211)

- [ ] **Step 1: Re-grep boundaries**

```bash
grep -n "public void Add\|public void Clear" src/PeakCan.Host.App/Services/Trace/RecentSessionsService.cs
```

Expected: starts at first Add xmldoc (~L108), ends at closing `}` after `Clear(string?)` (~L211).

- [ ] **Step 2: Re-extract verbatim from HEAD** + write partial + deletion script
- [ ] **Step 3: Run deletion**: expected main file ≈ 149 LoC ±2 (delta ≈ 98)
- [ ] **Step 4: Build + filter test** (W20 LESSON APPLIED 28th time)
- [ ] **Step 5: Commit T2**

```bash
git add src/PeakCan.Host.App/Services/Trace/RecentSessionsService/Mutators.partial.cs
git add src/PeakCan.Host.App/Services/Trace/RecentSessionsService.cs
git commit -m "W27 T2: extract Mutators partial (Add+Clear x2 + Raise calls, 247->149 main)"
```

---

## Task 4: W27 T3 — Extract StaticHelpers partial (~32 LoC)

**Files:**
- NEW: `src/PeakCan.Host.App/Services/Trace/RecentSessionsService/StaticHelpers.partial.cs`
- MODIFY: `src/PeakCan.Host.App/Services/Trace/RecentSessionsService.cs` (delete `DefaultPath` + `Envelope` inner class, currently at ~L303-end)

- [ ] **Step 1: Re-grep boundaries**

```bash
grep -n "private static string DefaultPath\|public sealed class Envelope" src/PeakCan.Host.App/Services/Trace/RecentSessionsService.cs
```

Expected: starts at `DefaultPath` (~L303), ends at closing `}` of `Envelope` class.

- [ ] **Step 2: Re-extract verbatim from HEAD** + write partial + deletion script
- [ ] **Step 3: Run deletion**: expected main file ≈ 117 LoC ±2 (delta ≈ 32)
- [ ] **Step 4: Build + filter test** (W20 LESSON APPLIED 29th time)
- [ ] **Step 5: Commit T3**

```bash
git add src/PeakCan.Host.App/Services/Trace/RecentSessionsService/StaticHelpers.partial.cs
git add src/PeakCan.Host.App/Services/Trace/RecentSessionsService.cs
git commit -m "W27 T3: extract StaticHelpers partial (DefaultPath + Envelope inner class, 149->117 main)"
```

---

## Task 5: W27 T4 — Version bump + release notes

**Files:**
- MODIFY: `src/Directory.Build.props` (v3.40.5 → v3.41.0)
- NEW: `docs/release-notes-v3.41.0.md`

- [ ] **Step 1: Bump version**

```bash
sed -i 's|<Version>3.40.5</Version>|<Version>3.41.0</Version>|; s|<AssemblyVersion>3.40.5.0</AssemblyVersion>|<AssemblyVersion>3.41.0.0</AssemblyVersion>|; s|<FileVersion>3.40.5.0</FileVersion>|<FileVersion>3.41.0.0</FileVersion>|; s|<InformationalVersion>3.40.5</InformationalVersion>|<InformationalVersion>3.41.0</InformationalVersion>|' src/Directory.Build.props
```

- [ ] **Step 2: Write `docs/release-notes-v3.41.0.md`** with W3-W27 cumulative god-class trajectory.
- [ ] **Step 3: Commit T4**

```bash
git add src/Directory.Build.props
git add docs/release-notes-v3.41.0.md
git commit -m "W27 T4: v3.40.5 -> v3.41.0 MINOR + release notes"
```

---

## Task 6: W27 T5 — Tier-3 ship

- [ ] **Step 1: Push branch + create PR**

```bash
git push -u origin feature/w27-recent-sessions-service-god-class
gh pr create --base main --head feature/w27-recent-sessions-service-god-class --title "W27 MINOR: RecentSessionsService god-class refactor (23rd overall, 3rd App/Services, -217 LoC main)" --body "Sister of W22 RecordService + W23 CyclicDbcSendService (App/Services JSON persistence). 23rd god-class refactor in W3-W27 series. 3 NEW partials in RecentSessionsService/ subdirectory: PersistenceOps (LoadAsync 60 LoC LARGEST + Persist + Raise) + Mutators (Add+Clear x2) + StaticHelpers (DefaultPath + Envelope inner class). Zero [LoggerMessage] partials in source. Inner Envelope class + RecentSessionDto record stay in main per W21+W24+W26 sister precedent. Tests pass without modification."
```

- [ ] **Step 2: Wait for CI**

```bash
gh pr checks --watch
```

Expected: 1+ retries possible due to pre-existing transient flaky `ReplayServiceTests.SetSpeed_PreservesCurrentTimestamp` — keep re-running until SUCCESS.

- [ ] **Step 3: Squash-merge + delete branch**

```bash
gh pr merge --squash --delete-branch
```

- [ ] **Step 4: Tag + GH release**

```bash
git checkout main
git pull
git tag -a v3.41.0 -m "W27 MINOR - RecentSessionsService god-class refactor (23rd overall, 3rd App/Services, -217 LoC main, 3 NEW partials)"
git push origin v3.41.0
gh release create v3.41.0 --target main --title "v3.41.0 MINOR - RecentSessionsService god-class refactor" --notes-file docs/release-notes-v3.41.0.md
```

- [ ] **Step 5: Post-PR docs commit**

Per W19-W26 D6 sister pattern: write `docs/superpowers/capture-decisions/2026-07-13-w27-recent-sessions-service-god-class-ship.md` and commit it to main as a separate post-PR docs commit.

---

## Verification matrix

| Check | Expected | Sister |
|---|---|---|
| `dotnet build src/PeakCan.Host.App/` | 0 errors, 0 warnings | W20+W22+W26 |
| `dotnet test --filter "~RecentSessions|~Trace"` | All pass | W20+W24 |
| `dotnet test` (full solution) | 0 new fails | W13 T1 sister |
| `wc -l src/.../RecentSessionsService.cs` | ≤ 130 LoC (target ~117) | W8.5 D7 ±2 |
| 3 NEW partial files in `RecentSessionsService/` | YES | W12-W26 |
| 3 const + 3 readonly + 1 Recent getter + 1 PropertyChanged + 2 ctors + Envelope inner class stay in main | YES | W21+W24+W26 sister |
| DI registration unchanged | YES | W12-W26 |
| INotifyPropertyChanged unchanged | YES | W12-W26 |
| Tag v3.41.0 + GH release published | YES | W12-W26 |
| Branch deleted post-merge | YES | W12-W26 |
| capture-decisions file landing on main | YES | W19-W26 |

## Out of scope (YAGNI)

- No public/internal API change.
- No test changes.
- No facade pattern.
- No xmldoc-grep risk.
- No `[LoggerMessage]` partials (zero in source).
- No `Envelope` inner-class relocation.
- No `RecentSessionDto` inner-record relocation.
