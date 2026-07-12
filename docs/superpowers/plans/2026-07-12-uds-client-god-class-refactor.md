# W12 Plan — UdsClient god-class refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mechanically split `src/PeakCan.Host.Core/Uds/UdsClient.cs` (704 LoC) into 5 partial-class files + 145 LoC main file. Zero behavioral change.

**Architecture:** Same W3-W11 partial-class split pattern. Order: A → B → C → D → E (Transport first to validate IDisposable+virtual+instance-class pattern; B + C small/medium; D + E large).

**Tech Stack:** C# .NET 10 (implicit global usings enabled), Core layer. Git + LF + `dotnet build` + `dotnet test`.

**Spec:** [`../specs/2026-07-12-uds-client-god-class-refactor.md`](../specs/2026-07-12-uds-client-god-class-refactor.md)
**Branch:** `feature/w12-uds-client-god-class` (created from `main` @ `411a92c`)
**Parent commit:** `411a92c` (this is the spec commit; the actual W12 SHIP will land Tasks T1-T7 across multiple commits)

## Global Constraints (carried verbatim from spec)

- Public API unchanged (no method signatures, properties, exceptions, or nested types move).
- partial-class visibility on private fields + private methods.
- Test coverage unchanged (no tests added, removed, modified). Test seams (`OnP2TimeoutFiredForTesting` + `PublicOnMessageReceivedForTesting`) move with their consumer to TransportFlow.
- LF line endings per v3.18.0 `.gitattributes`.
- No behavioral change (every method body, xmldoc, comment, whitespace moves verbatim).
- No version bump until Task 6. Tasks 1-5 keep `Directory.Build.props` at v3.26.0.
- No edits to other files unless explicitly required by a task's verify step.

## LoC trajectory table (per W8.5 PATCH D7 CONFIRMED)

Formula: **`LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`** per flow moved.

| Task | Flow | Range (1-indexed inclusive) | LoC deleted | Markers added | LoC main after |
|---|---|---|---|---|---|
| T1 | A — Transport | 152-177, 569-695 | 153 | 1 | 552 (704-153+1) |
| T2 | B — Session | 179-236, 558-576 | 76 | 1 | 477 (552-76+1) |
| T3 | C — DataIO | 238-270, 529-555 | 59 | 1 | 419 (477-59+1) |
| T4 | D — Security | 272-408 | 137 | 1 | 283 (419-137+1) |
| T5 | E — Transfer | 410-527 | 118 | 1 | 166 (283-118+1) |
| T6 | version bump + release notes | (no source LoC changes) | 0 | 0 | 166 |
| **T7** | ship | -- | -- | -- | **166** |

Cumulative checkpoint: 166 LoC in main file ≈ 5 partial files ≈ W10 DbcParser main ~150 LoC + 5 partials ~600 LoC sister. Slightly larger main (166 vs 150) is acceptable because UdsClient has 3 ctors that all stay in main.

---

## Task 0: Branch + plan commit

**Files:**
- Modify: (none, plan written in spec phase)
- Create: `docs/superpowers/plans/2026-07-12-uds-client-god-class-refactor.md` (this file)

**Step 1**: Verify branch is created (run after this plan commit):

```bash
git checkout -b feature/w12-uds-client-god-class main
```

**Step 2**: Verify `dotnet build` baseline at parent commit:

```bash
dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug --no-restore
```

Expected: 0 errors, baseline GREEN.

