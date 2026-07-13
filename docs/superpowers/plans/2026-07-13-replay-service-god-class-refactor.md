# W31 Implementation Plan — ReplayService god-class refactor (27th overall, 7th Core-layer)

**Goal**: Extract 2 NEW partial-class files (FileIoLifecycle + FrameEmission) from `src/PeakCan.Host.Core/Replay/ReplayService.cs` 265 → ~146 LoC (-119 LoC, -44.9%). Public API + existing tests unchanged.

**Architecture**: Sister pattern of W22 + W23 + W27 + W28 + W29 + W30 (subdirectory + non-suffix `.partial.cs` filenames). 27th god-class refactor. **7th Core-layer god-class** (1st new Core-layer since W25 ChannelRouter) + **21st subdirectory-pattern deployment** + **5th multi-interface partial-class extraction** (sister of W26 CanApi `IFrameSink + IScriptCanApi`).

**Tech Stack**: C# .NET 10 partial-class split pattern + W29 NEW `small-god-class-no-largest-method-keeps-all-inline-default-pattern` (D5 default sister-principle, NO W25 D5 deviation since LARGEST method 31 LoC < 60 LoC threshold) + W19 R1 first-correction + W20 verbatim re-extraction + W23 STRUCT-FABRICATION LESSON + W18 + W22 + W23 + W25 + W26 + W27 + W28 + W29 + W30 `[LoggerMessage]` cross-partial sister-pattern (CS8795 mitigation)

**Spec**: [2026-07-13-replay-service-god-class-refactor.md](../specs/2026-07-13-replay-service-god-class-refactor.md)

---

## Global Constraints

- 1 partial class with 2 partials in subdirectory `src/PeakCan.Host.Core/Replay/ReplayService/`
- `public sealed partial class ReplayService : IReplayService, IDisposable` (already partial at L11; no D2 application needed per W21 + W26.5 + W30 sister precedent)
- All public API surface unchanged (DI registration + tests + interface contracts)
- LoC formula `main_after = main_before - delete_count` (W8.5 D7 32-locked, no +1 offset)
- W17 wc-l-splitlines CONFIRMED + cp1252 binary read+write pattern (since file is Windows-1252 encoded)
- W19 R1 first-correction: re-grep boundaries BEFORE each deletion script
- W20 LESSON: verbatim re-extraction via `git show main:src/.../ReplayService.cs | sed -n '<range>p'`
- W23 STRUCT-FABRICATION LESSON: verify `Task.Run(Func<Task>)` async signature + `_sink.SendFrameAsync` signature + `_timeline.Pause()` signature
- W29 NEW `small-god-class-no-largest-method-keeps-all-inline-default-pattern`: LARGEST method 31 LoC < 50 LoC threshold → default D5 sister-principle applied (NO W25 D5 deviation)
- W26.5 + W21 + W30 partial-keyword already-applied: no D2 application needed

---

## Task T0: Branch + SPEC + PLAN commits

**Files:**
- Create: `docs/superpowers/specs/2026-07-13-replay-service-god-class-refactor.md`
- Create: `docs/superpowers/plans/2026-07-13-replay-service-god-class-refactor.md`

**Interfaces:**
- Produces: SPEC + PLAN committed to feature branch

- [ ] **Step 1: Branch + verify already partial**

```bash
git checkout -b feature/w31-replay-service-god-class main
grep -n "public sealed" src/PeakCan.Host.Core/Replay/ReplayService.cs | head -3
```

Expected: `11:public sealed partial class ReplayService : IReplayService, IDisposable` (already partial per W21 + W26.5 + W30 sister precedent; no D2 application needed).

- [ ] **Step 2: Build + tests baseline (no source change yet)**

```bash
dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug --no-restore 2>&1 | tail -5
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~ReplayService" --logger "console;verbosity=minimal" 2>&1 | tail -5
```

Expected: 0 errors, ReplayService tests pass (baseline).

- [ ] **Step 3: Commit SPEC + PLAN**

```bash
git add docs/superpowers/specs/2026-07-13-replay-service-god-class-refactor.md
git commit -m "W31 spec: ReplayService god-class refactor (2 partials + 5-task roll-out, 27th overall, 7th Core-layer, 1st new Core-layer since W25 ChannelRouter, 21st subdirectory-pattern deployment)"
git add docs/superpowers/plans/2026-07-13-replay-service-god-class-refactor.md
git commit -m "W31 plan: ReplayService god-class refactor (2 partials: FileIoLifecycle + FrameEmission)"
```

