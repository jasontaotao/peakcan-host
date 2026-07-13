# W33 Implementation Plan — SequenceLibrary god-class refactor (29th overall, 1st App/Services/Sequence)

**Goal**: Extract 3 NEW partial-class files (PersistenceFlow + Mutators + StaticHelpers) from `src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs` 244 → ~123 LoC (-121 LoC, -49.6%). Public API + existing tests unchanged.

**Architecture**: Sister pattern of W22 + W23 + W27 + W28 + W29 + W30 + W31 + W32 (subdirectory + non-suffix `.partial.cs` filenames). 29th god-class refactor. **7th App/Services layer** (sister of W22 + W23 + W27 + W28 + W29 + W30 + W31 + W32) + **1st App/Services/Sequence subdirectory** (NEW layer discovered) + **23rd subdirectory-pattern deployment**.

**Tech Stack**: C# .NET 10 partial-class split pattern + W29 NEW `small-god-class-no-largest-method-keeps-all-inline-default-pattern` (D5 default sister-principle, NO W25 D5 deviation since LARGEST method `SaveUnlocked` 21 LoC < 50 LoC threshold) + W19 R1 first-correction ENHANCED (pre-flight prevention + post-failure recovery) + W20 verbatim re-extraction + W23 STRUCT-FABRACTION LESSON

**Spec**: [2026-07-13-sequence-library-god-class-refactor.md](../specs/2026-07-13-sequence-library-god-class-refactor.md)

---

## Global Constraints

- 1 partial class with 3 partials in subdirectory `src/PeakCan.Host.App/Services/Sequence/SequenceLibrary/`
- `public sealed partial class SequenceLibrary` (already partial at L26; no D2 application needed per W21 + W26.5 + W30 + W31 + W32 sister precedent)
- All public API surface unchanged (DI registration + tests + nested types)
- LoC formula `main_after = main_before - delete_count` (W8.5 D7 32-locked, no +1 offset)
- W17 wc-l-splitlines CONFIRMED + cp1252 binary read+write pattern (since file is Windows-1252 encoded)
- W19 R1 first-correction ENHANCED: re-grep boundaries BEFORE each deletion script + post-failure recovery procedure (git checkout + re-grep + corrected offsets + re-run)
- W20 LESSON: verbatim re-extraction via `git show main:src/.../SequenceLibrary.cs | sed -n '<range>p'`
- W23 STRUCT-FABRICATION LESSON: verify `JsonSerializer.Serialize` 2-arg + `JsonSerializer.Deserialize` 2-arg + `File.WriteAllText` 3-arg + `File.Move` 3-arg overload + `JsonPropertyName` attribute signatures
- W29 NEW `small-god-class-no-largest-method-keeps-all-inline-default-pattern`: LARGEST method 21 LoC < 50 LoC threshold → default D5 sister-principle applied (NO W25 D5 deviation)

---

## Task T0: Branch + SPEC + PLAN commits

**Files:**
- Create: `docs/superpowers/specs/2026-07-13-sequence-library-god-class-refactor.md`
- Create: `docs/superpowers/plans/2026-07-13-sequence-library-god-class-refactor.md`

**Interfaces:**
- Produces: SPEC + PLAN committed to feature branch

- [ ] **Step 1: Branch + verify already partial**

```bash
git checkout -b feature/w33-sequence-library-god-class main
grep -n "public sealed" src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs | head -1
```

Expected: `26:public sealed partial class SequenceLibrary` (already partial per W21 + W26.5 + W30 + W31 + W32 sister precedent; no D2 application needed).