**Step 3**: Verify baseline test count for the UDS namespace (for closure comparison at end):

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~Uds" --logger "console;verbosity=minimal"
```

Expected: full UDS test suite passes, capture pass count for after-comparison.

**Step 4**: Commit plan (after Write tool save):

```bash
git add docs/superpowers/plans/2026-07-12-uds-client-god-class-refactor.md
git commit -m "W12 plan: UdsClient god-class refactor (5 partials + 7-task roll-out)"
```

---

## Task 1: Extract Flow A — Transport (wire + Rx + Dispose + test seam)

**Files:**
- Modify: `src/PeakCan.Host.Core/Uds/UdsClient.cs:152-177` (delete `SendRequestAsync` body)
- Modify: `src/PeakCan.Host.Core/Uds/UdsClient.cs:569-695` (delete `Dispose` + transport internals + `PublicOnMessageReceivedForTesting`)
- Modify: `src/PeakCan.Host.Core/Uds/UdsClient.cs:15` (change `public class UdsClient : IDisposable` → `public partial class UdsClient : IDisposable`)
- Create: `src/PeakCan.Host.Core/Uds/UdsClient/TransportFlow.cs` (NEW directory + NEW file)

**Range plan** (1-indexed, inclusive — for deletion script):
- Range 1: lines 152-177 (`SendRequestAsync`)
- Range 2: lines 569-695 (`Dispose` + `SendRequestInternalAsync` + `OnP2TimeoutFired` + `OnMessageReceived` + `PublicOnMessageReceivedForTesting`)
- The blank lines around the deleted ranges follow the original file's whitespace naturally.

**Cross-flow references (partial-class visible from TransportFlow)**:
- 13 UDS service methods (Flow B-G) call `SendRequestAsync` — no caller changes
- `Dispose` → `_isoTp.MessageReceived -= OnMessageReceived` — `OnMessageReceived` is in same partial

**Step 1**: Write the deletion script `scripts/w12_task1_delete_transportflow.py`:

```python
"""Delete Flow A (Transport wire + Rx + Dispose + test seam) from UdsClient.cs (Task 1)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.Core/Uds/UdsClient.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# 2 non-contiguous ranges in 704-LoC file:
# (1) SendRequestAsync (lines 152-177) — wire entry point
# (2) Dispose + SendRequestInternalAsync + OnP2TimeoutFired + OnMessageReceived + PublicOnMessageReceivedForTesting (lines 569-695)
DELETIONS = [
    (152, 177, "SendRequestAsync wire entry point"),
    (569, 695, "Dispose + SendRequestInternalAsync + OnP2TimeoutFired + OnMessageReceived + PublicOnMessageReceivedForTesting"),
]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 704, f"Expected 704 LoC at Task 1 start, got {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")
print(f"Per W8.5 D7 formula: expected main = 704 - 153 + 1 = 552")
assert len(lines) == 552, f"Expected 552 LoC after Task 1, got {len(lines)}"

text = "".join(lines)

# Critical invariants — public API + state preserved:
assert "namespace PeakCan.Host.Core.Uds;" in text
assert "public partial class UdsClient : IDisposable" in text, "Outer class must be partial"
# 3 ctors preserved in main
assert text.count("public UdsClient(") == 3, "All 3 ctors must remain in main"
# Public properties preserved
assert "public UdsSession Session" in text
assert "public UdsSecurity Security" in text
# Private fields preserved (transport reads them via partial-class visibility)
assert "_isoTp" in text
assert "_responseTcs" in text
assert "_responseCts" in text
assert "_requestLock" in text
assert "_pendingRequestSid" in text
assert "OnP2TimeoutFiredForTesting" in text
# Tests seam-equivalent preserved
assert "OnP2TimeoutFiredForTesting" in text  # the delegate field stays (test-only)
# All UDS service methods preserved in main (Flow B-G haven't moved yet)
assert "DiagnosticSessionControlAsync" in text  # Flow B
assert "SecurityAccessAsync" in text  # Flow D
assert "TesterPresentAsync" in text  # Flow E

# Transport methods GONE from main:
assert "private async Task<byte[]> SendRequestInternalAsync" not in text
assert "private void OnP2TimeoutFired()" not in text
assert "private void OnMessageReceived(byte[] data)" not in text
assert "internal void PublicOnMessageReceivedForTesting" not in text

# Marker — insert before closing brace of class
marker = "    // === Flow A methods moved to UdsClient/TransportFlow.cs (W12 Task 1) ===\n"
for i in range(len(lines) - 1, -1, -1):
    if lines[i].strip() == "}":
        lines.insert(i, marker)
        print(f"Flow A marker inserted before line {i + 1} (class closing brace)")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")
```

**Step 2**: Run the deletion script:

```bash
cd D:/claude_proj2/peakcan-host
python scripts/w12_task1_delete_transportflow.py
```

Expected output: `Original line count: 704` → `New line count: 552 (removed 152 lines)` → marker inserted → `Wrote ... bytes`.

**Step 3**: Modify the outer class declaration in main file (CS0260 mitigation):

```bash
# Edit 1: change "public class UdsClient : IDisposable" to "public partial class UdsClient : IDisposable"
# Use Edit tool, old_string: "public class UdsClient : IDisposable"
#                   new_string: "public partial class UdsClient : IDisposable"
```

**Step 4**: Create `src/PeakCan.Host.Core/Uds/UdsClient/TransportFlow.cs` with verbatim extracted code:

```csharp
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core.Uds.IsoTp;

namespace PeakCan.Host.Core.Uds;

public partial class UdsClient
{
    // Flow A: Transport wire layer + Rx + Dispose + test seam.
    // SendRequestAsync → SendRequestInternalAsync → OnMessageReceived + OnP2TimeoutFired.
    // Extracted from UdsClient.cs verbatim per W12 D5 (test-seam + Dispose grouped
    // with the OnMessageReceived subscription handler they pair with).
    //
    // Cross-flow callers (partial-class visible):
    //   - SendRequestAsync ← all 13 UDS service methods (Flow B/C/D/E)

    /// <summary>
    /// Send a UDS service request and wait for response.
    /// </summary>
    /// <param name="serviceId">Service ID (SID).</param>
    /// <param name="data">Service data (excluding SID).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Response bytes (excluding SID + 0x40).</returns>
    /// <remarks>
    /// v1.2.14 PATCH Item 4: marked <c>virtual</c> so test doubles can
    /// intercept wire-level frame emit without subclassing the entire
    /// <see cref="UdsClient"/>. Visibility stays <c>public</c> for
    /// backwards compatibility with existing direct callers
    /// (e.g. <c>UdsClientTests</c>).
    /// </remarks>
    public virtual async Task<byte[]> SendRequestAsync(byte serviceId, byte[]? data = null, CancellationToken ct = default)
    {
        // Build request: SID + data
        byte[] request;
        if (data is null)
        {
            request = [serviceId];
        }
        else
        {
            request = new byte[1 + data.Length];
            request[0] = serviceId;
            Array.Copy(data, 0, request, 1, data.Length);
        }

        // Serialize requests to prevent overlapping
        await _requestLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await SendRequestInternalAsync(request, ct).ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public void Dispose()
    {
        _isoTp.MessageReceived -= OnMessageReceived;
        _requestLock.Dispose();
        Volatile.Read(ref _responseCts)?.Dispose();
        Session.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<byte[]> SendRequestInternalAsync(byte[] request, CancellationToken ct)
    {
        _pendingRequestSid = request[0];
        Volatile.Write(ref _responseTcs, new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously));
        Volatile.Write(ref _responseCts, CancellationTokenSource.CreateLinkedTokenSource(ct));

        // v1.2.13 PATCH Item 4: register a callback so P2 timeout unblocks
        // await _responseTcs.Task. Without this registration the linked CTS
        // cancel only fires OperationCanceledException for whoever awaits the
        // token directly — and nothing does. P2 timeout would silently hang
        // the caller. The callback TrySetCancels the TCS so the await resumes
        // with TaskCanceledException → caught below → rethrown as UdsException.
        var registration = _responseCts.Token.Register(
            static state => ((UdsClient)state!).OnP2TimeoutFired(), this);

        // Register timeout
        _responseCts.CancelAfter(_timer.P2Timeout);

        try
        {
            // Send via ISO-TP
            await _isoTp.SendMessageAsync(request, ct).ConfigureAwait(false);

            // Wait for response
            var response = await _responseTcs.Task.ConfigureAwait(false);
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UdsException("UDS response timeout");
        }
        finally
        {
            // v1.2.13 PATCH Item 4 (Phase 2.5 new finding): strict ordering
            // matters here. Disposal sequence:
            //   1. registration.Dispose()  — unhook the Token.Register callback
            //                                so it cannot fire during/after Dispose
            //   2. Volatile.Write(_responseTcs, null) — OnMessageReceived sees no TCS
            //   3. Volatile.Write(_responseCts, null) — OnMessageReceived sees no CTS
            //   4. cts?.Dispose()           — last; no in-flight reference remains
            // Without this ordering OnMessageReceived may cts?.CancelAfter on a
            // disposed CTS (ObjectDisposedException propagates onto the SDK
            // read thread — process crash on graceful shutdown).
            registration.Dispose();
            var cts = Volatile.Read(ref _responseCts);
            Volatile.Write(ref _responseTcs, null);
            Volatile.Write(ref _responseCts, null);
            cts?.Dispose();
        }
    }

    private void OnP2TimeoutFired()
    {
        OnP2TimeoutFiredForTesting?.Invoke();
        Volatile.Read(ref _responseTcs)?.TrySetCanceled();
    }

    private void OnMessageReceived(byte[] data)
    {
        if (data.Length < 1)
            return;

        byte sid = data[0];

        // Item 14: acquire-load the pending response handles. Without
        // Volatile.Read the JIT may have cached or hoisted the read.
        var tcs = Volatile.Read(ref _responseTcs);
        var cts = Volatile.Read(ref _responseCts);

        // Check for negative response (0x7F)
        if (sid == 0x7F && data.Length >= 3)
        {
            byte requestedSid = data[1];
            byte nrc = data[2];

            // Handle NRC 0x78 (requestCorrectlyReceivedResponsePending)
            if (nrc == 0x78)
            {
                // v1.2.13 PATCH Item 4 (Phase 2.5): guard against disposed
                // CTS. After SendRequestInternalAsync's finally has nulled
                // the fields and disposed cts, a late-arriving response
                // (already in flight on the SDK read thread) would crash
                // here. The IsCancellationRequested check is the cheap
                // fast-path; the try/catch handles the disposed-after-read
                // race window.
                if (cts is not null && !cts.IsCancellationRequested)
                {
                    try { cts.CancelAfter(_timer.P2StarTimeout); }
                    catch (ObjectDisposedException) { /* shutdown race */ }
                }
                return;
            }

            // Other NRCs: complete with error
            tcs?.TrySetException(new UdsNegativeResponseException(requestedSid, (UdsNegativeResponseCode)nrc));
            return;
        }

        // Positive response: SID + 0x40
        if (data.Length >= 2)
        {
            // C-8 fix: validate the SID echoes our in-flight request's SID + 0x40.
            // A misaligned SID means the frame is stale or from a different
            // request; dropping it lets the P2 timer expire (semantically correct).
            byte expectedPositiveSid = (byte)(_pendingRequestSid + 0x40);
            if (sid != expectedPositiveSid)
                return;

            tcs?.TrySetResult(data[1..]);
        }
    }

    /// <summary>
    /// v1.2.13 PATCH Item 4: test-only public surface for OnMessageReceived.
    /// Allows tests to drive late-arriving-response paths without standing
    /// up an IsoTpLayer + ICanChannel.
    /// </summary>
    internal void PublicOnMessageReceivedForTesting(byte[] data) => OnMessageReceived(data);
}
```

**Step 5**: Run `dotnet build`:

```bash
cd D:/claude_proj2/peakcan-host
dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug --no-restore
```

Expected: 0 errors. CS0260 partial-requirement fixed by Step 3. `using System.Collections.Concurrent;` already present in main file (line 1) — covers `SemaphoreSlim`. `Array.Copy` is in `System` (implicit). No new usings expected. If CS0246 surfaces, add `using` to the partial file's top.

**Step 6**: Run UDS tests (mirror all 6 partial-class refactors' verification):

```bash
cd D:/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~Uds" --logger "console;verbosity=minimal"
```

Expected: pass count matches baseline (Task 0 Step 3). 0 fail, 0 skip new.

**Step 7**: Commit Task 1:

```bash
cd D:/claude_proj2/peakcan-host
git add src/PeakCan.Host.Core/Uds/UdsClient.cs src/PeakCan.Host.Core/Uds/UdsClient/TransportFlow.cs scripts/w12_task1_delete_transportflow.py
git commit -m "W12 Task 1: extract Flow A (Transport wire + Rx + Dispose + test seam) to partial"
```

Expected: 1 commit ahead of parent. Main file 552 LoC; TransportFlow.cs ~135 LoC.

---

## Task 2: Extract Flow B — SessionFlow (0x10 + 0x11 + S3 keepalive façades)

**Files:**
- Modify: `src/PeakCan.Host.Core/Uds/UdsClient.cs:179-236` (delete `DiagnosticSessionControlAsync` + `EcuResetAsync` byte overload + `EcuResetAsync` enum overload)
- Modify: `src/PeakCan.Host.Core/Uds/UdsClient.cs:558-576` (delete `StartTesterPresent` + `StopTesterPresent`)
- Create: `src/PeakCan.Host.Core/Uds/UdsClient/SessionFlow.cs`

**Range plan** (1-indexed inclusive — note line numbers shift after Task 1):
After T1 deletion (153 LoC removed + 1 marker), original lines 179-236 become 26-83 in the new file (offset = -152 + 1 = -152). Original lines 558-576 become 405-423 (offset = -152). For Task 2 deletion script, work from the **post-T1** line counts:

Actually — the task script reads file post-T1 commit. We need to re-count lines. Per W8.5 D7 formula:
- Before T2: 552 LoC (post-T1)
- T2 deletes 76 LoC (179-236 = 58 LoC, 558-576 = 19 LoC — but with separator blank lines, it's 76 LoC after T1-state)
- After T2: 552 - 76 + 1 = 477 LoC

The script needs to compute these ranges against the **post-T1** line numbers, which means we need to confirm by re-reading the post-T1 file. Easiest: re-`wc -l` after T1 commit and use those new offsets.

**Step 1**: Re-count post-T1 to get post-T1 line numbers:

```bash
wc -l src/PeakCan.Host.Core/Uds/UdsClient.cs
```

Expected: 552 lines. Compute Task 2 ranges: the 58 LoC 0x10/0x11 block (now likely at lines 178-235) + the 19 LoC Start/Stop TesterPresent façades (now likely at lines 405-423).

Use Grep to find them post-T1:

```bash
grep -n "public virtual async Task<DiagnosticSessionResponse> DiagnosticSessionControlAsync\|public virtual async Task<byte> EcuResetAsync(byte\|public Task<byte> EcuResetAsync(UdsResetType\|public void StartTesterPresent\|public void StopTesterPresent\|^/// <summary>Start automatic TesterPresent\|public void Dispose" src/PeakCan.Host.Core/Uds/UdsClient.cs
```

This will output line numbers post-T1. Construct the deletions list from those.

**Step 2**: Write `scripts/w12_task2_delete_sessionflow.py` with confirmed ranges + assertions:

```python
"""Delete Flow B (SessionFlow: 0x10 + 0x11 + S3 keepalive façades) from UdsClient.cs (Task 2)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.Core/Uds/UdsClient.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 552, f"Expected 552 LoC at Task 2 start (post-T1), got {original_count}"