---

## Task T1: FileIoLifecycle partial extraction (~50 LoC)

**Files:**
- Create: `src/PeakCan.Host.Core/Replay/ReplayService/FileIoLifecycle.partial.cs` (~80 LoC)
- Modify: `src/PeakCan.Host.Core/Replay/ReplayService.cs` (delete L152-L182 (LoadAsync) + L191-L209 (Reset) = 50 LoC)

**Interfaces:**
- Consumes: `ReplayService` partial-class visibility (private fields + ctor + properties)
- Produces: 2 public methods `LoadAsync` + `Reset` extracted to FileIoLifecycle.partial.cs

- [ ] **Step 1: Re-grep boundaries BEFORE running deletion script (W19 R1 first-correction)**

```bash
grep -n "public async Task LoadAsync\|public void Reset\|private void EmitFrame" src/PeakCan.Host.Core/Replay/ReplayService.cs
```

Expected:
```
152:    public async Task LoadAsync(string path, CancellationToken ct = default)
204:    public void Reset()
211:    private void EmitFrame(ReplayFrame frame)
```

- [ ] **Step 2: Verbatim re-extract LoadAsync + Reset from main HEAD (W20 LESSON 37th application)**

```bash
git show main:src/PeakCan.Host.Core/Replay/ReplayService.cs | sed -n '152,182p'  # LoadAsync body
git show main:src/PeakCan.Host.Core/Replay/ReplayService.cs | sed -n '191,209p'  # Reset xmldoc + body
```

Expected: 31 LoC for LoadAsync body (no xmldoc) + 19 LoC for Reset (xmldoc + body).

- [ ] **Step 3: Write FileIoLifecycle.partial.cs (~80 LoC)**

```csharp
// ReplayService/FileIoLifecycle.partial.cs — W31 T1 (Flow A, 50 LoC)
// File-IO lifecycle methods: LoadAsync (file open + AscParser.Parse +
// defensive-reset-on-entry + exception-wrapping) + Reset (state clear +
// timeline.Stop + frame buffer reset). Both touch _frames + _timeline
// + state-management. Sister of W22 RecordService/Lifecycle + W27
// RecentSessionsService/PersistenceOps + W28 DbcService/LoadLifecycle
// file-IO lifecycle sister-pattern.
//
// Cross-partial caller pattern (W22+W23+W24+W25+W26+W27+W28+W29+W30 sister):
// LoadAsync reads ASC file + parses frames + delegates timeline.SetFrames;
// Reset clears state + delegates timeline.Stop + SetFrames. Both methods
// touch the same _frames + _timeline private fields.
//
// W31 T1 verbatim re-extracted via `git show main:src/.../ReplayService.cs | sed -n '152,182p;191,209p'`
// per W20 T2 R1 fabrication LESSON (37th application).

using PeakCan.Host.Core.Path;

namespace PeakCan.Host.Core.Replay;

public sealed partial class ReplayService
{
    public async Task LoadAsync(string path, CancellationToken ct = default)
    {
        // ... [verbatim content from main HEAD L152-L182]
    }

    /// <summary>
    /// v3.8.4 PATCH H2: drop the loaded frame buffer and reset the
    /// ... [verbatim xmldoc + content from main HEAD L191-L209]
    /// </summary>
    public void Reset()
    {
        // ... [verbatim content from main HEAD L204-L209]
    }
}
```

- [ ] **Step 4: Write deletion script + run + verify**

```python
# scripts/w31_task1_delete_fileiolifecycle.py — W19 R1 first-correction + W17 wc-l-splitlines CONFIRMED
from pathlib import Path

p = Path("src/PeakCan.Host.Core/Replay/ReplayService.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

# W31 has 2 non-contiguous regions to delete:
# 1. LoadAsync: L152-L182 (31 LoC)
# 2. Reset: L191-L209 (19 LoC)
# Process in reverse order (highest line first) to keep line numbers stable
START_2, END_2 = 191, 209  # Reset (process first)
new_lines = lines[:START_2 - 1] + lines[END_2:]
START_1, END_1 = 152, 182  # LoadAsync (then this)
new_lines = new_lines[:START_1 - 1] + new_lines[END_1:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

before = 265
after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"ReplayService.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 50 (LoadAsync 31 + Reset 19). Within ±2 LoC tolerance.")
assert 48 <= delta <= 52, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
```

