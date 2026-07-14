# W35 Implementation Plan — PeakCanChannel write-flow god-class refactor (31st overall, 3rd Infrastructure-layer)

**Goal**: Extract 2 NEW partial-class files (ConnectFlow + WriteFlow) from `src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs` 244 → ~128 LoC (-116 LoC, -47.5%). Public API + existing tests unchanged.

**Architecture**: Sister pattern of W18 PeakCanChannel initial (2 partials: NativeBindings + ReadLoopFlow) + W25 ChannelRouter (3 partials) + W34 2-partial dual-cluster (Lifecycle + TimerTick). 31st god-class refactor. **3rd Infrastructure-layer** (after W18 + W25) + **25th subdirectory-pattern deployment**. **2nd-cycle god-class refactor of PeakCanChannel** (W35 takes PeakCanChannel from 442 LoC total to ~370 LoC total — substantial further extraction).

**Tech Stack**: C# .NET 10 partial-class split pattern + W22+W23+W34 sister "orchestration-loop stays inline" pattern (NOT applied at W35 since LARGEST method 50 LoC < 60 LoC threshold) + W19 R1 first-correction ENHANCED (pre-flight prevention + post-failure recovery) + W20 verbatim re-extraction + W23 STRUCT-FABRACTION LESSON

**Spec**: [2026-07-14-peakcan-channel-write-flow-god-class-refactor.md](../specs/2026-07-14-peakcan-channel-write-flow-god-class-refactor.md)

---

## Global Constraints

