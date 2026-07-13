# W32 Implementation Plan — DbcApi god-class refactor (28th overall, 4th App/Services/Scripting)

**Goal**: Extract 2 NEW partial-class files (LoadFlow + QueryFlow) from `src/PeakCan.Host.App/Services/Scripting/DbcApi.cs` 279 → ~108 LoC (-171 LoC, -61.3%). Public API + existing tests unchanged.

**Architecture**: Sister pattern of W22 + W23 + W27 + W28 + W29 + W30 + W31 (subdirectory + non-suffix `.partial.cs` filenames). 28th god-class refactor. **6th App/Services layer** + **4th App/Services/Scripting subdirectory** + **22nd subdirectory-pattern deployment** + **6th multi-interface partial-class extraction** (sister of W26 CanApi `IFrameSink + IScriptCanApi` + W31 ReplayService `IReplayService + IDisposable`).

**Tech Stack**: C# .NET 10 partial-class split pattern + W25 D5 deviation (LARGEST method `Load` 73 LoC MOVES to LoadFlow.partial.cs) + W19 R1 first-correction ENHANCED (pre-flight prevention + post-failure recovery) + W20 verbatim re-extraction + W23 STRUCT-FABRICATION LESSON

**Spec**: [2026-07-13-dbc-api-god-class-refactor.md](../specs/2026-07-13-dbc-api-god-class-refactor.md)

---

## Global Constraints

- 1 partial class with 2 partials in subdirectory `src/PeakCan.Host.App/Services/Scripting/DbcApi/`
- `public sealed partial class DbcApi : IScriptDbcApi` (already partial at L20; no D2 application needed per W21 + W26.5 + W30 + W31 sister precedent)
- All public API surface unchanged (DI registration + tests + interface contracts)
- LoC formula `main_after = main_before - delete_count` (W8.5 D7 32-locked, no +1 offset)
- W17 wc-l-splitlines CONFIRMED + cp1252 binary read+write pattern (since file is Windows-1252 encoded)
- W19 R1 first-correction ENHANCED: re-grep boundaries BEFORE each deletion script + post-failure recovery procedure (git checkout + re-grep + corrected offsets + re-run)
- W20 LESSON: verbatim re-extraction via `git show main:src/.../DbcApi.cs | sed -n '<range>p'`
- W23 STRUCT-FABRICATION LESSON: verify `Task<object>` async signature + `Volatile.Write` 1-arg + `ConcurrentDictionary` indexer + `DbcDocument.Messages` enumerable + `Message.Signals` + `SignalDecoder.Decode` static + `_dbcService.LoadAsync` async signatures
- W25 D5 deviation: LARGEST method `Load` 73 LoC ≥ 60 LoC + sharp discrete flow boundary (Load → return result envelope) = MOVES to LoadFlow.partial.cs

---

## Task T0: Branch + SPEC + PLAN commits

**Files:**
- Create: `docs/superpowers/specs/2026-07-13-dbc-api-god-class-refactor.md`
- Create: `docs/superpowers/plans/2026-07-13-dbc-api-god-class-refactor.md`

**Interfaces:**
- Produces: SPEC + PLAN committed to feature branch

- [ ] **Step 1: Branch + verify already partial**

```bash
git checkout -b feature/w32-dbc-api-god-class main
grep -n "public sealed" src/PeakCan.Host.App/Services/Scripting/DbcApi.cs | head -1
```

Expected: `20:public sealed partial class DbcApi : IScriptDbcApi` (already partial per W21 + W26.5 + W30 + W31 sister precedent; no D2 application needed).