# RECONFIRMED FROM POST-T1 GREP — exact ranges:
DELETIONS = [
    (<diag_start>, <diag_end>, "DiagnosticSessionControlAsync + EcuResetAsync 2 overloads"),
    (<keepalive_start>, <keepalive_end>, "StartTesterPresent + StopTesterPresent"),
]

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

# Per W8.5 D7 formula: 552 - 76 + 1 = 477
expected_after = 552 - 76 + 1
assert len(lines) == expected_after, f"Expected {expected_after} LoC after Task 2, got {len(lines)}"
print(f"New line count: {len(lines)}")

text = "".join(lines)
# Outer partial class still partial
assert "public partial class UdsClient : IDisposable" in text
# 3 ctors still in main
assert text.count("public UdsClient(") == 3
# All other flows still in main
assert "ReadDataByIdentifierAsync" in text  # Flow C
assert "WriteDataByIdentifierAsync" in text  # Flow C
assert "SecurityAccessAsync" in text  # Flow D
assert "TesterPresentAsync" in text  # Flow E
assert "RequestDownloadAsync" in text  # Flow E

# Transport moves preserved
assert "SendRequestAsync" not in text or "Flow A methods moved to" in text
# SessionFlow moves GONE from main:
assert "public virtual async Task<DiagnosticSessionResponse> DiagnosticSessionControlAsync" not in text
assert "public void StartTesterPresent" not in text
assert "public void StopTesterPresent" not in text

