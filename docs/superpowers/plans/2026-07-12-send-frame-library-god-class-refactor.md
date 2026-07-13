# W29 PLAN — SendFrameLibrary god-class refactor (25th overall)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract 3 NEW partial-class files from `src/PeakCan.Host.App/Services/SendFrameLibrary.cs` (276 → ~131 LoC) in line with the W3-W28 god-class refactor series.

**Architecture:** Sister pattern of W22 RecordService + W27 RecentSessionsService + W28 DbcService (App/Services JSON-persistence + atomic temp-rename Persist + lock-protected Mutator). 25th god-class refactor. 5th App/Services layer + 19th subdirectory-pattern deployment. Order largest-first per flow-clarity: A (PersistenceFlow, ~50 LoC) → B (Mutators, ~85 LoC, LARGEST cluster) → C (StaticHelpers, ~10 LoC).

**Tech Stack:** C# .NET 10 partial-class split pattern (W3-W28 series), `System.Text.Json` for JSON persistence + `JsonSerializerOptions` WriteIndented, `System.IO` for atomic tmp+rename file ops, `Microsoft.Extensions.Logging` + 2 `[LoggerMessage]` partials.

## Global Constraints

- **LoC formula**: W8.5 D7 ±2 LoC tolerance (W13 T1 2/3 loose-assertion sister). Re-grep + range verify after each task per W19 R1 first-correction.
- **Verbatim re-extraction**: W20 T2 R1 fabrication LESSON — every partial's content MUST come from `git show HEAD:src/...cs | sed -n '<range>p'`. NEVER hand-reconstruct method bodies.
- **Struct-ctor verification**: W23 LESSON — verify `JsonSerializer.Serialize<T>(T, JsonSerializerOptions?)` 2-arg + `JsonSerializer.Deserialize<T>(string, JsonSerializerOptions?)` 2-arg + `File.WriteAllText(string, string?, Encoding)` 3-arg + `File.Move(string, string, bool)` 3-arg overload + `File.Delete` + `File.Exists` + `File.ReadAllText` + `Interlocked.Increment(ref int)` 1-arg + `Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)` 1-arg signatures.
- **No `[LoggerMessage]` partial moves** (D4 sister): 2 `LogCorrupt` + `LogSaveUnlockedFailed` declarations stay on main.
- **Branch name**: `feature/w29-send-frame-library-god-class`.
- **Target version**: v3.43.0 MINOR.
- **No LARGEST method move** (D5 default): `SaveUnlocked` 24 LoC LARGEST is too small for W25 D5 deviation → all methods stay inline OR extract per flow-boundary (per W12-W14-W18-W19-W20-W21-W22-W23 D5 default sister).

---

## File structure

- NEW: `src/PeakCan.Host.App/Services/SendFrameLibrary/PersistenceFlow.partial.cs` (T1, ~50 LoC)
- NEW: `src/PeakCan.Host.App/Services/SendFrameLibrary/Mutators.partial.cs` (T2, ~85 LoC)
- NEW: `src/PeakCan.Host.App/Services/SendFrameLibrary/StaticHelpers.partial.cs` (T3, ~10 LoC)
- MODIFY: `src/PeakCan.Host.App/Services/SendFrameLibrary.cs` (T1+T2+T3 deletions + final state ~131 LoC)
- MODIFY: `src/Directory.Build.props` (v3.42.5 → v3.43.0, T4)
- NEW: `docs/release-notes-v3.43.0.md` (T4)
- NEW: `scripts/w29_task1_delete_persistenceflow.py`
- NEW: `scripts/w29_task2_delete_mutators.py`
- NEW: `scripts/w29_task3_delete_statichelpers.py`
- NEW: `docs/superpowers/capture-decisions/2026-07-13-w29-send-frame-library-god-class-ship.md` (post-PR docs commit)

---

## Task 1: W29 T0.5 — Branch + baseline verification

**Files**: SPEC already on branch + SPEC commit `22475b0`. PLAN = this file.

