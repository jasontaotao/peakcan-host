# W26 PLAN — CanApi god-class refactor (22nd overall)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract 3 NEW partial-class files from `src/PeakCan.Host.App/Services/Scripting/CanApi.cs` (347 → ~137 LoC) in line with the W3-W25 god-class refactor series.

**Architecture:** Sister pattern of W14 ScriptEngine (App/Services/Scripting layer) + W25 ChannelRouter (largest-method-can-move deviation precedent). 22nd god-class refactor. 7th App/Services + 16th subdirectory-pattern deployment. Order A (SinkLifecycle, 80 LoC) → B (CallbackRegistry, 80 LoC) → C (SendAndQuery, 50 LoC).

**Tech Stack:** C# .NET 10 partial-class split pattern (W3-W25 series), `Microsoft.Extensions.Logging.Abstractions` for ILogger + 11 `[LoggerMessage]` partials, `System.Collections.Concurrent.ConcurrentDictionary` for thread-safe callback registry.

## Global Constraints

- **LoC formula**: W8.5 D7 ±2 LoC tolerance at each task boundary (W13 T1 2/3 loose-assertion sister). Re-grep + range verify after each task per W19 R1 first-correction.
- **Verbatim re-extraction**: W20 T2 R1 fabrication LESSON — every partial's content MUST come from `git show HEAD:src/...cs | sed -n '<range>p'`. NEVER hand-reconstruct method bodies.
- **Struct-ctor verification**: W23 LESSON — verify `CanFrame(canId, payload, FrameFlags.None, ChannelId.None, default)` 5-arg + `CanId(raw, FrameFormat format)` 2-arg + `FrameFormat.Standard`/`FrameFormat.Extended` enum + `ChannelId.None` static field signatures.
- **`[LoggerMessage]` partials stay on main partial declaration**: W18 R1 + W22 D4 + W23 D3 D4 + W25 D3 D4 sister precedent — 11 `LogInvalidCanId` + `LogSendEmptyData` + `LogDataTooLong` + `LogSendFailed` + `LogFrameCallbackRegistered` + `LogFrameCallbackUnregistered` + `LogMessageCallbackRegistered` + `LogPrefixCallbackRegistered` + `LogFrameCallbackError` + `LogMessageCallbackError` + `LogPrefixCallbackError` + `LogSinkError` declarations stay on main partial.
- **Branch name**: `feature/w26-can-api-god-class`.
- **Target version**: v3.40.0 MINOR.

---

## File structure

- NEW: `src/PeakCan.Host.App/Services/Scripting/CanApi/SinkLifecycle.partial.cs` (T1, ~80 LoC)
- NEW: `src/PeakCan.Host.App/Services/Scripting/CanApi/CallbackRegistry.partial.cs` (T2, ~80 LoC)
- NEW: `src/PeakCan.Host.App/Services/Scripting/CanApi/SendAndQuery.partial.cs` (T3, ~50 LoC)
- MODIFY: `src/PeakCan.Host.App/Services/Scripting/CanApi.cs` (T1+T2+T3 deletions + final state ~137 LoC)
- MODIFY: `src/Directory.Build.props` (v3.39.5 → v3.40.0, T4)
- NEW: `docs/release-notes-v3.40.0.md` (T4, ~110 LoC)
- NEW: `scripts/w26_task1_delete_sinklifecycle.py` (T1)
- NEW: `scripts/w26_task2_delete_callbackregistry.py` (T2)
- NEW: `scripts/w26_task3_delete_sendandquery.py` (T3)
- NEW: `docs/superpowers/capture-decisions/2026-07-12-w26-can-api-god-class-ship.md` (post-PR docs commit)

---

## Task 1: W26 T0.5 — Branch + baseline verification

**Files**: SPEC already on branch from `git checkout -b` + SPEC commit `c446019`. PLAN = this file. Tests baseline = full App.Tests suite.

**Interfaces**:
- Consumes: `CanApi.cs` at v3.39.5 main
- Produces: PLAN committed to branch, baseline test suite passes

