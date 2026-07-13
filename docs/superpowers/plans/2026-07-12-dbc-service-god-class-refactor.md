# W28 PLAN — DbcService god-class refactor (24th overall)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract 2 NEW partial-class files from `src/PeakCan.Host.App/Services/DbcService.cs` (312 → ~117 LoC) in line with the W3-W27 god-class refactor series.

**Architecture:** Sister pattern of W22 RecordService + W23 CyclicDbcSendService + W27 RecentSessionsService (App/Services JSON-persistence + file-IO lifecycle). 24th god-class refactor. 4th App/Services layer + 18th subdirectory-pattern deployment. **2-partial design** (1 less than W27's 3) due to smaller public API surface. Order A (LoadLifecycle, 85 LoC) → B (TextDecoding, ~110 LoC).

**Tech Stack:** C# .NET 10 partial-class split pattern (W3-W27 series), `System.Text` for Encoding handling, `Microsoft.Extensions.Logging` + 4 `[LoggerMessage]` partials, `System.Threading.Volatile.Read/Write<T>` for cross-thread `Current` visibility, `PeakCan.Host.Core.Dbc` for `DbcDocument`/`DbcParser`/`DbcOptions`/`ErrorCode`.

## Global Constraints

- **LoC formula**: W8.5 D7 ±2 LoC tolerance at each task boundary (W13 T1 2/3 loose-assertion sister). Re-grep + range verify after each task per W19 R1 first-correction.
- **Verbatim re-extraction**: W20 T2 R1 fabrication LESSON — every partial's content MUST come from `git show HEAD:src/...cs | sed -n '<range>p'`. NEVER hand-reconstruct method bodies.
- **Struct-ctor verification**: W23 LESSON — verify `Encoding.GetEncoding(int, EncoderFallback, DecoderFallback)` 3-arg + `File.ReadAllBytesAsync(string, CancellationToken)` 2-arg + `DbcParser.Parse(string, int, CancellationToken)` 3-arg + `Volatile.Read<T>` + `Volatile.Write<T>` + `Encoding.UTF8Encoding(bool, bool)` 2-arg ctor + `Encoding.UTF32Encoding(bool, bool)` 2-arg ctor + `PathNormalizer.Normalize` 1-arg signatures.
- **`[LoggerMessage]` partials stay on main partial declaration**: W18 R1 + W22 D4 + W23 D4 + W25 D4 + W26 D4 + W27 D4 sister precedent — 4 `LogLoadSucceeded` + `LogLoadParseFailed` + `LogLoadSizeFailed` + `LogLoadIoFailed` declarations stay on main partial.
- **Branch name**: `feature/w28-dbc-service-god-class`.
- **Target version**: v3.42.0 MINOR.
- **`LoadAsync` stays `virtual`** (test override per xmldoc L29) — NOT sealed.

---

## File structure

- NEW: `src/PeakCan.Host.App/Services/DbcService/LoadLifecycle.partial.cs` (T1, ~85 LoC)
- NEW: `src/PeakCan.Host.App/Services/DbcService/TextDecoding.partial.cs` (T2, ~110 LoC)
- MODIFY: `src/PeakCan.Host.App/Services/DbcService.cs` (T1+T2 deletions + final state ~117 LoC)
- MODIFY: `src/Directory.Build.props` (v3.41.5 → v3.42.0, T4)
- NEW: `docs/release-notes-v3.42.0.md` (T4, ~110 LoC)
- NEW: `scripts/w28_task1_delete_loadlifecycle.py`
- NEW: `scripts/w28_task2_delete_textdecoding.py`
- NEW: `docs/superpowers/capture-decisions/2026-07-13-w28-dbc-service-god-class-ship.md` (post-PR docs commit)

---

## Task 1: W28 T0.5 — Branch + baseline verification

**Files**: SPEC already on branch from `git checkout -b` + SPEC commit `3b26a77`. PLAN = this file.

- [ ] **Step 1: Verify baseline build + filter tests**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~DbcService|FullyQualifiedName~Dbc" --logger "console;verbosity=minimal"
```

Expected: 0 build errors. Filter tests pass.

- [ ] **Step 2: Commit PLAN**

```bash
git add docs/superpowers/plans/2026-07-12-dbc-service-god-class-refactor.md
git commit -m "W28 plan: DbcService god-class refactor (2 partials: LoadLifecycle + TextDecoding, A->B order, W20+W23 LESSONS, W25 D5 4th confirmation)"
```

---

## Task 2: W28 T1 — Extract LoadLifecycle partial (~85 LoC, LARGEST)

**Files:**
- NEW: `src/PeakCan.Host.App/Services/DbcService/LoadLifecycle.partial.cs`
- MODIFY: `src/PeakCan.Host.App/Services/DbcService.cs` (delete `LoadAsync` method with xmldoc, currently at L103-L187)

**Interfaces:**
- Consumes: `DbcService.cs` at lines 103-187 from HEAD.
- Produces: `DbcService/LoadLifecycle.partial.cs` containing `partial class DbcService` with `LoadAsync(string path, CancellationToken ct)` method.

- [ ] **Step 1: Re-grep boundaries of LoadLifecycle region**

```bash
grep -n "public virtual async Task LoadAsync" src/PeakCan.Host.App/Services/DbcService.cs
```

Expected: starts at LoadAsync xmldoc at L103, ends at closing `}` at L187.

- [ ] **Step 2: Re-extract verbatim from HEAD**

```bash
START_LINE=103  # update from Step 1
END_LINE=187    # update from Step 1
git show HEAD:src/PeakCan.Host.App/Services/DbcService.cs | sed -n "${START_LINE},${END_LINE}p"
```

Expected: exact verbatim LoadLifecycle region with xmldoc.

- [ ] **Step 3: Write LoadLifecycle.partial.cs**

```csharp
// DbcService/LoadLifecycle.partial.cs — W28 T1 (Flow A, LARGEST 85 LoC with xmldoc)
// Public LoadAsync virtual method: read DBC bytes → decode text →
// parse → mutate Current + raise DbcLoaded/LoadFailed event.
// Sister of W27 RecentSessionsService.LoadAsync which moved at W27 T1.
// LoadAsync 79 LoC LARGEST method moves here per W25 D5 + W26 +
// W27 D5 deviation (4th confirmation of "largest method CAN move"
// pattern: file-IO + parsing lifecycle = sharp discrete flow).
//
// 4 [LoggerMessage] declarations (LogLoadSucceeded + LogLoadParseFailed
// + LogLoadSizeFailed + LogLoadIoFailed) stay on DbcService.cs per
// W18 R1 + W22 D4 + W23 D4 + W25 D4 + W26 D4 + W27 D4 sister
// precedent (CS8795 mitigation).
//
// W23 STRUCT-FABRICATION LESSON: verify DbcParser.Parse(string, int,
// CancellationToken) 3-arg + Volatile.Read/Write<T> + Volatile.Write
// signatures (verified during verbatim re-extraction from HEAD).
//
// W28 T1 verbatim re-extracted via `git show HEAD:src/.../DbcService.cs | sed -n '103,187p'`
// per W20 T2 R1 fabrication LESSON (30th application).