- [ ] **Step 2: Build + tests baseline (no source change yet)**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore 2>&1 | tail -5
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~SequenceLibrary" --logger "console;verbosity=minimal" 2>&1 | tail -5
```

Expected: 0 errors, SequenceLibrary tests pass (baseline).

- [ ] **Step 3: Commit SPEC + PLAN**

```bash
git add docs/superpowers/specs/2026-07-13-sequence-library-god-class-refactor.md
git commit -m "W33 spec: SequenceLibrary god-class refactor (3 partials + 6-task roll-out, 29th overall, 7th App/Services, 1st App/Services/Sequence, 23rd subdirectory-pattern deployment)"
git add docs/superpowers/plans/2026-07-13-sequence-library-god-class-refactor.md
git commit -m "W33 plan: SequenceLibrary god-class refactor (3 partials: PersistenceFlow + Mutators + StaticHelpers)"
```

---

## Task T1: PersistenceFlow partial extraction (~41 LoC)

**Files:**
- Create: `src/PeakCan.Host.App/Services/Sequence/SequenceLibrary/PersistenceFlow.partial.cs` (~70 LoC)
- Modify: `src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs` (delete L190-L194 + L196-L210 + L212-L232 = 41 LoC, processed in reverse order)

**Interfaces:**
- Consumes: `SequenceLibrary` partial-class visibility (private fields + JsonOpts)
- Produces: 3 private helpers `EnsureLoaded` + `LoadUnlocked` + `SaveUnlocked` extracted to PersistenceFlow.partial.cs

- [ ] **Step 1: Re-grep boundaries BEFORE running deletion script (W19 R1 first-correction ENHANCED pre-flight prevention)**

```bash
grep -n "private void EnsureLoaded\|private IReadOnlyList<SavedSequence> LoadUnlocked\|private void SaveUnlocked" src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs
```

Expected:
```
190:    private void EnsureLoaded()
196:    private IReadOnlyList<SavedSequence> LoadUnlocked()
212:    private void SaveUnlocked(IEnumerable<SavedSequence> sequences)
```

- [ ] **Step 2: Verbatim re-extract 3 helpers from main HEAD (W20 LESSON 41st application)**

```bash
git show main:src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs | sed -n '190,194p'  # EnsureLoaded body
git show main:src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs | sed -n '196,210p'  # LoadUnlocked body
git show main:src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs | sed -n '212,232p'  # SaveUnlocked body
```

Expected: 5 LoC for EnsureLoaded + 15 LoC for LoadUnlocked + 21 LoC for SaveUnlocked = 41 LoC total.

- [ ] **Step 3: Write PersistenceFlow.partial.cs (~70 LoC)**

```csharp
// SequenceLibrary/PersistenceFlow.partial.cs — W33 T1 (Flow A, 41 LoC)
// Private file-IO lifecycle helpers: EnsureLoaded (cache-init sentinel) +
// LoadUnlocked (JSON deserialize with corrupt-fallback) + SaveUnlocked
// (atomic tmp+rename with IOException cleanup). Sister of W22 RecordService/
// Lifecycle + W27 RecentSessionsService/PersistenceOps + W28 DbcService/
// LoadLifecycle + W29 SendFrameLibrary/PersistenceFlow file-IO lifecycle
// sister-pattern. W33 is explicit "Mirror of SendFrameLibrary" per class xmldoc.
//
// Cross-partial caller pattern (W22+W23+W24+W25+W26+W27+W28+W29+W30+W31+W32 sister):
// Mutators partial calls EnsureLoaded + LoadUnlocked + SaveUnlocked via partial-class visibility.
//
// W23 STRUCT-FABRICATION LESSON: JsonSerializer.Serialize 2-arg +
// JsonSerializer.Deserialize 2-arg + File.WriteAllText 3-arg + File.Move 3-arg
// overload + JsonPropertyName attribute signatures verified during verbatim
// re-extraction from HEAD.
//
// W33 T1 verbatim re-extracted via `git show main:src/.../SequenceLibrary.cs | sed -n '190,194p;196,210p;212,232p'`
// per W20 T2 R1 fabrication LESSON (41st application).

using System.Text;
using System.Text.Json;

namespace PeakCan.Host.App.Services.Sequence;

public sealed partial class SequenceLibrary
{
    private void EnsureLoaded()
    {
        // ... [verbatim content from main HEAD L190-L194]
    }

    private IReadOnlyList<SavedSequence> LoadUnlocked()
    {
        // ... [verbatim content from main HEAD L196-L210]
    }

    private void SaveUnlocked(IEnumerable<SavedSequence> sequences)
    {
        // ... [verbatim content from main HEAD L212-L232]
    }
}
```

- [ ] **Step 4: Write deletion script + run + verify**

```python
# scripts/w33_task1_delete_persistenceflow.py — W19 R1 first-correction ENHANCED + W17 wc-l-splitlines CONFIRMED
from pathlib import Path