- [ ] **Step 1: Verify baseline build + filter tests**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~CanApi|FullyQualifiedName~Script" --logger "console;verbosity=minimal"
```

Expected: 0 build errors. Filter test count varies — main goal is "no fails pre-W26".

- [ ] **Step 2: Commit PLAN**

```bash
git add docs/superpowers/plans/2026-07-12-can-api-god-class-refactor.md
git commit -m "W26 plan: CanApi god-class refactor (3 partials: SinkLifecycle + CallbackRegistry + SendAndQuery, largest-first A->B->C order)"
```

---

## Task 2: W26 T1 — Extract SinkLifecycle partial (80 LoC, LARGEST)

**Files:**
- NEW: `src/PeakCan.Host.App/Services/Scripting/CanApi/SinkLifecycle.partial.cs`
- MODIFY: `src/PeakCan.Host.App/Services/Scripting/CanApi.cs` (delete OnFrame(CanFrame) + OnError + Dispose methods, currently at L233-L310)

**Interfaces:**
- Consumes: `CanApi.cs` at lines 233-310 (3 sink-related methods) from HEAD.
- Produces: `CanApi/SinkLifecycle.partial.cs` containing `partial class CanApi` with `OnFrame(CanFrame frame)` + `OnError(Exception ex)` + `Dispose()` methods.

- [ ] **Step 1: Re-grep boundaries of SinkLifecycle region exactly**

```bash
grep -n "public void OnFrame\|public void OnError\|public void Dispose" src/PeakCan.Host.App/Services/Scripting/CanApi.cs
```

Expected: `OnFrame(CanFrame frame)` at L233, `OnError(Exception ex)` at L296, `Dispose()` at L304, end-of-class closing brace at L347.

- [ ] **Step 2: Re-extract verbatim from HEAD**

```bash
START_LINE=233  # update from Step 1
END_LINE=310    # update from Step 1
git show HEAD:src/PeakCan.Host.App/Services/Scripting/CanApi.cs | sed -n "${START_LINE},${END_LINE}p"
```

Expected: exact verbatim SinkLifecycle region.

- [ ] **Step 3: Write SinkLifecycle.partial.cs**

```csharp
// CanApi/SinkLifecycle.partial.cs — W26 T1 (Flow A, LARGEST)
// IFrameSink interface implementation: receives frames from
// ChannelRouter fanout (OnFrame(CanFrame frame) sink dispatcher)
// + auto-detaches on error (OnError(Exception ex)) + cleanup
// unsubscribe from ChannelRouter (Dispose()). Sister of W25
// FrameRouting.partial.cs pattern (same fan-out-with-error-isolation
// shape across partial boundary).
//
// OnFrame(CanFrame frame) 62 LoC LARGEST method moves here per W25
// D5 deviation (frame-arrives -> callback-fanout discrete dispatcher
// shape, sister of W25 OnChannelFrame 75 LoC which moved for
// fan-out-with-error-isolation).
//
// All 11 [LoggerMessage] declarations stay on CanApi.cs per W18 R1
// + W22 D4 + W23 D4 + W25 D4 sister precedent (CS8795 mitigation).

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.App.Services.Scripting;

public sealed partial class CanApi
{
    // <verbatim OnFrame(CanFrame) + OnError + Dispose bodies from Step 2, including xmldoc>
}
```

- [ ] **Step 4: Write `scripts/w26_task1_delete_sinklifecycle.py`** with W13 T1 2/3 loose assertion + W17 wc-l-splitlines CONFIRMED + W25 cp1252 binary pattern.

```python
import re
from pathlib import Path

p = Path("src/PeakCan.Host.App/Services/Scripting/CanApi.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

# ranges are 1-indexed INCLUSIVE
def delete_range(start, end):
    return lines[:start - 1] + lines[end:]

# Match the SinkLifecycle region
START, END = 233, 310  # UPDATE per Step 1 grep result
before = len(lines)
new_lines = delete_range(START, END)
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"CanApi.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 78 (Flow A SinkLifecycle OnFrame+OnError+Dispose). Within +/-2 LoC tolerance.")
assert 76 <= delta <= 80, f"FAIL: delta {delta} outside +/-2 tolerance"
print("PASS.")
```

- [ ] **Step 5: Run deletion script**

```bash
python scripts/w26_task1_delete_sinklifecycle.py
wc -l src/PeakCan.Host.App/Services/Scripting/CanApi.cs
```

Expected: main file = 269 LoC ±2 (delta = 78).

- [ ] **Step 6: Build + filter test**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~CanApi|FullyQualifiedName~Script"
```