# Marker
marker = "    // === Flow B methods moved to UdsClient/SessionFlow.cs (W12 Task 2) ===\n"
for i in range(len(lines) - 1, -1, -1):
    if lines[i].strip() == "}":
        lines.insert(i, marker)
        print(f"Flow B marker inserted before line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")
```

**Step 3**: Run deletion:

```bash
cd D:/claude_proj2/peakcan-host
python scripts/w12_task2_delete_sessionflow.py
```

**Step 4**: Create `src/PeakCan.Host.Core/Uds/UdsClient/SessionFlow.cs`:

```csharp
namespace PeakCan.Host.Core.Uds;

public partial class UdsClient
{
    // Flow B: SessionControl (0x10 + 0x11) + S3 keepalive façades.
    // DiagnosticSessionControlAsync (0x10) + EcuResetAsync × 2 overloads (0x11)
    // + StartTesterPresent/StopTesterPresent (S3 keepalive scheduling, per
    // W12 D2 grouped with session-state-mutating ops not TesterPresent wire-emit).
    // Extracted from UdsClient.cs verbatim per W12 D5.

    /// <summary>
    /// DiagnosticSessionControl (0x10).
    /// </summary>
    /// <param name="sessionType">Session type (1=Default, 2=Extended, 3=Programming).</param>
    /// <param name="ct">Cancellation token.</param>
    public virtual async Task<DiagnosticSessionResponse> DiagnosticSessionControlAsync(byte sessionType, CancellationToken ct = default)
    {
        var response = await SendRequestAsync(0x10, new byte[] { sessionType }, ct).ConfigureAwait(false);

        // Parse response: [sessionType, P2high, P2low, P2*high, P2*low]
        if (response.Length < 5)
            throw new UdsException("Invalid DiagnosticSessionControl response");

        var result = new DiagnosticSessionResponse
        {
            SessionType = response[0],
            P2 = (response[1] << 8) | response[2],
            P2Star = (response[3] << 8) | response[4]
        };

        Session.SetSession(result.SessionType, result.P2, result.P2Star);

        // C-3 fix: propagate negotiated timings to UdsTimer so subsequent
        // requests honour the ECU's P2 / P2* (e.g. longer P2* in Programming
        // session). Without this, SendRequestInternalAsync would always use
        // the 50 ms default and time out on the first diagnostic request.
        _timer.P2Timeout = TimeSpan.FromMilliseconds(result.P2);
        _timer.P2StarTimeout = TimeSpan.FromMilliseconds(result.P2Star);

        return result;
    }