- [ ] **Step 5: Build + tests to verify T1 extraction**

```bash
dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug --no-restore 2>&1 | tail -5
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~ReplayService" --logger "console;verbosity=minimal" 2>&1 | tail -5
```

Expected: 0 errors, ReplayService tests pass without modification.

- [ ] **Step 6: Commit T1**

```bash
git add src/PeakCan.Host.Core/Replay/ReplayService.cs src/PeakCan.Host.Core/Replay/ReplayService/FileIoLifecycle.partial.cs scripts/w31_task1_delete_fileiolifecycle.py
git commit -m "W31 T1: extract FileIoLifecycle partial (LoadAsync 31 LoC + Reset 6 LoC + xmldoc, -50 LoC main, 265 -> 215)"
```

---

## Task T2: FrameEmission partial extraction (~69 LoC)

**Files:**
- Create: `src/PeakCan.Host.Core/Replay/ReplayService/FrameEmission.partial.cs` (~95 LoC)
- Modify: `src/PeakCan.Host.Core/Replay/ReplayService.cs` (delete L122-L129 + L131-L145 + L211-L249 + L251-L257 = 69 LoC, processed in reverse order)

**Interfaces:**
- Consumes: `ReplayService` partial-class visibility (private fields + ctor + properties)
- Produces: 4 private helpers `EmitFrame` + `EmitFrameToSinkAsync` + `OnSinkThrewFromTimeline` + `RaisePlaybackEnded` extracted to FrameEmission.partial.cs

- [ ] **Step 1: Re-grep boundaries AFTER T1 deletion (W19 R1 first-correction)**

```bash
grep -n "private void RaisePlaybackEnded\|private void OnSinkThrewFromTimeline\|private void EmitFrame\|private async Task EmitFrameToSinkAsync\|public sealed partial class\|^}" src/PeakCan.Host.Core/Replay/ReplayService.cs
```

Expected: 4 helper method line numbers (post-T1 shift = -50 LoC) + closing `}` of class.

- [ ] **Step 2: Verbatim re-extract 4 helpers from main HEAD (W20 LESSON 38th application)**

```bash
git show main:src/PeakCan.Host.Core/Replay/ReplayService.cs | sed -n '122,129p'   # RaisePlaybackEnded
git show main:src/PeakCan.Host.Core/Replay/ReplayService.cs | sed -n '131,145p'   # OnSinkThrewFromTimeline
git show main:src/PeakCan.Host.Core/Replay/ReplayService.cs | sed -n '211,249p'   # EmitFrame
git show main:src/PeakCan.Host.Core/Replay/ReplayService.cs | sed -n '251,257p'   # EmitFrameToSinkAsync
```

Expected: 8 LoC for RaisePlaybackEnded (xmldoc + body) + 15 LoC for OnSinkThrewFromTimeline + 39 LoC for EmitFrame + 7 LoC for EmitFrameToSinkAsync = 69 LoC total.

- [ ] **Step 3: Write FrameEmission.partial.cs (~95 LoC)**