using System.Text;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.Path;

namespace PeakCan.Host.App.Services;

public partial class DbcService
{
    // <verbatim LoadAsync xmldoc + body from Step 2>
}
```

- [ ] **Step 4: Write `scripts/w28_task1_delete_loadlifecycle.py`**

```python
"""W28 T1 deletion script — remove LoadLifecycle region (L103-187) from DbcService.cs.

Per W19 R1 first-correction: re-grep boundaries BEFORE running this script.
Per W20 T2 R1 fabrication LESSON: verbatim re-extract from HEAD before deleting.
Per W17 wc-l-splitlines CONFIRMED + W25 cp1252 binary: use binary read+write with cp1252.

W13 T1 2/3 loose-assertion: predicted -85 LoC (range L103-L187 inclusive = 85 lines
including LoadAsync xmldoc + body).
Within ±2 LoC tolerance.
"""
from pathlib import Path

p = Path("src/PeakCan.Host.App/Services/DbcService.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

START, END = 103, 187  # UPDATE per Step 1 grep result
before = len(lines)
new_lines = lines[:START - 1] + lines[END:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"DbcService.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 85 (Flow A LoadLifecycle LoadAsync range L103-L187). Within ±2 LoC tolerance.")
assert 83 <= delta <= 87, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
```

- [ ] **Step 5: Run deletion script**

```bash
python scripts/w28_task1_delete_loadlifecycle.py
wc -l src/PeakCan.Host.App/Services/DbcService.cs
```

Expected: main file = 227 LoC ±2 (delta = 85).

- [ ] **Step 6: Build + filter test**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~DbcService|FullyQualifiedName~Dbc"
```

Expected: 0 errors, all filter tests pass. W20 LESSON APPLIED 30th time. Add using directives as needed (likely `PeakCan.Host.Core` + `PeakCan.Host.Core.Dbc` + `PeakCan.Host.Core.Path`).

- [ ] **Step 7: Commit T1**

```bash
git add src/PeakCan.Host.App/Services/DbcService/LoadLifecycle.partial.cs
git add src/PeakCan.Host.App/Services/DbcService.cs
git add scripts/w28_task1_delete_loadlifecycle.py
git commit -m "W28 T1: extract LoadLifecycle partial (LoadAsync 79 LoC LARGEST + xmldoc, 312->227 main)"
```

---

## Task 3: W28 T2 — Extract TextDecoding partial (~110 LoC)

**Files:**
- NEW: `src/PeakCan.Host.App/Services/DbcService/TextDecoding.partial.cs`
- MODIFY: `src/PeakCan.Host.App/Services/DbcService.cs` (delete 2 static helpers with xmldoc, currently at ~L189-L299)

- [ ] **Step 1: Re-grep boundaries**

```bash
grep -n "private static.*ReadDbcBytesAsync\|private static string ReadDbcText" src/PeakCan.Host.App/Services/DbcService.cs
```

Expected: starts at first xmldoc before ReadDbcBytesAsync (~L189), ends at closing `}` of ReadDbcText (~L299).

- [ ] **Step 2: Re-extract verbatim from HEAD** + write partial + deletion script
- [ ] **Step 3: Run deletion**: expected main file ≈ 117 LoC ±2 (delta ≈ 110)
- [ ] **Step 4: Build + filter test** (W20 LESSON APPLIED 31st time). Likely using-directive fixes for `System.Text` + `System.Globalization` + `PeakCan.Host.Core.Path`.
- [ ] **Step 5: Commit T2**

```bash
git add src/PeakCan.Host.App/Services/DbcService/TextDecoding.partial.cs
git add src/PeakCan.Host.App/Services/DbcService.cs
git commit -m "W28 T2: extract TextDecoding partial (ReadDbcBytesAsync + ReadDbcText, 227->117 main)"
```

---

## Task 4: W28 T3 — SKIPPED (no 3rd partial)

DbcService 2-partial design complete after T1 + T2. No T3 needed.

---

## Task 5: W28 T4 — Version bump + release notes

**Files:**
- MODIFY: `src/Directory.Build.props` (v3.41.5 → v3.42.0)
- NEW: `docs/release-notes-v3.42.0.md`

- [ ] **Step 1: Bump version**

```bash
sed -i 's|<Version>3.41.5</Version>|<Version>3.42.0</Version>|; s|<AssemblyVersion>3.41.5.0</AssemblyVersion>|<AssemblyVersion>3.42.0.0</AssemblyVersion>|; s|<FileVersion>3.41.5.0</FileVersion>|<FileVersion>3.42.0.0</FileVersion>|; s|<InformationalVersion>3.41.5</InformationalVersion>|<InformationalVersion>3.42.0</InformationalVersion>|' src/Directory.Build.props
```

- [ ] **Step 2: Write `docs/release-notes-v3.42.0.md`** with W3-W28 cumulative god-class trajectory.
- [ ] **Step 3: Commit T4**

```bash
git add src/Directory.Build.props
git add docs/release-notes-v3.42.0.md
git commit -m "W28 T4: v3.41.5 -> v3.42.0 MINOR + release notes"
```

---

## Task 6: W28 T5 — Tier-3 ship

- [ ] **Step 1: Push branch + create PR**

```bash
git push -u origin feature/w28-dbc-service-god-class
gh pr create --base main --head feature/w28-dbc-service-god-class --title "W28 MINOR: DbcService god-class refactor (24th overall, 4th App/Services, -195 LoC main)" --body "Sister of W22 RecordService + W23 CyclicDbcSendService + W27 RecentSessionsService (App/Services file-IO lifecycle + parsing). 24th god-class refactor. 2 NEW partials in DbcService/ subdirectory: LoadLifecycle (LoadAsync 79 LoC LARGEST moves per W25 D5 4th confirmation) + TextDecoding (ReadDbcBytesAsync + ReadDbcText with BOM detection + UTF-8/OEM/Latin-1 fallback). 4 [LoggerMessage] partials stay on main per D4 sister. LoadAsync stays virtual for test override. Tests pass without modification."
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
git tag -a v3.42.0 -m "W28 MINOR - DbcService god-class refactor (24th overall, 4th App/Services, -195 LoC main, 2 NEW partials)"
git push origin v3.42.0
gh release create v3.42.0 --target main --title "v3.42.0 MINOR - DbcService god-class refactor" --notes-file docs/release-notes-v3.42.0.md
```

- [ ] **Step 5: Post-PR docs commit**

Per W19-W27 D6 sister pattern: write `docs/superpowers/capture-decisions/2026-07-13-w28-dbc-service-god-class-ship.md` and commit it to main as a separate post-PR docs commit.

---

## Verification matrix

| Check | Expected | Sister |
|---|---|---|
| `dotnet build src/PeakCan.Host.App/` | 0 errors, 0 warnings | W20+W22+W27 |
| `dotnet test --filter "~DbcService|~Dbc"` | All pass (incl. virtual-override tests) | W20+W27 |
| `dotnet test` (full solution) | 0 new fails | W13 T1 sister |
| `wc -l src/.../DbcService.cs` | ≤ 130 LoC (target ~117) | W8.5 D7 ±2 |
| 2 NEW partial files in `DbcService/` | YES | W12-W27 |
| 4 [LoggerMessage] partials stay on main | YES | W18+W22+W23+W25+W26+W27 |
| 2 readonly fields + Current property + 2 events + 2 ctors + SetCurrentForTests stay in main | YES | W21+W27 |
| DI registration unchanged | YES | W12-W27 |
| LoadAsync stays virtual | YES | (test override preserved) |
| Tag v3.42.0 + GH release published | YES | W12-W27 |
| Branch deleted post-merge | YES | W12-W27 |
| capture-decisions file landing on main | YES | W19-W27 |

## Out of scope (YAGNI)

- No public/internal API change.
- No test changes.
- No facade pattern.
- No xmldoc-grep risk.
- No `[LoggerMessage]` partial relocation (all 4 stay on main per D4).
- No `LoadAsync` virtual → sealed change.
- No `Encoding.GetEncoding` overload change.