Expected: 0 errors, all filter tests pass. W20 LESSON APPLIED 24th time. Add using directives as needed (cp1252 binary pattern).

- [ ] **Step 7: Commit T1**

```bash
git add src/PeakCan.Host.App/Services/Scripting/CanApi/SinkLifecycle.partial.cs
git add src/PeakCan.Host.App/Services/Scripting/CanApi.cs
git add scripts/w26_task1_delete_sinklifecycle.py
git commit -m "W26 T1: extract SinkLifecycle partial (OnFrame(CanFrame) sink dispatcher 62 LoC LARGEST + OnError + Dispose, 347->269 main)"
```

---

## Task 3: W26 T2 — Extract CallbackRegistry partial (~80 LoC)

**Files:**
- NEW: `src/PeakCan.Host.App/Services/Scripting/CanApi/CallbackRegistry.partial.cs`
- MODIFY: `src/PeakCan.Host.App/Services/Scripting/CanApi.cs` (delete 4 registry methods, currently at ~L106-L187)

- [ ] **Step 1: Re-grep boundaries**

```bash
grep -n "public string OnFrame(Action\|public void OffFrame\|public string OnMessage\|public void OffMessage" src/PeakCan.Host.App/Services/Scripting/CanApi.cs
```

Expected: starts at `public string OnFrame(Action<CanFrame> callback)`, ends at closing `}` after OffMessage. Confirm 78-82 line range.

- [ ] **Step 2: Re-extract verbatim from HEAD** + write partial + deletion script
- [ ] **Step 3: Run deletion**: expected main file ≈ 187 LoC ±2 (delta ≈ 80)
- [ ] **Step 4: Build + filter test** (W20 LESSON APPLIED 25th time)
- [ ] **Step 5: Commit T2**

```bash
git add src/PeakCan.Host.App/Services/Scripting/CanApi/CallbackRegistry.partial.cs
git add src/PeakCan.Host.App/Services/Scripting/CanApi.cs
git commit -m "W26 T2: extract CallbackRegistry partial (4 registry mutators, 269->187 main)"
```

---

## Task 4: W26 T3 — Extract SendAndQuery partial (~50 LoC)

**Files:**
- NEW: `src/PeakCan.Host.App/Services/Scripting/CanApi/SendAndQuery.partial.cs`
- MODIFY: `src/PeakCan.Host.App/Services/Scripting/CanApi.cs` (delete Send + IsConnected + GetChannelId, currently at ~L64-L104 + L189 + L227)

- [ ] **Step 1: Re-grep boundaries**

```bash
grep -n "public async Task<bool> Send\|public bool IsConnected\|public string? GetChannelId" src/PeakCan.Host.App/Services/Scripting/CanApi.cs
```

Expected: ranges span ~L64 (Send xmldoc) ~L104 (Send end) + L189 (IsConnected) + L227 (GetChannelId). May need 3 separate re-greps.

- [ ] **Step 2: Re-extract verbatim from HEAD** (3 non-contiguous ranges) + write partial + deletion script
- [ ] **Step 3: Run deletion**: expected main file ≈ 137 LoC ±2 (delta ≈ 50)
- [ ] **Step 4: Build + filter test** (W20 LESSON APPLIED 26th time) — may require using-directive fix for `PeakCan.Host.Core` (CanFrame struct)
- [ ] **Step 5: Commit T3**

```bash
git add src/PeakCan.Host.App/Services/Scripting/CanApi/SendAndQuery.partial.cs
git add src/PeakCan.Host.App/Services/Scripting/CanApi.cs
git commit -m "W26 T3: extract SendAndQuery partial (Send + IsConnected + GetChannelId, 187->137 main)"
```

---

## Task 5: W26 T4 — Version bump + release notes

**Files:**
- MODIFY: `src/Directory.Build.props` (v3.39.5 → v3.40.0)
- NEW: `docs/release-notes-v3.40.0.md`

- [ ] **Step 1: Bump version**

```bash
sed -i 's|<Version>3.39.5</Version>|<Version>3.40.0</Version>|; s|<AssemblyVersion>3.39.5.0</AssemblyVersion>|<AssemblyVersion>3.40.0.0</AssemblyVersion>|; s|<FileVersion>3.39.5.0</FileVersion>|<FileVersion>3.40.0.0</FileVersion>|; s|<InformationalVersion>3.39.5</InformationalVersion>|<InformationalVersion>3.40.0</InformationalVersion>|' src/Directory.Build.props
```