```csharp
// ReplayService/FrameEmission.partial.cs — W31 T2 (Flow B, 69 LoC)
// Frame-emission helpers: EmitFrame (filter check + Task.Run fire-and-forget
// sink dispatch + FrameEmitted event raise) + EmitFrameToSinkAsync (await
// _sink.SendFrameAsync) + OnSinkThrewFromTimeline (capture-first-exception +
// timeline.Pause + RaisePlaybackEnded) + RaisePlaybackEnded (forward
// PlaybackEnded event).
//
// Cross-partial caller pattern: ctor (in main) passes EmitFrame as a
// delegate to ReplayTimeline ctor — partial-class cross-partial visibility
// handles this automatically (sister of W22+W23+W24+W25+W26+W27+W28+W29+W30).
//
// W23 STRUCT-FABRICATION LESSON: Task.Run(Func<Task>) async signature +
// _sink.SendFrameAsync(ReplayFrame, CancellationToken) signature +
// _timeline.Pause() signature verified during verbatim re-extraction.
//
// W31 T2 verbatim re-extracted via `git show main:src/.../ReplayService.cs | sed -n '122,129p;131,145p;211,249p;251,257p'`
// per W20 T2 R1 fabrication LESSON (38th application).

using Microsoft.Extensions.Logging;

namespace PeakCan.Host.Core.Replay;

public sealed partial class ReplayService
{
    /// <summary>
    /// Forwards the timeline's playback-ended callback to the public event.
    /// ... [verbatim xmldoc + body from main HEAD L122-L129]
    /// </summary>
    private void RaisePlaybackEnded(PlaybackEndedEventArgs args)
        => PlaybackEnded?.Invoke(this, args);

    /// <summary>
    /// v1.4.2 PATCH Item 3: invoked by the timeline when a sink callback
    /// ... [verbatim xmldoc + body from main HEAD L131-L145]
    /// </summary>
    private void OnSinkThrewFromTimeline(Exception ex)
    {
        // ... [verbatim content]
    }

    private void EmitFrame(ReplayFrame frame)
    {
        // ... [verbatim content from main HEAD L211-L249]
    }

    // v3.14.0 MINOR A6: ... [verbatim comment + body from main HEAD L251-L257]
    private async Task EmitFrameToSinkAsync(ReplayFrame frame)
    {
        await _sink.SendFrameAsync(frame, CancellationToken.None).ConfigureAwait(false);
    }
}
```

- [ ] **Step 4: Write deletion script + run + verify**

```python
# scripts/w31_task2_delete_frameemission.py
from pathlib import Path

p = Path("src/PeakCan.Host.Core/Replay/ReplayService.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

# W31 has 4 non-contiguous regions to delete:
# 1. RaisePlaybackEnded: L122-L129 (8 LoC)
# 2. OnSinkThrewFromTimeline: L131-L145 (15 LoC)
# 3. EmitFrame: L211-L249 (39 LoC)
# 4. EmitFrameToSinkAsync: L251-L257 (7 LoC)
# Process in reverse order (highest line first) to keep line numbers stable

# First pass: delete EmitFrameToSinkAsync (L251-L257)
new_lines = lines[:250] + lines[257:]
# Second pass: delete EmitFrame (now at L211-L249 after first pass)
new_lines = new_lines[:210] + new_lines[249:]
# Third pass: delete OnSinkThrewFromTimeline (now at L131-L145 after second pass)
new_lines = new_lines[:130] + new_lines[145:]
# Fourth pass: delete RaisePlaybackEnded (now at L122-L129 after third pass)
new_lines = new_lines[:121] + new_lines[129:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

before = 215
after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"ReplayService.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 69 (4 helpers: RaisePlaybackEnded 8 + OnSinkThrewFromTimeline 15 + EmitFrame 39 + EmitFrameToSinkAsync 7). Within ±2 LoC tolerance.")
assert 67 <= delta <= 71, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
```

- [ ] **Step 5: Build + tests to verify T2 extraction**

```bash
dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug --no-restore 2>&1 | tail -5
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~ReplayService" --logger "console;verbosity=minimal" 2>&1 | tail -5
```

Expected: 0 errors, ReplayService tests pass without modification.

- [ ] **Step 6: Commit T2**

```bash
git add src/PeakCan.Host.Core/Replay/ReplayService.cs src/PeakCan.Host.Core/Replay/ReplayService/FrameEmission.partial.cs scripts/w31_task2_delete_frameemission.py
git commit -m "W31 T2: extract FrameEmission partial (EmitFrame 39 LoC + 3 small helpers, -69 LoC main, 215 -> 146)"
```

---

## Task T3: v3.44.5 → v3.45.0 MINOR + release notes

**Files:**
- Modify: `src/Directory.Build.props` (4-field version bump)
- Create: `docs/release-notes-v3.45.0.md` (~120 LoC)

- [ ] **Step 1: Bump Directory.Build.props**