p = Path("src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

# W33 has 3 non-contiguous regions to delete (ensure + load + save unlocked)
# Process in REVERSE ORDER (highest line first) to keep line numbers stable
# First pass: delete SaveUnlocked (L212-L232)
new_lines = lines[:211] + lines[232:]
# Second pass: delete LoadUnlocked (now at L196-L210 after first pass)
new_lines = new_lines[:195] + new_lines[210:]
# Third pass: delete EnsureLoaded (now at L190-L194 after second pass)
new_lines = new_lines[:189] + new_lines[194:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

before = 244
after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"SequenceLibrary.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 41 (EnsureLoaded 5 + LoadUnlocked 15 + SaveUnlocked 21). Within ±2 LoC tolerance.")
assert 39 <= delta <= 43, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
```

- [ ] **Step 5: Build + tests to verify T1 extraction**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore 2>&1 | tail -5
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~SequenceLibrary" --logger "console;verbosity=minimal" 2>&1 | tail -5
```

Expected: 0 errors, SequenceLibrary tests pass without modification.

- [ ] **Step 6: Commit T1**

```bash
git add src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs src/PeakCan.Host.App/Services/Sequence/SequenceLibrary/PersistenceFlow.partial.cs scripts/w33_task1_delete_persistenceflow.py
git commit -m "W33 T1: extract PersistenceFlow partial (EnsureLoaded 5 LoC + LoadUnlocked 15 LoC + SaveUnlocked 21 LoC, -41 LoC main, 244 -> 203)"
```

---

## Task T2: Mutators partial extraction (~75 LoC)

**Files:**
- Create: `src/PeakCan.Host.App/Services/Sequence/SequenceLibrary/Mutators.partial.cs` (~100 LoC)
- Modify: `src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs` (delete L110-L122 + L124-L138 + L140-L159 + L161-L175 + L177-L188 = 75 LoC, processed in reverse order)

**Interfaces:**
- Consumes: `SequenceLibrary` partial-class visibility (private fields + ctor + properties)
- Produces: 5 public methods/properties `Load` + `Save` + `Add` + `Remove` + `Count` extracted to Mutators.partial.cs

- [ ] **Step 1: Re-grep boundaries AFTER T1 deletion (W19 R1 first-correction ENHANCED pre-flight prevention)**

```bash
grep -n "public IReadOnlyList<SavedSequence> Load\|public void Save\|public int Add\|public bool Remove\|public int Count" src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs
```

Expected: 5 method line numbers (post-T1 shift = -41 LoC).

- [ ] **Step 2: Verbatim re-extract 5 mutators from main HEAD (W20 LESSON 42nd application)**

```bash
git show main:src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs | sed -n '110,122p'   # Load xmldoc + body
git show main:src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs | sed -n '124,138p'   # Save xmldoc + body
git show main:src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs | sed -n '140,159p'   # Add xmldoc + body
git show main:src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs | sed -n '161,175p'   # Remove xmldoc + body
git show main:src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs | sed -n '177,188p'   # Count xmldoc + body
```

Expected: 13 + 15 + 20 + 15 + 12 = 75 LoC total.

- [ ] **Step 3: Write Mutators.partial.cs (~100 LoC)**

```csharp
// SequenceLibrary/Mutators.partial.cs — W33 T2 (Flow B, 75 LoC)
// 5 lock-gated mutator methods: Load + Save + Add + Remove + Count.
// All 5 share the _gate lock pattern (sister of W22 RecordService Mutators
// partial + W27 RecentSessionsService Mutators partial + W29 SendFrameLibrary
// Mutators partial) + every mutator calls cross-partial helpers from
// PersistenceFlow.partial.cs via partial-class visibility.
//
// All methods <50 LoC individually — NO W25 D5 deviation applied
// (per W29 NEW `small-god-class-no-largest-method-keeps-all-inline-default-pattern`
// 2/3 PROMOTION at W31.5: W33 SequenceLibrary SaveUnlocked 21 LoC LARGEST method
// < 50 LoC threshold → default D5 sister-principle applied).
//
// 2 [LoggerMessage] declarations (LogCorrupt + LogSaveFailed) stay on
// SequenceLibrary.cs per W18+W22+W23+W25+W26+W27+W28+W29+W30+W31+W32+W33
// sister precedent (CS8795 mitigation). Called from Flow A (PersistenceFlow)
// LoadUnlocked + SaveUnlocked.
//
// W33 T2 verbatim re-extracted via `git show main:src/.../SequenceLibrary.cs | sed -n '110,122p;124,138p;140,159p;161,175p;177,188p'`
// per W20 T2 R1 fabrication LESSON (42nd application).

namespace PeakCan.Host.App.Services.Sequence;

public sealed partial class SequenceLibrary
{
    /// <summary>
    /// Read the library from disk. ... [verbatim xmldoc + body from main HEAD L110-L122]
    /// </summary>
    public IReadOnlyList<SavedSequence> Load()
    {
        // ... [verbatim content]
    }

    /// <summary>
    /// Persist the entire library atomically. ... [verbatim xmldoc + body from main HEAD L124-L138]
    /// </summary>
    public void Save(IEnumerable<SavedSequence> sequences)
    {
        // ... [verbatim content]
    }

    /// <summary>
    /// Atomic Add. ... [verbatim xmldoc + body from main HEAD L140-L159]
    /// </summary>
    public int Add(SavedSequence sequence)
    {
        // ... [verbatim content]
    }

    /// <summary>Atomic Remove-by-Name. ... [verbatim xmldoc + body from main HEAD L161-L175]
    /// </summary>
    public bool Remove(string name)
    {
        // ... [verbatim content]
    }

    /// <summary>Number of saved sequences. ... [verbatim xmldoc + body from main HEAD L177-L188]
    /// </summary>
    public int Count
    {
        // ... [verbatim content]
    }
}
```

- [ ] **Step 4: Write deletion script + run + verify**

```python
# scripts/w33_task2_delete_mutators.py — W19 R1 first-correction ENHANCED (boundary verification + recovery procedure documented)
from pathlib import Path

p = Path("src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

# Verify post-T1 boundaries first
print(f"Total lines: {len(lines)}")

# W33 has 5 non-contiguous regions to delete:
# 1. Count (L177-L188, shifted by -41 from main HEAD = L136-L147 post-T1)
# 2. Remove (L161-L175, shifted = L120-L134)
# 3. Add (L140-L159, shifted = L99-L118)
# 4. Save (L124-L138, shifted = L83-L97)
# 5. Load (L110-L122, shifted = L69-L81)

# Process in REVERSE ORDER (highest line first):
# First pass: delete Count (expected L136-L147 post-T1)
new_lines = lines[:135] + lines[147:] if len(lines) >= 147 else lines
# Second pass: delete Remove (expected L120-L134)
if len(new_lines) >= 134:
    new_lines = new_lines[:119] + new_lines[134:]
# Third pass: delete Add (expected L99-L118)
if len(new_lines) >= 118:
    new_lines = new_lines[:98] + new_lines[118:]
# Fourth pass: delete Save (expected L83-L97)
if len(new_lines) >= 97:
    new_lines = new_lines[:82] + new_lines[97:]
# Fifth pass: delete Load (expected L69-L81)
if len(new_lines) >= 81:
    new_lines = new_lines[:68] + new_lines[81:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

before = 203
after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"SequenceLibrary.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 75 (Load 13 + Save 15 + Add 20 + Remove 15 + Count 12). Within ±2 LoC tolerance.")
assert 73 <= delta <= 77, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
```

- [ ] **Step 5: Build + tests to verify T2 extraction**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore 2>&1 | tail -5
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~SequenceLibrary" --logger "console;verbosity=minimal" 2>&1 | tail -5
```

Expected: 0 errors, SequenceLibrary tests pass without modification.

- [ ] **Step 6: Commit T2**

```bash
git add src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs src/PeakCan.Host.App/Services/Sequence/SequenceLibrary/Mutators.partial.cs scripts/w33_task2_delete_mutators.py
git commit -m "W33 T2: extract Mutators partial (Load 13 + Save 15 + Add 20 + Remove 15 + Count 12 LoC = 75 LoC, -75 LoC main, 203 -> 128)"
```

---

## Task T3: StaticHelpers partial extraction (~5 LoC)

**Files:**
- Create: `src/PeakCan.Host.App/Services/Sequence/SequenceLibrary/StaticHelpers.partial.cs` (~15 LoC)
- Modify: `src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs` (delete L234-L238 = 5 LoC, 1 contiguous region)

**Interfaces:**
- Consumes: `SequenceLibrary` partial-class visibility (no instance state needed)
- Produces: 1 static helper `DefaultPath` extracted to StaticHelpers.partial.cs

- [ ] **Step 1: Re-grep boundaries AFTER T2 deletion (W19 R1 first-correction ENHANCED pre-flight prevention)**

```bash
grep -n "private static string DefaultPath\|public sealed partial class\|^}" src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs
```

Expected: `DefaultPath` + closing `}` of class.

- [ ] **Step 2: Verbatim re-extract DefaultPath from main HEAD (W20 LESSON 43rd application)**

```bash
git show main:src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs | sed -n '234,238p'
```

Expected: 5 LoC.

- [ ] **Step 3: Write StaticHelpers.partial.cs (~15 LoC)**

```csharp
// SequenceLibrary/StaticHelpers.partial.cs — W33 T3 (Flow C, 5 LoC)
// Private static helper: DefaultPath resolves
// %APPDATA%\PeakCan.Host\sequences.json. Sister of W22 RecordService
// StaticHelpers + W27 RecentSessionsService StaticHelpers + W29
// SendFrameLibrary StaticHelpers default-path pattern.
//
// W23 STRUCT-FABRICATION LESSON: Environment.GetFolderPath 1-arg +
// Path.Combine 3-arg overload signatures verified during verbatim
// re-extraction from HEAD.
//
// W33 T3 verbatim re-extracted via `git show main:src/.../SequenceLibrary.cs | sed -n '234,238p'`
// per W20 T2 R1 fabrication LESSON (43rd application).