- [ ] **Step 2: Write `docs/release-notes-v3.40.0.md`** with W3-W26 cumulative god-class trajectory, 12-section format per W22-W25 D7 sister.

- [ ] **Step 3: Commit T4**

```bash
git add src/Directory.Build.props
git add docs/release-notes-v3.40.0.md
git commit -m "W26 T4: v3.39.5 -> v3.40.0 MINOR + release notes"
```

---

## Task 6: W26 T5 — Tier-3 ship

- [ ] **Step 1: Push branch + create PR**

```bash
git push -u origin feature/w26-can-api-god-class
gh pr create --base main --head feature/w26-can-api-god-class --title "W26 MINOR: CanApi god-class refactor (22nd overall, 2nd App/Services/Scripting, -210 LoC main)" --body "Sister of W14 ScriptEngine + W25 ChannelRouter (multi-interface IFrameSink + IScriptCanApi). 22nd god-class refactor in W3-W26 series. 3 NEW partials in CanApi/ subdirectory: SinkLifecycle (OnFrame(CanFrame) sink dispatcher 62 LoC LARGEST + OnError + Dispose) + CallbackRegistry (4 registry mutators) + SendAndQuery (Send + IsConnected + GetChannelId). All 11 [LoggerMessage] declarations stay on main partial per W18+W22+W23+W25 sister precedent. Tests pass without modification."
```

- [ ] **Step 2: Wait for CI**

```bash
gh pr checks --watch
```

Expected: 1+ retries due to pre-existing transient flaky `ReplayServiceTests.SetSpeed_PreservesCurrentTimestamp` (W13 T1 + W14-W24 sister pattern) — keep re-running until SUCCESS (per W25 5-attempt precedent).

- [ ] **Step 3: Squash-merge + delete branch**

```bash
gh pr merge --squash --delete-branch
```

- [ ] **Step 4: Tag + GH release**

```bash
git checkout main
git pull
git tag -a v3.40.0 -m "W26 MINOR - CanApi god-class refactor (22nd overall, -210 LoC main, 3 NEW partials in CanApi/)"
git push origin v3.40.0
gh release create v3.40.0 --target main --title "v3.40.0 MINOR - CanApi god-class refactor" --notes-file docs/release-notes-v3.40.0.md
```

- [ ] **Step 5: Post-PR docs commit**

Per W19 D6 + W20 D6 + W21 D6 + W22 D6 + W23 D6 + W24 D6 + W25 D6 sister pattern: write `docs/superpowers/capture-decisions/2026-07-12-w26-can-api-god-class-ship.md` and commit it to main as a separate post-PR docs commit.

---

## Verification matrix

| Check | Expected | Sister |
|---|---|---|
| `dotnet build src/PeakCan.Host.App/` | 0 errors, 0 warnings | W20+W22+W24+W25 |
| `dotnet test --filter "~CanApi\|~Script"` | All pass | W20+W24 |
| `dotnet test` (full solution) | 0 new fails | W13 T1 sister |
| `wc -l src/.../CanApi.cs` | ≤ 150 LoC (target ~137) | W8.5 D7 ±2 |
| 3 NEW partial files in `CanApi/` | YES | W12-W25 |
| 7 readonly fields + 1 ctor + 11 [LoggerMessage] stay in main | YES | W18+W22+W23+W25 |
| DI registration unchanged | YES | W12-W25 |
| `IFrameSink` + `IScriptCanApi` interfaces unchanged | YES | W12-W25 |
| V8 scripting bindings unchanged | YES (can.send + can.onFrame + can.onMessage) | W14 sister |
| Tag v3.40.0 + GH release published | YES | W12-W25 |
| Branch deleted post-merge | YES | W12-W25 |
| capture-decisions file landing on main | YES | W19-W25 |

## Out of scope (YAGNI)

- No public/internal API change.
- No test changes (CanApi tests + Script tests pass without modification).
- No facade pattern.
- No xmldoc-grep risk.
- No CS8795 risk (D4 keeps all 11 [LoggerMessage] partials on main partial declaration).
- No `AttachSink(this)` relocation (stays in ctor per D3 + D5 sister).
- No V8 scripting API surface change.
- No `IScriptCanApi` interface signature change.
- No `IFrameSink` interface signature change.