- [ ] **Step 1: Verify baseline build + filter tests**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~SendFrameLibrary|FullyQualifiedName~Send" --logger "console;verbosity=minimal"
```

Expected: 0 build errors. Filter tests pass.

- [ ] **Step 2: Commit PLAN**

```bash
git add docs/superpowers/plans/2026-07-12-send-frame-library-god-class-refactor.md
git commit -m "W29 plan: SendFrameLibrary god-class refactor (3 partials: PersistenceFlow + Mutators + StaticHelpers, A->B->C order)"
```

---

## Task 2: W29 T1 — Extract PersistenceFlow partial (~50 LoC)

**Files:**
- NEW: `src/PeakCan.Host.App/Services/SendFrameLibrary/PersistenceFlow.partial.cs`
- MODIFY: `src/PeakCan.Host.App/Services/SendFrameLibrary.cs` (delete `EnsureLoaded` + `LoadUnlocked` + `SaveUnlocked` private helpers, currently at L211-L264)

**Interfaces:**
- Consumes: `SendFrameLibrary.cs` at lines 211-264 from HEAD.
- Produces: `SendFrameLibrary/PersistenceFlow.partial.cs` containing `partial class SendFrameLibrary` with 3 private I/O helper methods.

- [ ] **Step 1: Re-grep boundaries of PersistenceFlow region**

```bash
grep -n "private void EnsureLoaded\|private IReadOnlyList\|private void SaveUnlocked" src/PeakCan.Host.App/Services/SendFrameLibrary.cs
```

Expected: starts at `EnsureLoaded` (~L211), ends at closing `}` of `SaveUnlocked` (~L264).

- [ ] **Step 2: Re-extract verbatim from HEAD**

```bash
START_LINE=211  # update from Step 1
END_LINE=264    # update from Step 1
git show HEAD:src/PeakCan.Host.App/Services/SendFrameLibrary.cs | sed -n "${START_LINE},${END_LINE}p"
```

Expected: exact verbatim PersistenceFlow region (3 private helper methods).

- [ ] **Step 3: Write PersistenceFlow.partial.cs**

```csharp
// SendFrameLibrary/PersistenceFlow.partial.cs — W29 T1 (Flow A)
// Private file-IO lifecycle helpers: EnsureLoaded (cache-init
// sentinel) + LoadUnlocked (JSON deserialize with corrupt-fallback)
// + SaveUnlocked (atomic tmp+rename with IOException cleanup).
// Sister of W22 RecordService/Lifecycle + W27 RecentSessionsService/
// PersistenceOps + W28 DbcService/LoadLifecycle private-helpers-in-
// partial sister-pattern.
//
// 2 [LoggerMessage] declarations (LogCorrupt + LogSaveUnlockedFailed)
// stay on SendFrameLibrary.cs per W18+W22+W23+W25+W26+W27+W28
// sister precedent (CS8795 mitigation).
//
// W23 STRUCT-FABRICATION LESSON: verify JsonSerializer.Serialize/
// Deserialize 2-arg + File.WriteAllText 3-arg + File.Move 3-arg
// + Interlocked.Increment 1-arg signatures.
//
// W29 T1 verbatim re-extracted via `git show HEAD:src/...cs | sed -n '211,264p'`
// per W20 T2 R1 fabrication LESSON (32nd application).

using System.Text.Json;

namespace PeakCan.Host.App.Services;

public sealed partial class SendFrameLibrary
{
    // <verbatim EnsureLoaded + LoadUnlocked + SaveUnlocked bodies from Step 2, including xmldoc>
}
```

- [ ] **Step 4: Write `scripts/w29_task1_delete_persistenceflow.py`**

```python
"""W29 T1 deletion script — remove PersistenceFlow region (L211-264) from SendFrameLibrary.cs.

Per W19 R1 first-correction: re-grep boundaries BEFORE running this script.
Per W20 T2 R1 fabrication LESSON: verbatim re-extract from HEAD before deleting.
Per W17 wc-l-splitlines CONFIRMED + W25/W27/W28 cp1252 binary: use binary read+write with cp1252.

W13 T1 2/3 loose-assertion: predicted -54 LoC (range L211-L264 inclusive = 54 lines
including 3 method bodies with xmldoc).
Within ±2 LoC tolerance.
"""
from pathlib import Path

