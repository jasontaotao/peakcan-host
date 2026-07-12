# W25 PLAN — ChannelRouter god-class refactor (21st overall)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract 3 NEW partial-class files from `src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs` (305 → ~162 LoC) in line with the W3-W24 god-class refactor series.

**Architecture:** Sister pattern of W18 PeakCanChannel (Infrastructure/Channel layer precedent). 21st god-class refactor. 6th Infrastructure-layer + 15th subdirectory-pattern deployment. Largest-first order: C (FrameRouting, 75 LoC) → B (Sinks, 46 LoC) → A (Channels, 22 LoC).

**Tech Stack:** C# .NET 10 partial-class split pattern (W3-W24 series), `CommunityToolkit.Mvvm` N/A (no `[ObservableProperty]` / `[RelayCommand]` in ChannelRouter), `Microsoft.Extensions.Logging.Abstractions` + `ImmutableArray<T>` (stdlib).

## Global Constraints

- **LoC formula**: W8.5 D7 ±2 LoC tolerance at each task boundary (W13 T1 2/3 loose-assertion sister). Re-grep + range verify after each task per W19 R1 first-correction.
- **Verbatim re-extraction**: W20 T2 R1 fabrication LESSON — every partial's content MUST come from `git show HEAD:src/...cs | sed -n '<range>p'`. NEVER hand-reconstruct method bodies.
- **Struct-ctor verification**: W23 LESSON — verify `Array.Copy(source, sourceIndex, destination, destinationIndex, length)` 5-arg signature + `Volatile.Read<T>(ref T)` + `Volatile.Write<T>(ref T, T)` signatures.
- **`[LoggerMessage]` partials stay on main partial declaration**: W18 R1 + W22 D4 + W23 D3 D4 sister precedent — 3 `LogSinkOnError` + `LogChannelRouterSinkOnFrameFailed` + `LogDetachSinkFailed` declarations stay on main partial to avoid CS8795.
- **Branch name**: `feature/w25-channel-router-god-class`.
- **Target version**: v3.39.0 MINOR.

---

## File structure

- NEW: `src/PeakCan.Host.Infrastructure/Channel/ChannelRouter/FrameRouting.partial.cs` (T1, ~75 LoC)
- NEW: `src/PeakCan.Host.Infrastructure/Channel/ChannelRouter/Sinks.partial.cs` (T2, ~46 LoC)
- NEW: `src/PeakCan.Host.Infrastructure/Channel/ChannelRouter/Channels.partial.cs` (T3, ~22 LoC)
- MODIFY: `src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs` (T1+T2+T3 deletions + final state ~162 LoC)
- MODIFY: `src/Directory.Build.props` (v3.38.0 → v3.39.0, T4)
- NEW: `docs/release-notes-v3.39.0.md` (T4, ~110 LoC)
- NEW: `scripts/w25_task1_delete_framerouting.py` (T1)
- NEW: `scripts/w25_task2_delete_sinks.py` (T2)
- NEW: `scripts/w25_task3_delete_channels.py` (T3)
- NEW: `docs/superpowers/capture-decisions/2026-07-12-w25-channel-router-god-class-ship.md` (post-PR docs commit)

---

## Task 1: W25 T0.5 — Branch + baseline verification

**Files**: `docs/superpowers/specs/...` (already on branch from `git checkout -b` at session start), `docs/superpowers/plans/...` (write this file first), tests baseline.

**Interfaces**:
- Consumes: `ChannelRouter.cs` at v3.38.0 main
- Produces: SPEC + PLAN committed to branch, baseline 14 ChannelRouter tests pass.

- [ ] **Step 1: Write the W25 PLAN** (this file)
- [ ] **Step 2: Verify baseline build + tests**

```bash
dotnet build src/PeakCan.Host.Infrastructure/PeakCan.Host.Infrastructure.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.Infrastructure.Tests/PeakCan.Host.Infrastructure.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~ChannelRouter" --logger "console;verbosity=minimal"
```

Expected: 14/14 ChannelRouter tests pass, 0 build errors.

- [ ] **Step 3: Commit SPEC + PLAN**

```bash
git add docs/superpowers/specs/2026-07-12-channel-router-god-class-refactor.md
git commit -m "W25 spec: ChannelRouter god-class refactor (21st overall, 6th Infrastructure layer, 15th subdirectory-pattern deployment)"
git add docs/superpowers/plans/2026-07-12-channel-router-god-class-refactor.md
git commit -m "W25 plan: ChannelRouter god-class refactor (3 partials: FrameRouting + Sinks + Channels, largest-first C→B→A order)"
```

---

## Task 2: W25 T1 — Extract FrameRouting partial (75 LoC, LARGEST)