- [ ] **Step 2: Build + tests baseline (no source change yet)**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore 2>&1 | tail -5
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~DbcApi" --logger "console;verbosity=minimal" 2>&1 | tail -5
```

Expected: 0 errors, DbcApi tests pass (baseline).

- [ ] **Step 3: Commit SPEC + PLAN**

```bash
git add docs/superpowers/specs/2026-07-13-dbc-api-god-class-refactor.md
git commit -m "W32 spec: DbcApi god-class refactor (2 partials + 5-task roll-out, 28th overall, 6th App/Services, 4th App/Services/Scripting, 22nd subdirectory-pattern deployment)"
git add docs/superpowers/plans/2026-07-13-dbc-api-god-class-refactor.md
git commit -m "W32 plan: DbcApi god-class refactor (2 partials: LoadFlow + QueryFlow)"
```

---

## Task T1: LoadFlow partial extraction (~96 LoC)

**Files:**
- Create: `src/PeakCan.Host.App/Services/Scripting/DbcApi/LoadFlow.partial.cs` (~125 LoC)
- Modify: `src/PeakCan.Host.App/Services/Scripting/DbcApi.cs` (delete L53-L148 = 96 LoC)

**Interfaces:**
- Consumes: `DbcApi` partial-class visibility (private fields + ctor + properties)
- Produces: 1 public method `Load` extracted to LoadFlow.partial.cs

- [ ] **Step 1: Re-grep boundaries BEFORE running deletion script (W19 R1 first-correction ENHANCED pre-flight prevention)**

```bash
grep -n "public async Task<object> Load\|public object? Decode\|public object\[\] GetMessages" src/PeakCan.Host.App/Services/Scripting/DbcApi.cs
```

Expected:
```
76:    public async Task<object> Load(string path, CancellationToken ct = default)
155:    public object? Decode(CanFrame frame)
214:    public object[] GetMessages()
```

- [ ] **Step 2: Verbatim re-extract Load xmldoc + body from main HEAD (W20 LESSON 39th application)**

```bash
git show main:src/PeakCan.Host.App/Services/Scripting/DbcApi.cs | sed -n '53,148p'
```

Expected: 96 LoC, complete `Load` xmldoc (L53-L75, 23 LoC) + body (L76-L148, 73 LoC) with `Task<object>` async return + result envelope (success / LoadFailed-surfaced-error / Cancelled / Exception = 4 distinct result paths).

- [ ] **Step 3: Write LoadFlow.partial.cs (~125 LoC)**

```csharp
// DbcApi/LoadFlow.partial.cs — W32 T1 (Flow A, LARGEST 73 LoC)
// Load method: delegates to _dbcService.LoadAsync + surfaces 4 distinct result
// envelopes (success / LoadFailed-surfaced-error / Cancelled / Exception).
// Public async API for ClearScript V8 script callers (script engine uses
// default-value parameter so existing dbc.load(path) calls work bit-identically).
//
// W25 D5 deviation APPLIED: Load 73 LoC LARGEST method MOVES per the sharp
// discrete flow boundary criterion (Load → return result envelope = discrete
// Load dispatcher with 4 distinct result paths, NOT a single central
// orchestration loop). Sister of W25 OnChannelFrame 73 LoC + W26 OnFrame
// 62 LoC + W27 LoadAsync 60 LoC + W28 LoadAsync 79 LoC + W30 SendAsync
// 91 LoC moves. **6th move** in largest-method-can-move observations.
//
// Cross-partial helper pattern (W22+W23+W24+W25+W26+W27+W28+W29+W30+W31 sister):
// Load reads _currentDocument + _lastLoadError (both stay in main partial;
// written by OnDbcLoaded + OnLoadFailed respectively).
//
// W23 STRUCT-FABRICATION LESSON: Task<object> async signature + Volatile.Write
// 1-arg + ConcurrentDictionary indexer + DbcDocument.Messages enumerable
// signatures verified during verbatim re-extraction.
//
// W32 T1 verbatim re-extracted via `git show main:src/.../DbcApi.cs | sed -n '53,148p'`
// per W20 T2 R1 fabrication LESSON (39th application).

using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.Services.Scripting;

public sealed partial class DbcApi
{
    /// <summary>
    /// Load and parse a DBC file. ... [verbatim xmldoc + body from main HEAD L53-L148]
    /// </summary>
    public async Task<object> Load(string path, CancellationToken ct = default)
    {
        // ... [verbatim content]
    }
}
```

- [ ] **Step 4: Write deletion script + run + verify**

```python
# scripts/w32_task1_delete_loadflow.py — W19 R1 first-correction ENHANCED + W17 wc-l-splitlines CONFIRMED
from pathlib import Path