    /// <summary>
    /// ECUReset (0x11).
    /// </summary>
    /// <param name="resetType">Reset type (1=Hard, 2=KeyOff, 3=Soft).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// v1.3.0 MINOR Item 2: marked <c>virtual</c> for consistency with 7
    /// sibling UDS methods. Tests can override to intercept wire emit.
    /// Defensive length check on <c>response[0]</c> prevents
    /// <see cref="IndexOutOfRangeException"/> if <see cref="SendRequestAsync"/>
    /// returns an empty payload.
    /// </remarks>
    public virtual async Task<byte> EcuResetAsync(byte resetType, CancellationToken ct = default)
    {
        var response = await SendRequestAsync(0x11, new byte[] { resetType }, ct).ConfigureAwait(false);
        return response.Length > 0 ? response[0] : (byte)0;
    }

    /// <summary>
    /// v1.3.0 MINOR Item 2/4: type-safe enum overload.
    /// </summary>
    /// <param name="resetType">ISO 14229-1 §10.2 standard reset sub-function.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The echoed sub-function byte from the positive response.</returns>
    public Task<byte> EcuResetAsync(UdsResetType resetType, CancellationToken ct = default)
        => EcuResetAsync((byte)resetType, ct);

    /// <summary>Start automatic TesterPresent.</summary>
    public void StartTesterPresent(TimeSpan? interval = null)
    {
        Session.StartS3KeepAlive(this, interval);
    }