**Files:**
- NEW: `src/PeakCan.Host.Infrastructure/Channel/ChannelRouter/FrameRouting.partial.cs`
- MODIFY: `src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs:183-255` (delete OnChannelFrame method)

**Interfaces:**
- Consumes: `ChannelRouter.cs` at lines 183-255 (OnChannelFrame method body) from HEAD.
- Produces: `ChannelRouter/FrameRouting.partial.cs` containing `partial class ChannelRouter : IFrameSource` with `private void OnChannelFrame(CanFrame frame)` method.

- [ ] **Step 1: Re-grep boundaries of FrameRouting method exactly**

```bash
grep -n "private void OnChannelFrame" src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs
grep -n "^}" src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs | head -30
```

Expected: starts at "private void OnChannelFrame(CanFrame frame)" line; ends at matching `^}` closing brace line. Confirm 73-75 line range.

- [ ] **Step 2: Re-extract verbatim from HEAD**

```bash
START_LINE=183  # update from Step 1
END_LINE=255    # update from Step 1
git show HEAD:src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs | sed -n "${START_LINE},${END_LINE}p"
```

Expected: exact verbatim OnChannelFrame method body.

- [ ] **Step 3: Write FrameRouting.partial.cs**

```csharp
// ChannelRouter/FrameRouting.partial.cs — W25 T1
// Hot-path frame dispatcher: fans a CanFrame out to all registered
// IFrameSink instances with per-sink exception isolation. On a sink
// that throws, logs the original OnFrame exception at Warning
// (EventId 6010) BEFORE invoking OnError. If OnError itself throws,
// logs the secondary exception (EventId 6004) and auto-detaches the
// misbehaving sink via DetachSink (with DetachSink-failure now
// contained — see v3.14.0 MINOR A5 wrapper).
//
// All 3 [LoggerMessage] declarations (LogChannelRouterSinkOnFrameFailed
// + LogSinkOnError + LogDetachSinkFailed) stay on ChannelRouter.cs per
// W18 R1 + W22 D4 + W23 D4 sister precedent (CS8795 mitigation).
//
// Sister of the W18 PeakCanChannel LayerFlow.cs pattern: same
// Infrastructure/Channel namespace, same fan-out-with-error-isolation
// concern shape.

using Microsoft.Extensions.Logging;

namespace PeakCan.Host.Infrastructure.Channel;

public sealed partial class ChannelRouter
{
    // <verbatim OnChannelFrame body from Step 2, including xmldoc>
}
```

Add `using` directives as needed (likely `Microsoft.Extensions.Logging` already on main).

- [ ] **Step 4: Write `scripts/w25_task1_delete_framerouting.py`** with W13 T1 2/3 loose assertion + W17 wc-l-splitlines CONFIRMED pattern.

```python
import sys
from pathlib import Path

p = Path("src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs")
text = p.read_text()
lines = text.splitlines(keepends=True)

# ranges are 1-indexed INCLUSIVE; convert to 0-indexed exclusive
def delete_range(start, end):
    return lines[:start - 1] + lines[end:]

# Match the FrameRouting region
# Re-grep boundaries per W19 R1 first-correction
START, END = 183, 255  # UPDATE per Step 1 grep result
before = len(lines)
new_lines = delete_range(START, END)
new_text = "".join(new_lines)
p.write_text(new_text)

after_count = sum(1 for _ in Path(p).read_text().splitlines())
print(f"ChannelRouter.cs: {before} -> {after_count} (delta = {before - after_count})")
print(f"Expected delta: 75 (Flow C FrameRouting). Within ±2 LoC tolerance.")
```

- [ ] **Step 5: Run deletion script**

```bash
python scripts/w25_task1_delete_framerouting.py
git diff src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs | head -10
wc -l src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs
```

Expected: main file = 230 LoC ±2 (delta = 75). Build MUST be clean.

- [ ] **Step 6: Build + filter test**

```bash
dotnet build src/PeakCan.Host.Infrastructure/PeakCan.Host.Infrastructure.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.Infrastructure.Tests/PeakCan.Host.Infrastructure.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~ChannelRouter" --logger "console;verbosity=minimal"
```

Expected: 0 errors, 14/14 tests pass. W20 LESSON APPLIED 21st time.

- [ ] **Step 7: Commit T1**

```bash
git add src/PeakCan.Host.Infrastructure/Channel/ChannelRouter/FrameRouting.partial.cs
git add src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs
git commit -m "W25 T1: extract FrameRouting partial (OnChannelFrame, 75 LoC LARGEST, 305->230 main)"
```

---

## Task 3: W25 T2 — Extract Sinks partial (46 LoC)

**Files:**
- NEW: `src/PeakCan.Host.Infrastructure/Channel/ChannelRouter/Sinks.partial.cs`
- MODIFY: `src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs` (delete AttachSink + DetachSink methods, now ~L137-181 in original)