using System.IO;

namespace PeakCan.Host.App.Services.Sequence;

public sealed partial class SequenceLibrary
{
    private static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "PeakCan.Host", "sequences.json");
    }
}
```

- [ ] **Step 4: Write deletion script + run + verify**

```python
# scripts/w33_task3_delete_statichelpers.py
from pathlib import Path

p = Path("src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

START, END = 123, 127  # UPDATE per Step 1 grep result (DefaultPath post-T2)
before = len(lines)
new_lines = lines[:START - 1] + lines[END:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

before = 128
after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"SequenceLibrary.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 5 (DefaultPath body). Within ±2 LoC tolerance.")
assert 3 <= delta <= 7, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
```

- [ ] **Step 5: Build + tests to verify T3 extraction**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore 2>&1 | tail -5
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~SequenceLibrary" --logger "console;verbosity=minimal" 2>&1 | tail -5
```

Expected: 0 errors, SequenceLibrary tests pass without modification.

- [ ] **Step 6: Commit T3**

```bash
git add src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs src/PeakCan.Host.App/Services/Sequence/SequenceLibrary/StaticHelpers.partial.cs scripts/w33_task3_delete_statichelpers.py
git commit -m "W33 T3: extract StaticHelpers partial (DefaultPath 5 LoC, -5 LoC main, 128 -> 123)"
```

---

## Task T4: v3.46.5 → v3.47.0 MINOR + release notes

**Files:**
- Modify: `src/Directory.Build.props` (4-field version bump)
- Create: `docs/release-notes-v3.47.0.md` (~120 LoC)

- [ ] **Step 1: Bump Directory.Build.props**

```bash
python -c "
from pathlib import Path
p = Path('src/Directory.Build.props')
text = p.read_text(encoding='cp1252')
text = text.replace('<Version>3.46.5</Version>', '<Version>3.47.0</Version>', 1)
text = text.replace('<AssemblyVersion>3.46.5.0</AssemblyVersion>', '<AssemblyVersion>3.47.0.0</AssemblyVersion>', 1)
text = text.replace('<FileVersion>3.46.5.0</FileVersion>', '<FileVersion>3.47.0.0</FileVersion>', 1)
text = text.replace('<InformationalVersion>3.46.5</InformationalVersion>', '<InformationalVersion>3.47.0</InformationalVersion>', 1)
p.write_text(text, encoding='cp1252')
"
grep -E 'Version|FileVersion|InformationalVersion' src/Directory.Build.props
```

Expected: `<Version>3.47.0</Version>` + `<AssemblyVersion>3.47.0.0</AssemblyVersion>` + `<FileVersion>3.47.0.0</FileVersion>` + `<InformationalVersion>3.47.0</InformationalVersion>`.

- [ ] **Step 2: Write release notes**

Mirror W32 release-notes-v3.46.0.md format (~120 LoC).

- [ ] **Step 3: Build + full test suite to verify MINOR**

```bash
dotnet build PeakCan.Host.slnx -c Debug --no-restore 2>&1 | tail -5
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~SequenceLibrary" --logger "console;verbosity=minimal" 2>&1 | tail -5
```

Expected: 0 errors, all SequenceLibrary tests pass.

- [ ] **Step 4: Commit T4**

```bash
git add src/Directory.Build.props docs/release-notes-v3.47.0.md
git commit -m "W33 T4: v3.46.5 -> v3.47.0 MINOR (SequenceLibrary god-class refactor, 29th overall, 7th App/Services, 1st App/Services/Sequence, -121 LoC -49.6% main)"
```

---

## Task T5: Tier-3 ship (PR + squash + tag + GH release)

- [ ] **Step 1: Push branch + create PR**

```bash
git push -u origin feature/w33-sequence-library-god-class
gh pr create --base main --head feature/w33-sequence-library-god-class --title "W33 MINOR: SequenceLibrary god-class refactor (29th overall, 1st App/Services/Sequence, -121 LoC -49.6% main)" --body "[full PR body per W32 PR #65 sister precedent]"
```

- [ ] **Step 2: Wait for CI + squash-merge + tag + GH release**

```bash
gh pr checks <PR-number> --watch  # wait for CI (5-attempt MAX per W23.5-W32 sister pattern)
gh pr merge <PR-number> --squash --delete-branch
git tag -a v3.47.0 -m "v3.47.0 MINOR: SequenceLibrary god-class refactor (29th overall, 7th App/Services, 1st App/Services/Sequence, -121 LoC -49.6% main, 3-partial design PersistenceFlow+Mutators+StaticHelpers, sister of W29 SendFrameLibrary, default D5 sister-principle since LARGEST method 21 LoC < 50 LoC threshold)" <squash-commit-sha>
git push origin v3.47.0
gh release create v3.47.0 --title "v3.47.0 — SequenceLibrary god-class refactor" --notes "[release notes body]"
```

---

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings
- `dotnet test --filter "FullyQualifiedName~SequenceLibrary"`: tests pass without modification
- `dotnet test` (full solution): 0 new fails
- `wc -l src/PeakCan.Host.App/Services/Sequence/SequenceLibrary.cs` ≤ 135 LoC (target ~123)
- 3 NEW partial files in `SequenceLibrary/` directory
- 4 fields + 1 static readonly `JsonOpts` + 2 ctors + 2 nested enums + 4 inner classes/records + 2 `[LoggerMessage]` partials remain in main
- Tag v3.47.0 + GH release published
- Branch deleted post-merge

## Out of scope (YAGNI)

- No public/internal API change.
- No test changes.
- No facade pattern (W3-W32 CONFIRMED direct partial-class visibility).
- No xmldoc-grep risk (verified — tests do not path-grep main file content).
- No W18 R1 mitigation needed (D4 keeps all 2 `[LoggerMessage]` partials on main partial declaration).
- No `MultiFrameSequenceRow.cs` partial changes (stays in `Models` namespace; W33 SequenceLibrary is the persistence layer for saved sequences).
- No `SequenceSendService.cs` partial changes (W30 sister; W33 SequenceLibrary is the persistence layer for named sequences).
- No `SendFrameLibrary.cs` partial changes (W29 sister; W33 SequenceLibrary is the explicit "Mirror of SendFrameLibrary" — sister extraction, NOT merge).