    /// <summary>Stop automatic TesterPresent.</summary>
    public void StopTesterPresent()
    {
        Session.StopS3KeepAlive();
    }
}
```

**Step 5**: `dotnet build` + `dotnet test --filter "~Uds"`:

```bash
cd D:/claude_proj2/peakcan-host
dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~Uds" --logger "console;verbosity=minimal"
```

Expected: 0 errors, test count unchanged.

**Step 6**: Commit:

```bash
cd D:/claude_proj2/peakcan-host
git add src/PeakCan.Host.Core/Uds/UdsClient.cs src/PeakCan.Host.Core/Uds/UdsClient/SessionFlow.cs scripts/w12_task2_delete_sessionflow.py
git commit -m "W12 Task 2: extract Flow B (SessionControl 0x10/0x11 + S3 keepalive façades) to partial"
```

---

## Task 3: Extract Flow C — DataIOFlow (0x22 + 0x2E + 0x19 + 0x14)

**Files:**
- Modify: `src/PeakCan.Host.Core/Uds/UdsClient.cs` (delete `ReadDataByIdentifierAsync` + `WriteDataByIdentifierAsync` + `ReadDtcInformationAsync` + `ClearDiagnosticInformationAsync`)
- Create: `src/PeakCan.Host.Core/Uds/UdsClient/DataIOFlow.cs`

**Step 1**: Re-count post-T2 (expected 477 LoC):

```bash
wc -l src/PeakCan.Host.Core/Uds/UdsClient.cs
grep -n "public virtual async Task<byte\[\]> ReadDataByIdentifierAsync\|public virtual async Task WriteDataByIdentifierAsync\|public virtual async Task<byte\[\]> ReadDtcInformationAsync\|public virtual async Task ClearDiagnosticInformationAsync" src/PeakCan.Host.Core/Uds/UdsClient.cs
```

**Step 2**: Write `scripts/w12_task3_delete_dataioflow.py` using confirmed post-T2 ranges.

**Step 3**: Run deletion.

**Step 4**: Create `src/PeakCan.Host.Core/Uds/UdsClient/DataIOFlow.cs` with verbatim extracted code (4 methods as listed in Flow C of spec).

**Step 5**: Build + test:

```bash
dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~Uds" --logger "console;verbosity=minimal"
```

Expected: 0 errors, tests pass.

**Step 6**: Commit:

```bash
git add src/PeakCan.Host.Core/Uds/UdsClient.cs src/PeakCan.Host.Core/Uds/UdsClient/DataIOFlow.cs scripts/w12_task3_delete_dataioflow.py
git commit -m "W12 Task 3: extract Flow C (DataIO + DTC: 0x22/0x2E/0x19/0x14) to partial"
```

---

## Task 4: Extract Flow D — SecurityFlow (0x27 × 2)

**Files:**
- Modify: `src/PeakCan.Host.Core/Uds/UdsClient.cs` (delete both `SecurityAccessAsync` overloads)
- Create: `src/PeakCan.Host.Core/Uds/UdsClient/SecurityFlow.cs`

**Step 1**: Re-count post-T3 (expected 419 LoC), grep for both `SecurityAccessAsync` methods + their xmldoc coverage.

**Step 2**: Write `scripts/w12_task4_delete_securityflow.py`.

**Step 3**: Run deletion.

**Step 4**: Create `src/PeakCan.Host.Core/Uds/UdsClient/SecurityFlow.cs` with verbatim extracted code (2 overloads — see spec Flow D).

**Step 5**: Build + test:

```bash
dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~Uds" --logger "console;verbosity=minimal"
```

Expected: 0 errors, tests pass.

**Step 6**: Commit:

```bash
git add src/PeakCan.Host.Core/Uds/UdsClient.cs src/PeakCan.Host.Core/Uds/UdsClient/SecurityFlow.cs scripts/w12_task4_delete_securityflow.py
git commit -m "W12 Task 4: extract Flow D (SecurityAccess 0x27 × 2 overloads) to partial"
```

---

## Task 5: Extract Flow E — TransferFlow (0x3E + 0x31 + 0x34 + 0x36 + 0x37)

**Files:**
- Modify: `src/PeakCan.Host.Core/Uds/UdsClient.cs` (delete `TesterPresentAsync` + `RoutineControlAsync` × 2 + `RequestDownloadAsync` + `TransferDataAsync` + `RequestTransferExitAsync`)
- Create: `src/PeakCan.Host.Core/Uds/UdsClient/TransferFlow.cs`

**Step 1**: Re-count post-T4 (expected 283 LoC).

**Step 2**: Write `scripts/w12_task5_delete_transferflow.py`. Range should cover TesterPresent (line ~410 area) through RequestTransferExit (~527 area in original).

**Step 3**: Run deletion.

**Step 4**: Create `src/PeakCan.Host.Core/Uds/UdsClient/TransferFlow.cs` with verbatim extracted code (6 methods as listed in Flow E of spec).

**Step 5**: Final build + tests:

```bash
dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~Uds" --logger "console;verbosity=minimal"
dotnet test --no-restore --nologo -c Debug --logger "console;verbosity=minimal"  # full solution
```

Expected: 0 errors, full test suite GREEN (all pre-W12 tests still pass).

**Step 6**: Commit:

```bash
git add src/PeakCan.Host.Core/Uds/UdsClient.cs src/PeakCan.Host.Core/Uds/UdsClient/TransferFlow.cs scripts/w12_task5_delete_transferflow.py
git commit -m "W12 Task 5: extract Flow E (Transfer: 0x3E/0x31/0x34/0x36/0x37) to partial"
```

Expected: Main file LoC reaches 166 (per LoC trajectory table). 5 partial files in UdsClient/.

---

## Task 6: Bump version v3.26.0 → v3.27.0 + write release notes (MINOR ship)

**Files:**
- Modify: `src/Directory.Build.props` (bump `<Version>v3.26.0</Version>` → `<Version>v3.27.0</Version>`)
- Create: `docs/release-notes-v3.27.0.md` (NEW)

**Step 1**: Read current `src/Directory.Build.props` to confirm version line:

```bash
cat src/Directory.Build.props | grep -A1 "<Version"
```

**Step 2**: Edit `src/Directory.Build.props`:

```bash
# Use Edit tool, old_string: "<Version>v3.26.0</Version>" (or whatever the exact line is)
#                   new_string: "<Version>v3.27.0</Version>"
```

**Step 3**: Create `docs/release-notes-v3.27.0.md` mirroring W10/W11 format. Content template:

```markdown
# Release notes — v3.27.0 (2026-07-12)