- [ ] **Step 1: Re-grep boundaries**

```bash
grep -n "public void AttachSink" src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs
grep -n "public void DetachSink" src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs
grep -n "^}" src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs | head -30
```

Expected: starts at "public void AttachSink" line, ends at closing `^}` after DetachSink. Confirm 44-46 line range.

- [ ] **Step 2: Re-extract verbatim from HEAD**

```bash
START_LINE=137  # update from Step 1
END_LINE=181    # update from Step 1
git show HEAD:src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs | sed -n "${START_LINE},${END_LINE}p"
```

- [ ] **Step 3: Write Sinks.partial.cs**

```csharp
// ChannelRouter/Sinks.partial.cs — W25 T2
// Sink-list registration: AttachSink (idempotent add + Volatile.Write
// release-fence publish) + DetachSink (idempotent remove + array
// rebuild + null publish when last sink removed). Both gated by
// the channel-router lock and use Volatile.Write to publish the new
// _sinks array — OnChannelFrame (in FrameRouting.partial.cs) reads
// it via Volatile.Read acquire-fence.
//
// Zero-allocation immutable-snapshot pattern documented at
// ChannelRouter.cs L28-42 (v1.2.3 + v3.5.7 PATCH).

using System.Collections.Immutable;

namespace PeakCan.Host.Infrastructure.Channel;

public sealed partial class ChannelRouter
{
    // <verbatim AttachSink + DetachSink bodies from Step 2, including xmldoc>
}
```

Add `using System.Collections.Immutable;` if needed.

- [ ] **Step 4: Write `scripts/w25_task2_delete_sinks.py`** with same pattern as Task 2.
- [ ] **Step 5: Run deletion**

```bash
python scripts/w25_task2_delete_sinks.py
wc -l src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs
```

Expected: main file = 184 LoC ±2 (delta = 46).

- [ ] **Step 6: Build + filter test**

```bash
dotnet build src/PeakCan.Host.Infrastructure/PeakCan.Host.Infrastructure.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.Infrastructure.Tests/PeakCan.Host.Infrastructure.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~ChannelRouter"
```

Expected: 0 errors, 14/14 tests pass. W20 LESSON APPLIED 22nd time.

- [ ] **Step 7: Commit T2**

```bash
git add src/PeakCan.Host.Infrastructure/Channel/ChannelRouter/Sinks.partial.cs
git add src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs
git commit -m "W25 T2: extract Sinks partial (AttachSink + DetachSink, 46 LoC, 230->184 main)"
```

---

## Task 4: W25 T3 — Extract Channels partial (22 LoC)

**Files:**
- NEW: `src/PeakCan.Host.Infrastructure/Channel/ChannelRouter/Channels.partial.cs`
- MODIFY: `src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs` (delete RegisterChannel + UnregisterChannel methods, now ~L112-134 in original)

- [ ] **Step 1: Re-grep boundaries**

```bash
grep -n "public void RegisterChannel" src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs
grep -n "public void UnregisterChannel" src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs
grep -n "^}" src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs | head -30
```

Expected: starts at "public void RegisterChannel" line, ends at closing `^}` after UnregisterChannel. Confirm 22-24 line range.

- [ ] **Step 2: Re-extract verbatim from HEAD**

```bash
START_LINE=112  # update from Step 1
END_LINE=134    # update from Step 1
git show HEAD:src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs | sed -n "${START_LINE},${END_LINE}p"
```

- [ ] **Step 3: Write Channels.partial.cs**

```csharp
// ChannelRouter/Channels.partial.cs — W25 T3
// Channel-list registration: RegisterChannel (idempotent add +
// FrameReceived subscription) + UnregisterChannel (idempotent remove
// + FrameReceived unsubscription). Both gated by the channel-router
// lock; OnChannelFrame (in FrameRouting.partial.cs) is the delegate
// target registered here.

namespace PeakCan.Host.Infrastructure.Channel;

public sealed partial class ChannelRouter
{
    // <verbatim RegisterChannel + UnregisterChannel bodies from Step 2, including xmldoc>
}
```

- [ ] **Step 4: Write `scripts/w25_task3_delete_channels.py`**
- [ ] **Step 5: Run deletion**

```bash
python scripts/w25_task3_delete_channels.py
wc -l src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs
```

Expected: main file = 162 LoC ±2 (delta = 22).

- [ ] **Step 6: Build + filter test**

```bash
dotnet build src/PeakCan.Host.Infrastructure/PeakCan.Host.Infrastructure.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.Infrastructure.Tests/PeakCan.Host.Infrastructure.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~ChannelRouter"
```

Expected: 0 errors, 14/14 tests pass. W20 LESSON APPLIED 23rd time.

- [ ] **Step 7: Commit T3**