- 1 partial class with 2 NEW partials in subdirectory `src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel/` (sister of W18's existing NativeBindings.cs + ReadLoopFlow.cs in same subdirectory)
- `public sealed partial class PeakCanChannel : ICanChannel` (already partial at L65; no D2 application needed per W18 + W26.5 + W30 + W31 + W32 + W33 + W34 sister precedent)
- All public API surface unchanged (DI registration + tests + `ICanChannel` interface contracts)
- LoC formula `main_after = main_before - delete_count` (W8.5 D7 32-locked, no +1 offset)
- W17 wc-l-splitlines CONFIRMED + cp1252 binary read+write pattern (since file is Windows-1252 encoded)
- W19 R1 first-correction ENHANCED: re-grep boundaries BEFORE each deletion script + post-failure recovery procedure (git checkout + re-grep + corrected offsets + re-run)
- W20 LESSON: verbatim re-extraction via `git show main:src/.../PeakCanChannel.cs | sed -n '<range>p'`
- W23 STRUCT-FABRACTION LESSON: verify `TPCANMsgFD` struct fields + `TPCANMsg` struct fields + `CanFrame.IsFd` property + `FrameFlags.BitRateSwitch` + `FrameFlags.ErrorStateIndicator` + `PeakCanFrameFormatter.ToFixedBytes64` + `ToFixedBytes8` static method signatures
- W25 D5 deviation NOT applied: `ConnectAsync` 50 LoC LARGEST method < 60 LoC threshold (orchestration-loop stay pattern requires ≥ 60 LoC threshold; W35 is BELOW threshold → ConnectAsync moves to ConnectFlow)
- W34 sister precedent: ORDER (ConnectFlow T1 → WriteFlow T2) per W34 D7 "largest-cluster-first" sister; ConnectFlow first (largest methods + lifecycle state-management); WriteFlow second (single pure-dispatch method)

---

## Task T0: Branch + SPEC + PLAN commits

**Files:**
- Create: `docs/superpowers/specs/2026-07-14-peakcan-channel-write-flow-god-class-refactor.md` (already created)
- Create: `docs/superpowers/plans/2026-07-14-peakcan-channel-write-flow-god-class-refactor.md` (this file)

**Interfaces:**
- Produces: SPEC + PLAN committed to feature branch

- [ ] **Step 1: Branch + verify already partial + W18 subdirectory state**

```bash
git checkout -b feature/w35-peakcan-channel-write-flow-god-class main
grep -n "public sealed" src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs | head -1
ls -la src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel/
```

Expected:
- `65:public sealed partial class PeakCanChannel : ICanChannel` (already partial per W18 + W26.5 + W30 + W31 + W32 + W33 + W34 sister precedent; no D2 application needed)
- `NativeBindings.cs` + `ReadLoopFlow.cs` exist from W18 (sister partials)

- [ ] **Step 2: Build + tests baseline (no source change yet)**

```bash
dotnet build src/PeakCan.Host.Infrastructure/PeakCan.Host.Infrastructure.csproj -c Debug --no-restore 2>&1 | tail -5
dotnet test tests/PeakCan.Host.Infrastructure.Tests/PeakCan.Host.Infrastructure.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~PeakCanChannel" --logger "console;verbosity=minimal" 2>&1 | tail -5
```

Expected: 0 errors, **7/7 PeakCanChannel tests pass** (baseline).

- [ ] **Step 3: Commit SPEC + PLAN**

```bash
git add docs/superpowers/specs/2026-07-14-peakcan-channel-write-flow-god-class-refactor.md
git commit -m "W35 spec: PeakCanChannel write-flow god-class refactor (2 partials + 5-task roll-out, 31st overall, 3rd Infrastructure-layer, 25th subdirectory-pattern deployment, 2nd-cycle god-class refactor of PeakCanChannel after W18)"
git add docs/superpowers/plans/2026-07-14-peakcan-channel-write-flow-god-class-refactor.md
git commit -m "W35 plan: PeakCanChannel write-flow god-class refactor (2 partials: ConnectFlow + WriteFlow)"
```

---

## Task T1: ConnectFlow partial extraction (~69 LoC)

**Files:**
- Create: `src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel/ConnectFlow.partial.cs` (~110 LoC)
- Modify: `src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs` (delete L109-L158 + L160-L172 + L222-L227 = 69 LoC, processed in REVERSE order)

**Interfaces:**
- Consumes: `PeakCanChannel` partial-class visibility (private fields `_handle` + `_gate` + `_logger` + `_reader` + `Id` property + `IsConnected` property + 4 LoggerMessage partials + 1 ctor)
- Calls cross-partial: `MakeError` (in `NativeBindings.cs` W18 partial) + `ResolveClassicCode` (in `NativeBindings.cs` W18 partial)
- Produces: 3 methods extracted to ConnectFlow.partial.cs (`ConnectAsync` 50 LoC + `DisconnectAsync` 13 LoC + `DisposeAsync` 6 LoC)

- [ ] **Step 1: Re-grep boundaries BEFORE running deletion script (W19 R1 first-correction ENHANCED pre-flight prevention)**

```bash
grep -n "public async Task<Result<Unit>> ConnectAsync\|public async Task DisconnectAsync\|public async ValueTask DisposeAsync" src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs
```

Expected:
```
109:    public async Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
160:    public async Task DisconnectAsync(CancellationToken ct = default)
222:    public async ValueTask DisposeAsync()
```

- [ ] **Step 2: Verbatim re-extract 3 methods from main HEAD (W20 LESSON 46th application)**

```bash
git show main:src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs | sed -n '109,158p'   # ConnectAsync body
git show main:src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs | sed -n '160,172p'   # DisconnectAsync body
git show main:src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs | sed -n '222,227p'   # DisposeAsync body
```

Expected: 50 + 13 + 6 = 69 LoC total.

- [ ] **Step 3: Write ConnectFlow.partial.cs (~110 LoC)**

```csharp
// PeakCanChannel/ConnectFlow.partial.cs — W35 T1 (Flow A, 69 LoC)
// Lifecycle methods: ConnectAsync (gate → classic/FD Initialize → start
// read loop) + DisconnectAsync (await read loop + Uninitialize) +
// DisposeAsync (calls DisconnectAsync). All 3 touch _handle + _gate state.
//
// W18 PeakCanChannel NativeBindings.cs has MakeError + ResolveClassicCode
// (also extracted in W18). W35 cross-partial caller: ConnectAsync calls
// MakeError + ResolveClassicCode from NativeBindings.cs partial (W18 sister).
//
// Cross-partial visibility:
//   - ConnectAsync (this partial) reads _handle + _gate + _reader + _logger + Id
//     (all in main partial); calls MakeError + ResolveClassicCode (NativeBindings
//     partial); calls Task.Run + ReadLoopAsync delegate (uses existing W18 read
//     loop by name).
//   - DisconnectAsync reads _handle + _gate (in main); calls await + PCANBasic.
//   - DisposeAsync calls DisconnectAsync (this partial).
//
// 4 [LoggerMessage] declarations (LogReadLoopException + LogReadLoopGivingUp +
// LogReadLoopSubscriberThrew + the 4th implicit static partial) STAY on main
// partial per W18+W22+W23+W25+W26+W27+W28+W29+W30+W31+W32+W33+W34+W35 sister
// precedent (CS8795 mitigation). ConnectFlow does NOT call any logger partial
// directly (no logger calls in ConnectAsync + DisconnectAsync + DisposeAsync
// per main HEAD L109-L227 verification).
//
// W23 STRUCT-FABRACTION LESSON: TPCANStatus enum + uint cast + Result<Unit>
// static factory + ChannelConnectGate.TryEnter + MakeError + ResolveClassicCode
// signatures verified.
//
// W35 T1 verbatim re-extracted via
//   `git show main:src/.../PeakCanChannel.cs | sed -n '109,158p;160,172p;222,227p'`
// per W20 T2 R1 fabrication LESSON (46th application).

using Microsoft.Extensions.Logging;
using Peak.Can.Basic.BackwardCompatibility;
using PeakCan.Host.Core;

namespace PeakCan.Host.Infrastructure.Peak;

public sealed partial class PeakCanChannel
{
    /// <summary>
    /// Initialize the underlying PCAN-Basic handle at <paramref name="baud"/>
    /// (or its descriptor for FD) and launch the read loop. If <paramref name="fd"/>
    /// is false, <see cref="ResolveClassicCode"/> maps the BaudRate name to the
    /// matching <c>PCAN_BAUD_*</c> preset. Returns <see cref="ErrorCode.HardwareParameter"/>
    /// when the baud rate has no classic CAN preset.
    /// </summary>
    public async Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
    {
        // ... [verbatim content from main HEAD L109-L158]
    }

    /// <summary>Stop the read loop and uninitialize the handle. Idempotent.</summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        // ... [verbatim content from main HEAD L160-L172]
    }

    /// <summary>DisposeAsync delegates to DisconnectAsync.</summary>
    public async ValueTask DisposeAsync()
    {
        // ... [verbatim content from main HEAD L222-L227]
    }
}
```

- [ ] **Step 4: Write deletion script + run + verify**

```python
# scripts/w35_task1_delete_connectflow.py — W19 R1 first-correction ENHANCED + W17 wc-l-splitlines CONFIRMED
"""W35 T1 deletion script — remove 3 methods from PeakCanChannel.cs (lifecycle cluster).

Per W19 R1 first-correction ENHANCED: re-grep boundaries BEFORE running this script
+ post-failure recovery procedure baked in (W31 T2 + W32 T2 + W33 T1+T2+T3 + W34 T1 lessons learned).
Per W20 T2 R1 fabrication LESSON: verbatim re-extract from main HEAD before deleting.
Per W17 wc-l-splitlines CONFIRMED: use binary read+write with cp1252.

W35 has 3 non-contiguous regions to delete (Connect lifecycle cluster):
  1. DisposeAsync: L222-L227 (6 LoC)
  2. DisconnectAsync: L160-L172 (13 LoC)
  3. ConnectAsync: L109-L158 (50 LoC)

Process in REVERSE ORDER (highest line first) to keep line numbers stable.

W35 T1 2/3 loose-assertion: predicted -69 LoC (3 methods: ConnectAsync 50 + DisconnectAsync 13 + DisposeAsync 6).
Within ±2 LoC tolerance.

**W19 R1 LESSON ENHANCED**: if delta is outside ±2 tolerance, recovery procedure is:
  1. git checkout src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs (restore from git)
  2. Re-grep post-T0 boundaries via grep -n
  3. Correct the offsets in the script
  4. Re-run the script
  5. Verify delta = expected
  6. Build + test
"""
from pathlib import Path

p = Path("src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

# Verify post-T0 boundaries first
print(f"Total lines: {len(lines)}")
if len(lines) > 108:
    print(f"L109 (ConnectAsync): {lines[108].strip()}")
if len(lines) > 159:
    print(f"L160 (DisconnectAsync): {lines[159].strip()}")
if len(lines) > 221:
    print(f"L222 (DisposeAsync): {lines[221].strip()}")
print()

# Process in REVERSE ORDER:
# First pass: delete DisposeAsync (L222-L227)
new_lines = lines[:221] + lines[227:]
# Second pass: delete DisconnectAsync (now at L160-L172 after first pass)
new_lines = new_lines[:159] + new_lines[172:]
# Third pass: delete ConnectAsync (now at L109-L158 after second pass)
new_lines = new_lines[:108] + new_lines[158:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

before = 244
after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"PeakCanChannel.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 69 (ConnectAsync 50 + DisconnectAsync 13 + DisposeAsync 6). Within ±2 LoC tolerance.")
assert 67 <= delta <= 71, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
```

- [ ] **Step 5: Build + tests to verify T1 extraction**

```bash
dotnet build src/PeakCan.Host.Infrastructure/PeakCan.Host.Infrastructure.csproj -c Debug --no-restore 2>&1 | tail -5
dotnet test tests/PeakCan.Host.Infrastructure.Tests/PeakCan.Host.Infrastructure.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~PeakCanChannel" --logger "console;verbosity=minimal" 2>&1 | tail -5
```

Expected: 0 errors, **7/7 PeakCanChannel tests pass without modification**.

- [ ] **Step 6: Commit T1**

```bash
git add src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel/ConnectFlow.partial.cs scripts/w35_task1_delete_connectflow.py
git commit -m "W35 T1: extract ConnectFlow partial (ConnectAsync 50 + DisconnectAsync 13 + DisposeAsync 6 LoC, -69 LoC main, 244 -> 175)"
```

---

## Task T2: WriteFlow partial extraction (~47 LoC)

**Files:**
- Create: `src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel/WriteFlow.partial.cs` (~70 LoC)
- Modify: `src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs` (delete post-T1 range for `WriteAsync` 47 LoC, 1 contiguous region)

**Interfaces:**
- Consumes: `PeakCanChannel` partial-class visibility (private fields `_handle` + `_logger` + `IsConnected` property)
- Calls cross-partial: `MakeError` (in `NativeBindings.cs` W18 partial, returns `Result<Unit>`)
- Produces: 1 public method `WriteAsync` extracted to WriteFlow.partial.cs

- [ ] **Step 1: Re-grep boundaries AFTER T1 deletion (W19 R1 first-correction ENHANCED pre-flight prevention)**

```bash
grep -n "public ValueTask<Result<Unit>> WriteAsync\|public sealed partial class\|^}" src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs
```

Expected: `WriteAsync` signature line + closing `}` of class. (Line numbers will be SHIFTED by -69 from main HEAD L174-L220.)

- [ ] **Step 2: Verbatim re-extract WriteAsync from main HEAD (W20 LESSON 47th application)**

```bash
git show main:src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs | sed -n '174,220p'
```

Expected: 47 LoC (classic-vs-FD dual-path dispatch body).

- [ ] **Step 3: Write WriteFlow.partial.cs (~70 LoC)**

```csharp
// PeakCanChannel/WriteFlow.partial.cs — W35 T2 (Flow B, 47 LoC)
// Public async method: WriteAsync dispatches the CanFrame write to
// PCAN-Basic via classic (TPCANMsg + Write) or FD (TPCANMsgFD + WriteFD)
// based on frame.IsFd. Returns Result<Unit> from the W18 NativeBindings
// partial's MakeError helper.
//
// W18 PeakCanChannel NativeBindings.cs has MakeError (also extracted in W18).
// W35 cross-partial caller: WriteAsync calls MakeError from NativeBindings.cs
// partial (W18 sister).
//
// Cross-partial visibility:
//   - WriteAsync (this partial) reads _handle + IsConnected (in main partial);
//     uses PeakCanFrameFormatter.ToFixedBytes64 + ToFixedBytes8 (static methods
//     in PeakCan.Host.Core namespace, NOT partial); calls MakeError (NativeBindings
//     partial).
//
// W23 STRUCT-FABRACTION LESSON: TPCANStatus enum + TPCANMessageType flag-bit
// pattern + TPCANMsg struct (ID + MSGTYPE + LEN + DATA fields) + TPCANMsgFD
// struct (ID + MSGTYPE + DLC + DATA fields) + PeakCanFrameFormatter.ToFixedBytes8
// (returns byte[8]) + ToFixedBytes64 (returns byte[64]) + FrameFlags.BitRateSwitch
// + FrameFlags.ErrorStateIndicator bitflag enums + CanFrame.IsFd property +
// CanFrame.Id.IsExtended property verified during verbatim re-extraction.
//
// W35 T2 verbatim re-extracted via
//   `git show main:src/.../PeakCanChannel.cs | sed -n '174,220p'`
// per W20 T2 R1 fabrication LESSON (47th application).

using Peak.Can.Basic.BackwardCompatibility;
using PeakCan.Host.Core;

namespace PeakCan.Host.Infrastructure.Peak;

public sealed partial class PeakCanChannel
{
    /// <summary>
    /// Write <paramref name="frame"/> to the underlying PCAN-Basic handle.
    /// Calls <c>PCANBasic.Write</c> for classic frames or <c>PCANBasic.WriteFD</c>
    /// for FD frames, translating <paramref name="frame"/>'s
    /// <see cref="CanFrame.Id"/> + <see cref="CanFrame.Flags"/> + <see cref="CanFrame.Data"/>
    /// into the SDK's <c>TPCANMsg</c> / <c>TPCANMsgFD</c> representation.
    /// </summary>
    public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
    {
        // ... [verbatim content from main HEAD L174-L220]
    }
}
```

- [ ] **Step 4: Write deletion script + run + verify**

```python
# scripts/w35_task2_delete_writeflow.py — W19 R1 first-correction ENHANCED (boundary verification + recovery procedure documented)
"""W35 T2 deletion script — remove WriteAsync from PeakCanChannel.cs (write-flow cluster).

Per W19 R1 first-correction ENHANCED: re-grep boundaries BEFORE running this script
+ post-failure recovery procedure baked in (W31 T2 + W32 T2 + W33 T1+T2+T3 + W34 T1 + W35 T1 lessons learned).
Per W20 T2 R1 fabrication LESSON: verbatim re-extract from main HEAD before deleting.
Per W17 wc-l-splitlines CONFIRMED: use binary read+write with cp1252.

POST-T1 boundaries (after W35 T1 deleted 3 connect lifecycle methods:
ConnectAsync 50 + DisconnectAsync 13 + DisposeAsync 6 = 69 LoC):
  - WriteAsync: SHIFTED by -69 from main HEAD L174-L220 to post-T1 L105-L151

W35 T2 2/3 loose-assertion: predicted -47 LoC.
Within ±2 LoC tolerance.

**W19 R1 LESSON ENHANCED**: if delta is outside ±2 tolerance, recovery procedure is:
  1. git checkout src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs (restore from git)
  2. Re-grep post-T1 boundaries via grep -n
  3. Correct the offsets in the script
  4. Re-run the script
  5. Verify delta = expected
  6. Build + test
"""
from pathlib import Path

p = Path("src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs")
raw = p.read_bytes()
text = raw.decode("cp1252")
lines = text.splitlines(keepends=True)

# Verify post-T1 boundaries first
print(f"Total lines: {len(lines)}")

START, END = 105, 151  # UPDATE per Step 1 grep result (WriteAsync post-T1)
before = len(lines)
new_lines = lines[:START - 1] + lines[END:]
new_text = "".join(new_lines)
p.write_bytes(new_text.encode("cp1252"))

after = sum(1 for _ in Path(p).read_bytes().decode("cp1252").splitlines())
delta = before - after
print(f"PeakCanChannel.cs: {before} -> {after} (delta = {delta})")
print(f"Expected delta: 47 (WriteAsync body). Within ±2 LoC tolerance.")
assert 45 <= delta <= 49, f"FAIL: delta {delta} outside ±2 tolerance"
print("PASS.")
```

- [ ] **Step 5: Build + tests to verify T2 extraction**

```bash
dotnet build src/PeakCan.Host.Infrastructure/PeakCan.Host.Infrastructure.csproj -c Debug --no-restore 2>&1 | tail -5
dotnet test tests/PeakCan.Host.Infrastructure.Tests/PeakCan.Host.Infrastructure.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~PeakCanChannel" --logger "console;verbosity=minimal" 2>&1 | tail -5
```

Expected: 0 errors, **7/7 PeakCanChannel tests pass without modification**.

- [ ] **Step 6: Commit T2**

```bash
git add src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel/WriteFlow.partial.cs scripts/w35_task2_delete_writeflow.py
git commit -m "W35 T2: extract WriteFlow partial (WriteAsync 47 LoC classic-vs-FD dual-path dispatch, -47 LoC main, 175 -> 128)"
```

---

## Task T3: v3.48.0 → v3.48.1 PATCH + release notes

**Files:**
- Modify: `src/Directory.Build.props` (4-field version bump)
- Create: `docs/release-notes-v3.48.1.md` (~120 LoC)

- [ ] **Step 1: Bump Directory.Build.props**

```bash
python -c "
from pathlib import Path
p = Path('src/Directory.Build.props')
text = p.read_text(encoding='cp1252')
text = text.replace('<Version>3.48.0</Version>', '<Version>3.48.1</Version>', 1)
text = text.replace('<AssemblyVersion>3.48.0.0</AssemblyVersion>', '<AssemblyVersion>3.48.1.0</AssemblyVersion>', 1)
text = text.replace('<FileVersion>3.48.0.0</FileVersion>', '<FileVersion>3.48.1.0</FileVersion>', 1)
text = text.replace('<InformationalVersion>3.48.0</InformationalVersion>', '<InformationalVersion>3.48.1</InformationalVersion>', 1)
p.write_text(text, encoding='cp1252')
"
grep -E 'Version|FileVersion|InformationalVersion' src/Directory.Build.props
```

Expected: `<Version>3.48.1</Version>` + `<AssemblyVersion>3.48.1.0</AssemblyVersion>` + `<FileVersion>3.48.1.0</FileVersion>` + `<InformationalVersion>3.48.1</InformationalVersion>`.

- [ ] **Step 2: Write release notes**

Mirror W34 release-notes-v3.48.0.md format (~120 LoC). Title: "v3.48.1 PATCH — PeakCanChannel write-flow god-class refactor (31st overall)".

Sections:
- Header: target class, target version, sister pattern
- D1-D7 + sister-lesson candidates table
- Architecture + flow boundaries
- LoC trajectory table
- W20 + W23 LESSON APPLIED
- Verification
- Out of scope (YAGNI)
- Next (W35.5 vault-only PATCH candidate + W36 sister candidates)

- [ ] **Step 3: Build + full test suite to verify PATCH**

```bash
dotnet build src/PeakCan.Host.Infrastructure/PeakCan.Host.Infrastructure.csproj -c Debug --no-restore 2>&1 | tail -5
dotnet test tests/PeakCan.Host.Infrastructure.Tests/PeakCan.Host.Infrastructure.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~PeakCanChannel" --logger "console;verbosity=minimal" 2>&1 | tail -5
```

Expected: 0 errors, **7/7 PeakCanChannel tests pass**.

- [ ] **Step 4: Commit T3**

```bash
git add src/Directory.Build.props docs/release-notes-v3.48.1.md
git commit -m "W35 T3: v3.48.0 -> v3.48.1 PATCH (PeakCanChannel write-flow god-class refactor, 31st overall, 3rd Infrastructure-layer, 25th subdirectory-pattern deployment, 2nd-cycle god-class refactor after W18, -116 LoC -47.5% main, 4-partial total NativeBindings+ReadLoopFlow+ConnectFlow+WriteFlow)"
```

---

## Task T4: Tier-3 ship (PR + squash + tag + GH release)

- [ ] **Step 1: Push branch + create PR**

```bash
git push -u origin feature/w35-peakcan-channel-write-flow-god-class
gh pr create --base main --head feature/w35-peakcan-channel-write-flow-god-class --title "W35 PATCH: PeakCanChannel write-flow god-class refactor (31st overall, 3rd Infrastructure-layer, 2nd-cycle after W18, -116 LoC -47.5% main, 4-partial total)" --body "[full PR body per W34 PR #68 sister precedent]"
```

- [ ] **Step 2: Wait for CI + squash-merge + tag + GH release**

```bash
gh pr checks <PR-number> --watch  # wait for CI (5-attempt MAX per W23.5-W34 sister pattern)
gh pr merge <PR-number> --squash --delete-branch
git tag -a v3.48.1 -m "v3.48.1 PATCH: PeakCanChannel write-flow god-class refactor (31st overall, 3rd Infrastructure-layer, 25th subdirectory-pattern deployment, 2nd-cycle god-class refactor of PeakCanChannel after W18, -116 LoC -47.5% main, 4-partial total NativeBindings+ReadLoopFlow+ConnectFlow+WriteFlow, ConnectAsync 50 LoC moves since below 60 LoC threshold, WriteAsync 47 LoC moves)" <squash-commit-sha>
git push origin v3.48.1
gh release create v3.48.1 --title "v3.48.1 — PeakCanChannel write-flow god-class refactor" --notes "[release notes body]"
```

---

## Verification

- `dotnet build src/PeakCan.Host.Infrastructure/`: 0 errors, 0 warnings
- `dotnet test --filter "FullyQualifiedName~PeakCanChannel"`: 7/7 tests pass without modification
- `dotnet test` (full solution): 0 new fails
- `wc -l src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs` ≤ 130 LoC (target ~128)
- 2 NEW partial files in `PeakCanChannel/` directory (ConnectFlow + WriteFlow)
- 4 existing W18 partials unchanged (NativeBindings.cs + ReadLoopFlow.cs)
- 4 fields + 4 events/properties + 1 ctor + 4 `[LoggerMessage]` partials + class xmldoc remain in main
- Cross-partial visibility works for `ConnectAsync → MakeError` + `ConnectAsync → ResolveClassicCode` (both in NativeBindings partial) + `ReadLoopAsync → Log*` (in main partial)
- DI registration unchanged (`AddSingleton<ICanChannel, PeakCanChannel>(...)` factory)
- Public API unchanged (`ICanChannel` interface)
- Tag v3.48.1 + GH release published
- Branch deleted post-merge

## Out of scope (YAGNI)

- No public/internal API change.
- No test changes (7 PeakCanChannelTests pass without modification).
- No facade pattern (W3-W34 CONFIRMED direct partial-class visibility).
- No xmldoc-grep risk (verified — tests do not path-grep main file content).
- No W18 R1 mitigation needed (D4 keeps all 4 `[LoggerMessage]` partials on main partial declaration).
- No `ICanChannel.cs` interface partial changes (stays in Infrastructure/Channel layer).
- No `PeakErrorMapper.cs` + `PeakCanFrameFormatter.cs` partial changes (consumed by PeakCanChannel but not modified).
- No `ChannelConnectGate.cs` partial changes (consumed by PeakCanChannel but not modified).
- No `IPcanReader.cs` + `PcanReader.cs` partial changes.
- No `ChannelRouter.cs` (W25-extracted) partial changes.
- W25 partials (ChannelRouter/Channels.partial.cs + FrameRouting.partial.cs + Sinks.partial.cs) unchanged.
- W18 partials (PeakCanChannel/NativeBindings.cs + ReadLoopFlow.cs) unchanged.

## Sister-lesson candidates

| Lesson | Status | What W35 might observe |
|---|---|---|
| `partial-extraction-must-use-original-code-from-head-not-fabricated-api` | 3/3 CONFIRMED (W21) | W35 11th god-class application (T1+T2) — 47th application total |
| `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` | 3/3 CONFIRMED (W23+W24+W25+W26+W27+W28+W29+W30+W31+W32+W33+W34 17-of-1) | W35 18th observation (`TPCANMsg` struct + `TPCANMsgFD` struct + `PeakCanFrameFormatter.ToFixedBytes64` + `ToFixedBytes8` static method + `FrameFlags.BitRateSwitch` + `FrameFlags.ErrorStateIndicator` bitflags + `CanFrame.IsFd` + `Id.IsExtended` property signatures verified) |
| `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` | 3/3 CONFIRMED (W23+W24+W25+W26+W27+W28+W29+W30+W31+W32+W33+W34 13-of-1) | W35 14th confirmation (ConnectFlow partial does NOT touch logger partials; WriteFlow partial does NOT touch logger partials; positive confirmation by absence — existing W18 logger partials from main partial remain callable from W18 ReadLoopFlow partial) |
| `add-partial-keyword-to-monolithic-class-before-extraction` | 3/3 CONFIRMED (W26.5) | W35 already partial (33rd cumulative confirmation) |
| `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` | 11/3 since 3/3 LOCKED (W34) | N/A — W35 LARGEST method 50 LoC < 60 LoC threshold (NOT a stay/observation candidate) |
| `subdirectory-partials-pattern-empirical-26-precedents` | 3/3 CONFIRMED (W20) | W35 25th deployment, sister-of-W34 |
| `infrastructure-channel-layer-sister-pattern-empirical-w18-w25` | 2/3 (W25) | W35 3rd observation candidate: 2nd-cycle god-class refactor of PeakCanChannel + sister of W18 initial + sister of W25 ChannelRouter. **Promotes to 3/3 CONFIRMED LOCKED if W35 ships cleanly.** |
| `second-cycle-god-class-refactor-empirical-w28-w29-w35` | NEW 1/3 (W35) | W35 1st observation: 2nd-cycle god-class refactor pattern (PeakCanChannel W18 → W35; SendFrameLibrary W22 → W29 2nd cycle; DbcService W19 → W28 2nd cycle). Sister-2nd-cycle pattern across 3 layers. |
| `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED` | 3/3 CONFIRMED LOCKED (W29.5) | N/A — W35 has no JSON-persistence |
| `app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28-w31-LOCKED` | 3/3 CONFIRMED LOCKED (W31.5) | N/A — W35 has no async file-load lifecycle (synchronous Connect + Disconnect only) |
| `small-god-class-no-largest-method-keeps-all-inline-default-pattern` | 3/3 CONFIRMED LOCKED (W33) | N/A — W35 LARGEST method 50 LoC = 50 LoC threshold (NOT < 50 LoC). Borderline; W35 strictly does NOT trigger. Could be re-evaluated at W35.5 if user has data points. |
| `multi-interface-partial-class-empirical-w26-w31-LOCKED` | 3/3 CONFIRMED LOCKED (W31.5) | N/A — W35 PeakCanChannel has 1 interface `ICanChannel` (no new multi-interface observation) |
| `app-services-cyclic-subsystem-sister-pattern-empirical-w23-w31-w34` | NEW 1/3 (W34) | N/A — W35 is Infrastructure/Peak, NOT App/Services/Cyclic |
| `app-services-scripting-sister-pattern-empirical-w14-w26-w32-LOCKED` | 3/3 PROMOTED (W32.5) → potential LOCK | N/A — W35 is Infrastructure/Peak, NOT App/Services/Scripting |