## God-class refactor: UdsClient

**Subject**: `src/PeakCan.Host.Core/Uds/UdsClient.cs` (704 LoC, 88.0% of 800 LoC ceiling) → 1 main file + 5 partial files (~166 LoC main).

**Files**:
- `src/PeakCan.Host.Core/Uds/UdsClient.cs` (704 → 166 LoC, **-538 LoC, -76.4%**)
- `src/PeakCan.Host.Core/Uds/UdsClient/TransportFlow.cs` (NEW)
- `src/PeakCan.Host.Core/Uds/UdsClient/SessionFlow.cs` (NEW)
- `src/PeakCan.Host.Core/Uds/UdsClient/DataIOFlow.cs` (NEW)
- `src/PeakCan.Host.Core/Uds/UdsClient/SecurityFlow.cs` (NEW)
- `src/PeakCan.Host.Core/Uds/UdsClient/TransferFlow.cs` (NEW)
- `scripts/w12_task{1-5}_delete_*.py` (NEW)

**Architecture**: partial-class split pattern (W3-W11) applied to **instance class with IDisposable + virtual methods + internal test seams + nested types**. Validates the pattern works across all god-class shapes in the project.

**Risks**: None. Pure mechanical extraction, zero behavioral change, zero test changes, zero API surface change. Test count unchanged.

**Verification**:
- `dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug`: 0 errors
- UDS tests: all pass (count unchanged from baseline)
- Full solution `dotnet test -c Debug`: GREEN