```bash
git add src/PeakCan.Host.Infrastructure/Channel/ChannelRouter/Channels.partial.cs
git add src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs
git commit -m "W25 T3: extract Channels partial (RegisterChannel + UnregisterChannel, 22 LoC, 184->162 main)"
```

---

## Task 5: W25 T4 — Version bump + release notes

**Files:**
- MODIFY: `src/Directory.Build.props` (v3.38.0 → v3.39.0)
- NEW: `docs/release-notes-v3.39.0.md`

- [ ] **Step 1: Bump version**

```bash
sed -i 's|<Version>3.38.0</Version>|<Version>3.39.0</Version>|' src/Directory.Build.props
sed -i 's|<AssemblyVersion>3.38.0.0</AssemblyVersion>|<AssemblyVersion>3.39.0.0</AssemblyVersion>|' src/Directory.Build.props
sed -i 's|<FileVersion>3.38.0.0</FileVersion>|<FileVersion>3.39.0.0</FileVersion>|' src/Directory.Build.props
sed -i 's|<InformationalVersion>3.38.0</InformationalVersion>|<InformationalVersion>3.39.0</InformationalVersion>|' src/Directory.Build.props
```

- [ ] **Step 2: Write `docs/release-notes-v3.39.0.md`** with W3-W24 + W25 cumulative god-class trajectory, 12-section format per W12-W24 D7 sister.

- [ ] **Step 3: Commit T4**

```bash
git add src/Directory.Build.props
git add docs/release-notes-v3.39.0.md
git commit -m "W25 T4: v3.38.0 -> v3.39.0 MINOR + release notes"
```

---

## Task 6: W25 T5 — Tier-3 ship

- [ ] **Step 1: Push branch + create PR**

```bash
git push -u origin feature/w25-channel-router-god-class
gh pr create --base main --head feature/w25-channel-router-god-class --title "W25 MINOR: ChannelRouter god-class refactor (21st overall, 6th Infrastructure/Channel, -143 LoC -46.9% main)" --body "Sister of W18 PeakCanChannel. 21st god-class refactor in W3-W25 series. 3 NEW partials in ChannelRouter/ subdirectory: FrameRouting (75 LoC OnChannelFrame) + Sinks (46 LoC Attach/Detach) + Channels (22 LoC Register/Unregister). All 3 [LoggerMessage] declarations stay on main partial per W18 R1 + W22 D4 + W23 D4 sister precedent. 14 ChannelRouter tests pass without modification."
```

- [ ] **Step 2: Wait for CI**

```bash
gh pr checks --watch
```

Expected: all checks green.

- [ ] **Step 3: Squash-merge + delete branch**

```bash
gh pr merge --squash --delete-branch
```

- [ ] **Step 4: Tag + GH release**

```bash
git checkout main
git pull
git tag -a v3.39.0 -m "W25 MINOR — ChannelRouter god-class refactor (21st overall, -143 LoC main, 3 NEW partials)"
git push origin v3.39.0
gh release create v3.39.0 --target main --title "v3.39.0 MINOR — ChannelRouter god-class refactor" --notes-file docs/release-notes-v3.39.0.md
```

- [ ] **Step 5: Post-PR docs commit**

Per W19 D6 + W20 D6 + W21 D6 + W22 D6 + W23 D6 + W24 D6 sister pattern: write `docs/superpowers/capture-decisions/2026-07-12-w25-channel-router-god-class-ship.md` and commit it to main as a separate post-PR docs commit.

---

## Verification matrix

| Check | Expected | Sister |
|---|---|---|
| `dotnet build src/PeakCan.Host.Infrastructure/` | 0 errors, 0 warnings | W20+W24 |
| `dotnet test --filter "~ChannelRouter"` | 14/14 PASS | W20+W24 |
| `dotnet test` (full solution) | 0 new fails | W13 T1 sister pattern |
| `wc -l src/.../ChannelRouter.cs` | ≤ 170 LoC | W8.5 D7 ±2 |
| 3 NEW partial files in `ChannelRouter/` | YES | W12-W24 |
| 4 readonly fields + 1 ctor + 3 [LoggerMessage] stay in main | YES | W18+W22+W23 |
| DI registration unchanged | YES | W12-W24 |
| `IFrameSource` interface unchanged | YES | W12-W24 |
| Tag v3.39.0 + GH release published | YES | W12-W24 |
| Branch deleted post-merge | YES | W12-W24 |
| capture-decisions file landing on main | YES | W19-W24 |

## Out of scope (YAGNI)

- No public/internal API change.
- No test changes (14 ChannelRouterTests + sister tests pass without modification).
- No facade pattern.
- No xmldoc-grep risk.
- No CS8795 risk (D4 keeps [LoggerMessage] partials on main partial declaration).
- No `IFrameSource` interface visibility change.
- No `PeakCanChannel.cs` (W18) partial changes.