p = Path("src/PeakCan.Host.App/Services/Scripting/DbcApi.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

START, END = 53, 148  # xmldoc + Load body (Load xmldoc starts at L53, body L76-L148)
before = len(lines)
new_lines = lines[:START - 1] + lines[END:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"DbcApi.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 96 (Load xmldoc L53-L75 + body L76-L148). Within ±2 LoC tolerance.")
assert 94 <= delta <= 98, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
```

- [ ] **Step 5: Build + tests to verify T1 extraction**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore 2>&1 | tail -5
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~DbcApi" --logger "console;verbosity=minimal" 2>&1 | tail -5
```

Expected: 0 errors, DbcApi tests pass without modification.

- [ ] **Step 6: Commit T1**

```bash
git add src/PeakCan.Host.App/Services/Scripting/DbcApi.cs src/PeakCan.Host.App/Services/Scripting/DbcApi/LoadFlow.partial.cs scripts/w32_task1_delete_loadflow.py
git commit -m "W32 T1: extract LoadFlow partial (Load 73 LoC LARGEST method moves per W25 D5 deviation, -96 LoC main, 279 -> 183)"
```

---

## Task T2: QueryFlow partial extraction (~75 LoC)

**Files:**
- Create: `src/PeakCan.Host.App/Services/Scripting/DbcApi/QueryFlow.partial.cs` (~100 LoC)
- Modify: `src/PeakCan.Host.App/Services/Scripting/DbcApi.cs` (delete L150-L185 + L187-L208 + L210-L226 = 75 LoC, processed in reverse order)

**Interfaces:**
- Consumes: `DbcApi` partial-class visibility (private fields + ctor + properties)
- Produces: 3 public methods `Decode` + `GetSignal` + `GetMessages` extracted to QueryFlow.partial.cs

- [ ] **Step 1: Re-grep boundaries AFTER T1 deletion (W19 R1 first-correction ENHANCED pre-flight prevention)**

```bash
grep -n "public object? Decode\|public object? GetSignal\|public object\[\] GetMessages\|public void Dispose" src/PeakCan.Host.App/Services/Scripting/DbcApi.cs
```

Expected: 3 query method line numbers (post-T1 shift = -96 LoC) + `Dispose` boundary.

- [ ] **Step 2: Verbatim re-extract 3 query methods from main HEAD (W20 LESSON 40th application)**

```bash
git show main:src/PeakCan.Host.App/Services/Scripting/DbcApi.cs | sed -n '150,185p'   # Decode xmldoc + body
git show main:src/PeakCan.Host.App/Services/Scripting/DbcApi.cs | sed -n '187,208p'   # GetSignal xmldoc + body
git show main:src/PeakCan.Host.App/Services/Scripting/DbcApi.cs | sed -n '210,226p'   # GetMessages xmldoc + body
```

Expected: 36 LoC for Decode (xmldoc + body) + 22 LoC for GetSignal + 17 LoC for GetMessages = 75 LoC total.

- [ ] **Step 3: Write QueryFlow.partial.cs (~100 LoC)**

```csharp
// DbcApi/QueryFlow.partial.cs — W32 T2 (Flow B, 75 LoC)
// Query+decode methods: Decode (frame → signal values + cache update) +
// GetSignal (most recent value lookup) + GetMessages (list all messages).
// All 3 touch _currentDocument (read-only, Volatile.Write'd by OnDbcLoaded)
// + _signalValues (ConcurrentDictionary, write in Decode).
//
// Called from ClearScript V8 script callers via IScriptDbcApi interface.
// Cross-partial caller pattern: LoadFlow (Flow A) Load reads _currentDocument
// + _lastLoadError; QueryFlow (Flow B) reads _currentDocument + writes
// _signalValues. Both partials share state via partial-class visibility
// (sister of W22+W23+W24+W25+W26+W27+W28+W29+W30+W31 cross-partial helper pattern).
//
// W23 STRUCT-FABRICATION LESSON: DbcDocument.Messages enumerable signature +
// Message.Signals signature + SignalDecoder.Decode static signature verified.
//
// W32 T2 verbatim re-extracted via `git show main:src/.../DbcApi.cs | sed -n '150,185p;187,208p;210,226p'`
// per W20 T2 R1 fabrication LESSON (40th application).

using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.Services.Scripting;

public sealed partial class DbcApi
{
    /// <summary>
    /// Decode a received frame using the loaded DBC document.
    /// ... [verbatim xmldoc + body from main HEAD L150-L185]
    /// </summary>
    public object? Decode(CanFrame frame)
    {
        // ... [verbatim content]
    }

    /// <summary>
    /// Get the most recent value of a specific signal.
    /// ... [verbatim xmldoc + body from main HEAD L187-L208]
    /// </summary>
    public object? GetSignal(string messageName, string signalName)
    {
        // ... [verbatim content]
    }

    /// <summary>
    /// List all messages in the loaded DBC document.
    /// ... [verbatim xmldoc + body from main HEAD L210-L226]
    /// </summary>
    public object[] GetMessages()
    {
        // ... [verbatim content]
    }
}
```

- [ ] **Step 4: Write deletion script + run + verify**

```python
# scripts/w32_task2_delete_queryflow.py — W19 R1 first-correction ENHANCED (post-failure recovery awareness)
from pathlib import Path

p = Path("src/PeakCan.Host.App/Services/Scripting/DbcApi.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

# Verify post-T1 boundaries first
print(f"Total lines: {len(lines)}")
print(f"Decode (expected L150-L185): line 150 = {lines[149].strip() if len(lines) > 149 else 'N/A'}")
print(f"GetSignal (expected L187-L208): line 187 = {lines[186].strip() if len(lines) > 186 else 'N/A'}")
print(f"GetMessages (expected L210-L226): line 210 = {lines[209].strip() if len(lines) > 209 else 'N/A'}")
print(f"Dispose (expected L229): {lines[228].strip() if len(lines) > 228 else 'N/A'}")
print()

# Process in REVERSE ORDER (highest line first):
# First pass: delete GetMessages (L210-L226)
new_lines = lines[:209] + lines[226:]
# Second pass: delete GetSignal (now at L187-L208 after first pass)
new_lines = new_lines[:186] + new_lines[208:]
# Third pass: delete Decode (now at L150-L185 after second pass)
new_lines = new_lines[:149] + new_lines[185:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

before = 183
after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"DbcApi.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 75 (Decode 36 + GetSignal 22 + GetMessages 17). Within ±2 LoC tolerance.")
assert 73 <= delta <= 77, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
```

- [ ] **Step 5: Build + tests to verify T2 extraction**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore 2>&1 | tail -5
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~DbcApi" --logger "console;verbosity=minimal" 2>&1 | tail -5
```

Expected: 0 errors, DbcApi tests pass without modification.

- [ ] **Step 6: Commit T2**

```bash
git add src/PeakCan.Host.App/Services/Scripting/DbcApi.cs src/PeakCan.Host.App/Services/Scripting/DbcApi/QueryFlow.partial.cs scripts/w32_task2_delete_queryflow.py
git commit -m "W32 T2: extract QueryFlow partial (Decode 31 LoC + GetSignal 16 LoC + GetMessages 13 LoC + xmldocs, -75 LoC main, 183 -> 108)"
```

---

## Task T3: v3.45.5 → v3.46.0 MINOR + release notes

**Files:**
- Modify: `src/Directory.Build.props` (4-field version bump)
- Create: `docs/release-notes-v3.46.0.md` (~120 LoC)

- [ ] **Step 1: Bump Directory.Build.props**

```bash
python -c "
from pathlib import Path
p = Path('src/Directory.Build.props')
text = p.read_text(encoding='cp1252')
text = text.replace('<Version>3.45.5</Version>', '<Version>3.46.0</Version>', 1)
text = text.replace('<AssemblyVersion>3.45.5.0</AssemblyVersion>', '<AssemblyVersion>3.46.0.0</AssemblyVersion>', 1)
text = text.replace('<FileVersion>3.45.5.0</FileVersion>', '<FileVersion>3.46.0.0</FileVersion>', 1)
text = text.replace('<InformationalVersion>3.45.5</InformationalVersion>', '<InformationalVersion>3.46.0</InformationalVersion>', 1)
p.write_text(text, encoding='cp1252')
"
grep -E 'Version|FileVersion|InformationalVersion' src/Directory.Build.props
```

Expected: `<Version>3.46.0</Version>` + `<AssemblyVersion>3.46.0.0</AssemblyVersion>` + `<FileVersion>3.46.0.0</FileVersion>` + `<InformationalVersion>3.46.0</InformationalVersion>`.

- [ ] **Step 2: Write release notes**

Mirror W31 release-notes-v3.45.0.md format (~120 LoC).

- [ ] **Step 3: Build + full test suite to verify MINOR**

```bash
dotnet build PeakCan.Host.slnx -c Debug --no-restore 2>&1 | tail -5
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~DbcApi" --logger "console;verbosity=minimal" 2>&1 | tail -5
```

Expected: 0 errors, all DbcApi tests pass.

- [ ] **Step 4: Commit T3**

```bash
git add src/Directory.Build.props docs/release-notes-v3.46.0.md
git commit -m "W32 T3: v3.45.5 -> v3.46.0 MINOR (DbcApi god-class refactor, 28th overall, 6th App/Services, 4th App/Services/Scripting, -171 LoC -61.3% main)"
```

---

## Task T4: Tier-3 ship (PR + squash + tag + GH release)

- [ ] **Step 1: Push branch + create PR**

```bash
git push -u origin feature/w32-dbc-api-god-class
gh pr create --base main --head feature/w32-dbc-api-god-class --title "W32 MINOR: DbcApi god-class refactor (28th overall, 4th App/Services/Scripting, -171 LoC -61.3% main)" --body "[full PR body per W31 PR #63 sister precedent]"
```

- [ ] **Step 2: Wait for CI + squash-merge + tag + GH release**

```bash
gh pr checks <PR-number> --watch  # wait for CI (5-attempt MAX per W23.5-W31 sister pattern)
gh pr merge <PR-number> --squash --delete-branch
git tag -a v3.46.0 -m "v3.46.0 MINOR: DbcApi god-class refactor (28th overall, 6th App/Services, 4th App/Services/Scripting, -171 LoC -61.3% main, 2-partial design LoadFlow+QueryFlow, single-interface IScriptDbcApi, W25 D5 deviation 6th application)" <squash-commit-sha>
git push origin v3.46.0
gh release create v3.46.0 --title "v3.46.0 — DbcApi god-class refactor" --notes "[release notes body]"
```

---

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings
- `dotnet test --filter "FullyQualifiedName~DbcApi"`: tests pass without modification
- `dotnet test` (full solution): 0 new fails
- `wc -l src/PeakCan.Host.App/Services/Scripting/DbcApi.cs` ≤ 120 LoC (target ~108)
- 2 NEW partial files in `DbcApi/` directory
- 5 fields + 1 ctor + 2 private helpers + 1 `Dispose` + 1 inner record + 2 `[LoggerMessage]` partials remain in main
- Tag v3.46.0 + GH release published
- Branch deleted post-merge

## Out of scope (YAGNI)

- No public/internal API change.
- No test changes.
- No facade pattern (W3-W31 CONFIRMED direct partial-class visibility).
- No xmldoc-grep risk (verified — tests do not path-grep main file content).
- No W18 R1 mitigation needed (D4 keeps all 2 `[LoggerMessage]` partials on main partial declaration).
- No `DbcService.cs` partial changes (sister of W28; W32's DbcApi wraps DbcService).
- No `SignalDecoder.cs` or `DbcDocument.cs` partial changes.