p = Path("src/PeakCan.Host.App/Services/SendFrameLibrary.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

START, END = 211, 264  # UPDATE per Step 1 grep result
before = len(lines)
new_lines = lines[:START - 1] + lines[END:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"SendFrameLibrary.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 54 (Flow A PersistenceFlow EnsureLoaded+LoadUnlocked+SaveUnlocked range L211-L264). Within ±2 LoC tolerance.")
assert 52 <= delta <= 56, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
```

- [ ] **Step 5: Run deletion script**

```bash
python scripts/w29_task1_delete_persistenceflow.py
wc -l src/PeakCan.Host.App/Services/SendFrameLibrary.cs
```

Expected: main file = 222 LoC ±2 (delta = 54).

- [ ] **Step 6: Build + filter test**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~SendFrameLibrary|FullyQualifiedName~Send"
```

Expected: 0 errors, all filter tests pass. W20 LESSON APPLIED 32nd time. Add using directives as needed.

- [ ] **Step 7: Commit T1**

```bash
git add src/PeakCan.Host.App/Services/SendFrameLibrary/PersistenceFlow.partial.cs
git add src/PeakCan.Host.App/Services/SendFrameLibrary.cs
git add scripts/w29_task1_delete_persistenceflow.py
git commit -m "W29 T1: extract PersistenceFlow partial (EnsureLoaded + LoadUnlocked + SaveUnlocked, 276->222 main)"
```

---

## Task 3: W29 T2 — Extract Mutators partial (~85 LoC, LARGEST cluster)

**Files:**
- NEW: `src/PeakCan.Host.App/Services/SendFrameLibrary/Mutators.partial.cs`
- MODIFY: `src/PeakCan.Host.App/Services/SendFrameLibrary.cs` (delete 6 lock-gated mutator methods + Count getter, currently at ~L106-L209)

- [ ] **Step 1: Re-grep boundaries**

```bash
grep -n "public IReadOnlyList\|public void Save\|public int Add\|public bool Remove\|public int Count" src/PeakCan.Host.App/Services/SendFrameLibrary.cs
```

Expected: starts at `public IReadOnlyList Load` xmldoc (~L106), ends at closing `}` of `Count` getter (~L209).

- [ ] **Step 2: Re-extract verbatim from HEAD** + write partial + deletion script
- [ ] **Step 3: Run deletion**: expected main file ≈ 137 LoC ±2 (delta ≈ 85)
- [ ] **Step 4: Build + filter test** (W20 LESSON APPLIED 33rd time)
- [ ] **Step 5: Commit T2**

```bash
git add src/PeakCan.Host.App/Services/SendFrameLibrary/Mutators.partial.cs
git add src/PeakCan.Host.App/Services/SendFrameLibrary.cs
git commit -m "W29 T2: extract Mutators partial (Load + Save x2 + Add + Remove + Count, 222->137 main)"
```

---

## Task 4: W29 T3 — Extract StaticHelpers partial (~10 LoC)

**Files:**
- NEW: `src/PeakCan.Host.App/Services/SendFrameLibrary/StaticHelpers.partial.cs`
- MODIFY: `src/PeakCan.Host.App/Services/SendFrameLibrary.cs` (delete `DefaultPath` static helper, currently at ~L266-L270)

- [ ] **Step 1: Re-grep boundaries**

```bash
grep -n "private static string DefaultPath" src/PeakCan.Host.App/Services/SendFrameLibrary.cs
```

Expected: starts at `DefaultPath` (~L266), ends at closing `}` (~L270).

- [ ] **Step 2: Re-extract verbatim from HEAD** + write partial + deletion script
- [ ] **Step 3: Run deletion**: expected main file ≈ 131 LoC ±2 (delta ≈ 10)
- [ ] **Step 4: Build + filter test** (W20 LESSON APPLIED 34th time)
- [ ] **Step 5: Commit T3**

```bash
git add src/PeakCan.Host.App/Services/SendFrameLibrary/StaticHelpers.partial.cs
git add src/PeakCan.Host.App/Services/SendFrameLibrary.cs
git commit -m "W29 T3: extract StaticHelpers partial (DefaultPath, 137->131 main)"
```

---

## Task 5: W29 T4 — Version bump + release notes

**Files:**
- MODIFY: `src/Directory.Build.props` (v3.42.5 → v3.43.0)
- NEW: `docs/release-notes-v3.43.0.md`

- [ ] **Step 1: Bump version**

```bash
sed -i 's|<Version>3.42.5</Version>|<Version>3.43.0</Version>|; s|<AssemblyVersion>3.42.5.0</AssemblyVersion>|<AssemblyVersion>3.43.0.0</AssemblyVersion>|; s|<FileVersion>3.42.5.0</FileVersion>|<FileVersion>3.43.0.0</FileVersion>|; s|<InformationalVersion>3.42.5</InformationalVersion>|<InformationalVersion>3.43.0</InformationalVersion>|' src/Directory.Build.props
```

- [ ] **Step 2: Write `docs/release-notes-v3.43.0.md`** with W3-W29 cumulative god-class trajectory + 2 NEW lesson promotions (small-god-class-no-largest-method 1/3 + app-services-json-persistence 3/3 CONFIRMED LOCK).
- [ ] **Step 3: Commit T4**

```bash
git add src/Directory.Build.props
git add docs/release-notes-v3.43.0.md
git commit -m "W29 T4: v3.42.5 -> v3.43.0 MINOR + release notes (25th god-class + 2 lesson promotions: small-god-class-no-largest-method NEW 1/3 + app-services-json-persistence 3/3 CONFIRMED LOCK)"
```

---

## Task 6: W29 T5 — Tier-3 ship

- [ ] **Step 1: Push branch + create PR**

```bash
git push -u origin feature/w29-send-frame-library-god-class
gh pr create --base main --head feature/w29-send-frame-library-god-class --title "W29 MINOR: SendFrameLibrary god-class refactor (25th overall, 5th App/Services, -145 LoC main, 3rd confirmation app-services-json-persistence 3/3 CONFIRMED LOCK)" --body "Sister of W22 RecordService + W27 RecentSessionsService (App/Services JSON-persistence + lock-protected Mutator pattern). 25th god-class refactor. 3 NEW partials in SendFrameLibrary/ subdirectory: PersistenceFlow (EnsureLoaded + LoadUnlocked + SaveUnlocked) + Mutators (Load + Save x2 + Add + Remove + Count) + StaticHelpers (DefaultPath). 2 [LoggerMessage] partials stay on main per D4 sister. Tests pass without modification. Small god-class (LARGEST method 24 LoC SaveUnlocked) → no W25 D5 deviation applied; all methods stay inline OR extract per flow-boundary clarity."
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
git tag -a v3.43.0 -m "W29 MINOR - SendFrameLibrary god-class refactor (25th overall, 5th App/Services, -145 LoC main, 3 NEW partials, 2 lesson promotions)"
git push origin v3.43.0
gh release create v3.43.0 --target main --title "v3.43.0 MINOR - SendFrameLibrary god-class refactor (25th overall, 5th App/Services)" --notes-file docs/release-notes-v3.43.0.md
```

- [ ] **Step 5: Post-PR docs commit**

Per W19-W28 D6 sister pattern: write `docs/superpowers/capture-decisions/2026-07-13-w29-send-frame-library-god-class-ship.md` and commit it to main as a separate post-PR docs commit.

---

## Verification matrix

| Check | Expected | Sister |
|---|---|---|
| `dotnet build src/PeakCan.Host.App/` | 0 errors, 0 warnings | W20+W22+W27+W28 |
| `dotnet test --filter "~SendFrameLibrary|~Send"` | All pass | W20+W27 |
| `dotnet test` (full solution) | 0 new fails | W13 T1 sister |
| `wc -l src/.../SendFrameLibrary.cs` | ≤ 145 LoC (target ~131) | W8.5 D7 ±2 |
| 3 NEW partial files in `SendFrameLibrary/` | YES | W12-W28 |
| 5 fields + 2 test hooks + 1 ctor + 1 SavedFrame record + 1 LibraryFile inner class + 2 [LoggerMessage] partials stay in main | YES | W21+W24+W26+W27+W28 |
| DI registration unchanged | YES | W12-W28 |
| 6 lock-gated Mutator methods + Count getter preserved | YES | W22+W27 sister |
| Tag v3.43.0 + GH release published | YES | W12-W28 |
| Branch deleted post-merge | YES | W12-W28 |
| capture-decisions file landing on main | YES | W19-W28 |

## Out of scope (YAGNI)

- No public/internal API change.
- No test changes.
- No facade pattern.
- No xmldoc-grep risk.
- No `[LoggerMessage]` partial relocation (D4 keeps all 2 on main).
- No `SavedFrame`/`LibraryFile` inner-class relocation.
- No `lock` removal.
- No `Interlocked.Increment` test-counter signature change.