```bash
python -c "
from pathlib import Path
p = Path('src/Directory.Build.props')
text = p.read_text(encoding='cp1252')
text = text.replace('<Version>3.44.5</Version>', '<Version>3.45.0</Version>', 1)
text = text.replace('<AssemblyVersion>3.44.5.0</AssemblyVersion>', '<AssemblyVersion>3.45.0.0</AssemblyVersion>', 1)
text = text.replace('<FileVersion>3.44.5.0</FileVersion>', '<FileVersion>3.45.0.0</FileVersion>', 1)
text = text.replace('<InformationalVersion>3.44.5</InformationalVersion>', '<InformationalVersion>3.45.0</InformationalVersion>', 1)
p.write_text(text, encoding='cp1252')
"
grep -E 'Version|FileVersion|InformationalVersion' src/Directory.Build.props
```

Expected: `<Version>3.45.0</Version>` + `<AssemblyVersion>3.45.0.0</AssemblyVersion>` + `<FileVersion>3.45.0.0</FileVersion>` + `<InformationalVersion>3.45.0</InformationalVersion>`.

- [ ] **Step 2: Write release notes**

Mirror W30 release-notes-v3.44.0.md format (~120 LoC).

- [ ] **Step 3: Build + full test suite to verify MINOR**

```bash
dotnet build PeakCan.Host.slnx -c Debug --no-restore 2>&1 | tail -5
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~ReplayService" --logger "console;verbosity=minimal" 2>&1 | tail -5
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~Replay" --logger "console;verbosity=minimal" 2>&1 | tail -5
```

Expected: 0 errors, all ReplayService + Replay tests pass.

- [ ] **Step 4: Commit T3**

```bash
git add src/Directory.Build.props docs/release-notes-v3.45.0.md
git commit -m "W31 T3: v3.44.5 -> v3.45.0 MINOR (ReplayService god-class refactor, 27th overall, 7th Core-layer, 1st new Core-layer since W25, -119 LoC -44.9% main)"
```

---

## Task T4: Tier-3 ship (PR + squash + tag + GH release)

- [ ] **Step 1: Push branch + create PR**

```bash
git push -u origin feature/w31-replay-service-god-class
gh pr create --base main --head feature/w31-replay-service-god-class --title "W31 MINOR: ReplayService god-class refactor (27th overall, 7th Core-layer, 2-partial design, -119 LoC -44.9% main)" --body "[full PR body per W30 PR #61 sister precedent]"
```

- [ ] **Step 2: Wait for CI + squash-merge + tag + GH release**

```bash
gh pr checks <PR-number> --watch  # wait for CI (5-attempt MAX per W23.5-W30 sister pattern)
gh pr merge <PR-number> --squash --delete-branch
git tag -a v3.45.0 -m "v3.45.0 MINOR: ReplayService god-class refactor (27th overall, 7th Core-layer, 1st new Core-layer since W25 ChannelRouter, -119 LoC -44.9% main, 2-partial design FileIoLifecycle+FrameEmission, multi-interface IReplayService+IDisposable, default D5 sister-principle since LARGEST method <60 LoC)" <squash-commit-sha>
git push origin v3.45.0
gh release create v3.45.0 --title "v3.45.0 — ReplayService god-class refactor" --notes "[release notes body]"
```

---

## Verification

- `dotnet build src/PeakCan.Host.Core/`: 0 errors, 0 warnings
- `dotnet test --filter "FullyQualifiedName~ReplayService"`: tests pass without modification
- `dotnet test` (full solution): 0 new fails
- `wc -l src/PeakCan.Host.Core/Replay/ReplayService.cs` ≤ 160 LoC (target ~146)
- 2 NEW partial files in `ReplayService/` directory
- 5 fields + 1 internal test property + 1 ctor + 8 delegating properties + 1 backing field + 3 events + `Dispose` + 6 delegating methods + 1 `[LoggerMessage]` partial remain in main
- Tag v3.45.0 + GH release published
- Branch deleted post-merge

## Out of scope (YAGNI)

- No public/internal API change.
- No test changes.
- No facade pattern (W3-W30 CONFIRMED direct partial-class visibility).
- No xmldoc-grep risk (verified — tests do not path-grep main file content).
- No W18 R1 mitigation needed (D4 keeps all 1 `[LoggerMessage]` partials on main partial declaration).
- No `ReplayTimeline.cs` partial changes (Core/Replay layer sister precedent; W15 ReplayTimeline already shipped as separate class).
- No `ReplayViewModel.cs` or `IReplayService.cs` interface changes.