**Lessons applied**:
- W8.5 D7 LoC correction formula (`LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`)
- W11 D5 ctor-stays-with-fields (3 ctors + 6 readonly + 2 volatile-simulation fields all stay in main)
- W11 R3 helper-extract-on-demand (no method-body split needed; verified during T1 that `SendRequestInternalAsync` at 47 LoC + `OnMessageReceived` at 51 LoC both stay inline)
- R1 missing-usings (4 of 5 partials need 0 new usings; SecurityFlow may need `using System.Threading.Tasks` if any type lookup needs it — verified during T4)

**Next**: W12.5 vault-only PATCH (lesson-promotion: 2 NEW 1-of-1 candidates: `instance-idisposable-class-with-virtual-methods-and-internal-test-seams-fits-partial-pattern-identically` + `uds-protocol-flow-grouping-is-stable-across-iso-14229-services`) OR W13 (next candidate: ScriptEngine.cs 548 LoC / AscParser.cs 513 LoC / ReplayTimeline.cs 469 LoC).
```

**Step 4**: Commit:

```bash
cd D:/claude_proj2/peakcan-host
git add src/Directory.Build.props docs/release-notes-v3.27.0.md
git commit -m "chore(release): bump version to v3.27.0 + add release notes (W12 ship)"
```

---

## Task 7: Tier-3 push + tag + GH release

**Step 1**: Push branch to origin:

```bash
cd D:/claude_proj2/peakcan-host
git push -u origin feature/w12-uds-client-god-class
```

**Step 2**: Open PR (use gh CLI or web):

```bash
gh pr create --base main --head feature/w12-uds-client-god-class --title "W12 MINOR: UdsClient god-class refactor" --body "..."
```

PR body should reference:
- Link to spec: `docs/superpowers/specs/2026-07-12-uds-client-god-class-refactor.md`
- Link to plan: `docs/superpowers/plans/2026-07-12-uds-client-god-class-refactor.md`
- 9th god-class refactor (3rd Core layer, 1st IDisposable+virtual+instance)
- LoC delta 704 → 166 in main file (-76.4%)
- Test count unchanged
- Apply v3.27.0 tag post-merge

**Step 3**: Merge PR (squash or merge commits — match repo convention from W11 which was squash):

```bash
gh pr merge --squash --delete-branch
```

**Step 4**: Tag v3.27.0 on the merge commit:

```bash
cd D:/claude_proj2/peakcan-host
git pull origin main
git tag -a v3.27.0 -m "W12 MINOR: UdsClient god-class refactor (3rd Core layer, 1st IDisposable+virtual instance class)"
git push origin v3.27.0
```

**Step 5**: Create GitHub release:

```bash
gh release create v3.27.0 --title "v3.27.0 — UdsClient god-class refactor" --notes-file docs/release-notes-v3.27.0.md
```

**Step 6**: Verify release visible:

```bash
gh release view v3.27.0
```

Expected: shows release notes + tag.

**Step 7**: Done.

---

## Acceptance Criteria (full plan closure)

- [ ] `src/PeakCan.Host.Core/Uds/UdsClient.cs` ≤ 200 LoC
- [ ] 5 partial files in `src/PeakCan.Host.Core/Uds/UdsClient/` directory
- [ ] Outer class is `public partial class UdsClient : IDisposable`
- [ ] `dotnet build src/PeakCan.Host.Core/`: 0 errors
- [ ] `dotnet test --filter "~Uds"`: test count unchanged from baseline, 0 new fails
- [ ] `dotnet test` (full solution): all pre-W12 tests pass, 0 new fails
- [ ] PR #38+ merged to main
- [ ] tag v3.27.0 on the merge commit
- [ ] GH release v3.27.0 published
- [ ] branch `feature/w12-uds-client-god-class` deleted post-merge
- [ ] MEMORY.md updated with v3.27.0 ship entry
- [ ] devlog entry written with 6 source commits + 1 ship + 1 spec + 1 plan
- [ ] capture-decisions file written at `01-Projects/peakcan-host/development/capture-decisions/2026-07-12-w12-uds-client-god-class-ship.md`

---

## Closing milestone context (verbatim from spec)

This is the **9th god-class refactor** in the project. UdsClient is the **3rd Core layer** god-class (W9 IsoTpLayer + W10 DbcParser + W12 UdsClient). UdsClient is the **first Core-layer instance class** with IDisposable + virtual + internal-test-seam patterns. Validates the partial-class split pattern works for: instance class with IDisposable + virtual methods + internal test seams + nested types (sister of W3-W8 ViewModels, but on Core layer).

If W12 ships + tests pass + lesson confirmations hold, W12.5 vault-only PATCH (lesson-promotion) and W13 (next candidate) become natural next steps.
