# Release Notes — v1.6.7 PATCH

**Date:** 2026-06-30
**Version:** v1.6.7 (PATCH)
**Previous:** v1.6.6 (PATCH)
**Commits since v1.6.6 (`f26fd3d`):** 4 task commits (`6c28d04` RED + `8e6138f` GREEN + `a1304ee` Item 4 docs + `a1a53d6` review-fixup) + 1 docs commit

## 概述

v1.6.7 PATCH is a **4-item Tidy PATCH** clearing 4 of the 5 v1.6.6 review MEDIUM carry-overs plus the 8+ release `DbcErrorCode.FileTooLarge` categorical carry-over. v1.6.0 MINOR 5-item decomposition PATCH 4 of 5 (V8 sandbox + OEM crypto still deferred).

| # | Item | Source | User-facing | Severity |
|---|------|--------|-------------|----------|
| 1 | **Wire `DbcErrorCode.FileTooLarge` categorical code** — append `ErrorCode.DbcFileTooLarge = 10` (wire-stable) to the canonical `ErrorCode` enum; `DbcService.LoadAsync` size-cap rejection emits `ErrorCode.DbcFileTooLarge` instead of `ErrorCode.ParseFailure` + disambiguating message. `DbcErrorCode.FileTooLarge` slot remains as forward-compat duplicate. Closes the 8+ release carry-over. | v1.6.6 review MEDIUM #3 + 8+ release carry-over | Yes (slightly different `Status` text) | MEDIUM |
| 2 | **Unify unlimited sentinel to `0`** — collapse `DbcParser`'s mixed convention (`int.MaxValue` default + `0 → int.MaxValue` conversion at seam) onto `DbcOptions`'s `0 = unlimited` convention. Removes the conversion indirection + the unreachable `ArgumentOutOfRangeException.ThrowIfNegative` (negative values now uniformly treated as unlimited). | v1.6.6 review MEDIUM #2 | No (no caller-visible behavior change; 2 new boundary tests added) | MEDIUM |
| 3 | **Concurrent caller test for cap concurrency** — new `[Fact]` `LoadAsync_Concurrent_Calls_All_Converge_Without_Race_Or_Exception` fires 10 concurrent `LoadAsync` calls on same `DbcService` instance + asserts no exceptions on `Task.WhenAll` + `DbcLoaded` fires 10 times + `LoadFailed` 0 times. Characterizes the as-built last-write-wins concurrency model. No production change. | v1.6.6 review MEDIUM (concurrent caller test) | No (test-only) | MEDIUM |
| 4 | **Replace file:line doc anchors with stable XML refs** — `DbcSendViewModel.cs:134 + :140` replace `DbcService.cs:17-22 class doc` + `DbcViewModel.OnLoaded pattern (lines 112-147)` with `<see cref="..."/>` anchors. Closes MEDIUM #4 (line-number doc reference fragility). | v1.6.6 review MEDIUM #4 | No (doc-only) | LOW |

### Non-Goals (per design doc)

- `DbcErrorCode` enum extension: stays at 9 values. `FileTooLarge` slot is now a forward-compat duplicate of canonical `ErrorCode.DbcFileTooLarge`.
- New `ErrorCode.DbcMessageCountExceeded`: skipped. Message-count cap keeps using `ErrorCode.ParseFailure` (mid-parse, same surface as other parse errors). Matches v1.6.6 design doc Decision 3 spirit.
- Add locking to `DbcService.LoadAsync`: deferred to a separate architectural decision (separate MINOR).
- Configurable unlock pattern: deferred (YAGNI, no operator demand).
- `DbcApi.Load` script observability (`LoadFailed` subscription): MEDIUM #1 from pre-ship review deferred to v1.6.8 PATCH per scope discipline.
- Concurrent test event-order assertion: MEDIUM #2 accepted as-is.
- Per-message signal cap (YAGNI).
- Release-notes / design-doc line refs (historical artifacts).

## Items

### Item 1 — Wire `DbcErrorCode.FileTooLarge`

**Files**:
- `src/PeakCan.Host.Core/ErrorCode.cs` (MODIFY, +3 LOC) — append `DbcFileTooLarge` at end of enum.
- `src/PeakCan.Host.Core/Dbc/DbcErrorCode.cs` (MODIFY, XML doc only) — note `FileTooLarge` slot now a forward-compat duplicate.
- `src/PeakCan.Host.App/Services/DbcService.cs` (MODIFY, 1 LOC) — `DbcService.LoadAsync` size-cap rejection emits `ErrorCode.DbcFileTooLarge`.
- `src/PeakCan.Host.App/Services/DbcOptions.cs` (MODIFY, XML doc) — refresh stale "Failure envelope" paragraph (post-review HIGH fix).
- `tests/PeakCan.Host.App.Tests/Services/DbcServiceLimitTests.cs` (MODIFY) — rename `LoadAsync_File_Above_MaxFileSize_Fires_LoadFailed_With_ParseFailure_Message` → `..._With_DbcFileTooLarge_Error_Code`; flip file-size assertion to `ErrorCode.DbcFileTooLarge`. The combined-caps real-fixture test accepts either `DbcFileTooLarge` or `ParseFailure` (whichever fires first).

**Background**: 8+ consecutive release notes listed `DbcErrorCode.FileTooLarge` as a forward-compat slot that was never wired. v1.6.6 PATCH intentionally left it unwired because the `Error` record's positional ctor accepts `ErrorCode` not `DbcErrorCode`. v1.6.7 PATCH closes the carry-over by extending the `ErrorCode` enum (wire-stable append — XML doc line 4-6 says "Stable integer values").

**Change**: append at end of `ErrorCode.cs`:

```csharp
    /// <summary>DBC file exceeds the configured MaxFileSizeBytes cap (v1.6.7 PATCH Item 1).</summary>
    DbcFileTooLarge,
```

(Appended after `Cancelled`. Auto-numbers to 10; wire-stable preserved.)

`DbcService.LoadAsync` size-cap branch (was line 123):

```csharp
var err = new Error(ErrorCode.DbcFileTooLarge,
    $"file size {bytes.Length} bytes exceeds MaxFileSizeBytes {_options.MaxFileSizeBytes} at {path}");
```

The disambiguating `Message` string is preserved (operator can read the cap value).

`DbcOptions.cs:22-40` "Failure envelope" paragraph refreshed (review-fixup commit `a1a53d6`): now describes v1.6.7 reality (size cap → `DbcFileTooLarge`, message-count cap → `ParseFailure` + disambiguating message), removes the now-incorrect "intentionally unwired" sentence.

**Tests** (2 modified, 0 net):
- File-size test renamed + assertion flipped to assert `ErrorCode.DbcFileTooLarge`.
- Combined-caps real-fixture test assertion relaxed to accept either code (whichever fires first — file trips both caps).

### Item 2 — Unified unlimited sentinel

**Files**:
- `src/PeakCan.Host.Core/Dbc/DbcParser.cs` (MODIFY, ±16 LOC) — 5 surgical changes at line 30 (2-arg delegation), lines 39-65 (3-arg Parse method body), line 90 (ParserState default), line 152 (mid-parse check), and XML docs at 32-38, 86-87, 149-151.
- `tests/PeakCan.Host.Core.Tests/DbcParserTests.cs` (MODIFY, +48 LOC) — 2 boundary tests.

**Background**: v1.6.6 mixed conventions: `DbcOptions` uses `0 = unlimited`, but `DbcParser`'s 3-arg overload defaulted `int.MaxValue` and did `0/neg → int.MaxValue` conversion at the seam; the conversion clause `maxMessageCount <= 0` was effectively unreachable for negative values (the `ThrowIfNegative` check above short-circuited first). v1.6.7 unifies on `0 = unlimited` everywhere.

**Change** (5 surgical edits in `DbcParser.cs`):

1. **Line 30** (2-arg Parse delegation):
   - From: `=> Parse(text, maxMessageCount: int.MaxValue, ct);`
   - To:   `=> Parse(text, maxMessageCount: 0, ct);`

2. **Line 39** (3-arg Parse method body):
   - Removed: `ArgumentOutOfRangeException.ThrowIfNegative(maxMessageCount);` (line 42)
   - Removed: `var effectiveCap = maxMessageCount <= 0 ? int.MaxValue : maxMessageCount;` (lines 44-47)
   - Changed: `new ParserState(tokens, effectiveCap)` → `new ParserState(tokens, maxMessageCount)` (line 52)

3. **Line 90** (ParserState default arg):
   - From: `int maxMessageCount = int.MaxValue`
   - To:   `int maxMessageCount = 0`

4. **Line 152** (mid-parse check):
   - From: `if (_maxMessageCount < _pendingMessages.Count)` (fires when `count > cap`)
   - To:   `if (_maxMessageCount > 0 && _pendingMessages.Count > _maxMessageCount)` (same `count > cap` semantic for `cap > 0`; `cap == 0` short-circuits to unlimited)

5. XML docs at lines 32-38 (3-arg Parse overload), 86-87 (ParserState `_maxMessageCount` field), 149-151 (mid-parse check) updated to reflect `0 = unlimited` convention.

**Tests** (2 new in `DbcParserTests.cs`, both adopt `0 = unlimited`):

```csharp
[Fact]
public void Parse_With_MaxMessageCount_Zero_Treats_As_Unlimited()
{
    // 5 messages, cap=0 → all parsed.
    ...
}

[Fact]
public void Parse_With_MaxMessageCount_Negative_Treats_As_Unlimited()
{
    // 3 messages, cap=-1 → all parsed (was throw in v1.6.6).
    ...
}
```

### Item 3 — Concurrent caller test

**Files**:
- `tests/PeakCan.Host.App.Tests/Services/DbcServiceLimitTests.cs` (MODIFY, +45 LOC) — new `[Fact]`.

**Background**: v1.6.6 added cap rejection but didn't verify behavior under concurrent `LoadAsync` calls (multiple callers, same DBC file). v1.6.7 characterizes the as-built last-write-wins concurrency model.

**Change**: new `[Fact]` `LoadAsync_Concurrent_Calls_All_Converge_Without_Race_Or_Exception`:

```csharp
[Fact]
public async Task LoadAsync_Concurrent_Calls_All_Converge_Without_Race_Or_Exception()
{
    var svc = NewUnlimited();
    var failCount = 0;
    var loadCount = 0;
    var failLock = new object();
    var loadLock = new object();
    svc.LoadFailed += _ => { lock (failLock) failCount++; };
    svc.DbcLoaded += _ => { lock (loadLock) loadCount++; };

    var paths = Enumerable.Range(0, 10)
        .Select(_ => WriteTempDbc(OneMessageDbc))
        .ToList();

    try
    {
        await Task.WhenAll(paths.Select(p => svc.LoadAsync(p)));

        loadCount.Should().Be(paths.Count);
        failCount.Should().Be(0);
        svc.Current.Should().NotBeNull();
    }
    finally
    {
        foreach (var p in paths) File.Delete(p);
    }
}
```

No production code change. Test only verifies the concurrency surface (no exceptions on `Task.WhenAll`); doesn't assert event-order matches write-order (defensible per design doc Decision 6). The MEDIUM #2 doc-only assertion gap (event-order not asserted) is documented in the test's purpose and accepted.

Uses existing `WriteTempDbc` helper (no new fixture migration triggered — 14 fixture-migration-candidates pre+v1.6.6; +0 for v1.6.7).

### Item 4 — XML doc anchor replacement

**Files**:
- `src/PeakCan.Host.App/ViewModels/DbcSendViewModel.cs` (MODIFY, 2 lines) — replace `<c>DbcService.cs:17-22</c> class doc` → `<see cref="DbcService"/>` threading remarks at line 134; drop `(lines 112-147)` from "Mirrors the `<see cref="DbcViewModel.OnLoaded"/>` pattern" at line 140.

**Background**: file:line references inside XML doc comments drift on every refactor. Replace with stable `<see cref="..."/>` symbol anchors. Closes v1.6.6 review MEDIUM #4.

**Change**:
- Line 134: `thread (see the threading remarks on <see cref="DbcService"/>)` (was `thread (see <c>DbcService.cs:17-22</c> class doc)`)
- Line 140: `Mirrors the <see cref="DbcViewModel.OnLoaded"/> pattern which uses the same chokepoint.` (was `Mirrors the <see cref="DbcViewModel.OnLoaded"/> pattern (lines 112-147) which uses the same chokepoint.`)

**Tests**: 0 (doc-only).

## Test counts

| Suite | v1.6.6 baseline | v1.6.7 PATCH | Delta |
|-------|-----------------|--------------|-------|
| Core  | 347             | 349          | +2 (DbcParserTests boundary tests) |
| App   | 419 (combined-cap-test relaxed; concurrent-test added) | 421 | +2 (Item 3 concurrent test; file-size test rename doesn't net) |
| Infra | 84              | 84           | 0 |
| **Total** | **850 + 6 SKIP** | **852 + 6 SKIP** | **+2 net** |

Confirmed via `dotnet test PeakCan.Host.slnx -c Debug` on `feature/v1-6-7-patch` (post-fixup): **852 + 6 SKIP / 1 race-test flake**. Pre-existing flake confirmed 7-of-7+ occurrences across v1.6.2 / v1.6.3 / v1.6.4 / v1.6.5 / v1.6.6 / v1.6.7. Passes in isolation. Mitigation deferred ([Retry(3)] xUnit attribute explicitly DECIDED NOT to add in v1.6.1 PATCH Decision 5).

## Process lessons (NEW)

1. **Single-convention sentinel cleanup** — collapsing a mixed `int.MaxValue` + `0 = unlimited` convention onto a uniform `0 = unlimited` convention removes a 4-line conversion indirection (`var effectiveCap = ... ? int.MaxValue : ...`) and an unreachable `ArgumentOutOfRangeException.ThrowIfNegative` check (negative values threw, but never reached the negative-branch conversion in the line below). The cleanup changed the negative-value semantic from throw → unlimited; 1 of 2 new boundary tests specifically exercises this (the other exercises `0 = unlimited` which was already-treated-as-unlimited pre- and post-). Lesson: when a sentinel has 2 representations, the conversion indirection + the validation that prevents the conversion from working both want removal. **Applies broadly** to other config-bound cap parameters (`Send:MaxFramesPerSecond`, etc.).

2. **XML doc anchor fragility** — `<c>file:line</c>` references inside XML doc comments drift on every refactor (DbcSendViewModel.cs:134 referenced `DbcService.cs:17-22`; the cited `DbcService.cs` partial-class declaration is now at line 34). Replace with `<see cref="MemberName"/>` symbol anchors where possible; drop parenthetical line ranges where `<see cref>` alone communicates the intent. Lesson: prefer `<see cref>` over `<c>file:line</c>` in XML doc comments for cross-class references.

3. **Pre-existing race-test flake (7-of-7+ confirmed)** — `CyclicDbcSendServiceRaceTests.Encode_Failure_Increments_FailureCount_Not_SuccessCount` failed in the v1.6.7 full-suite run; passes in isolation; not introduced by v1.6.7 PATCH. Establishes that any future PATCH hitting this test in full-suite mode can attribute the failure to the inherited flake (mitigation: re-isolate via `--filter`). Per established workflow per v1.6.1 PATCH Decision 5: `[Retry(3)]` xUnit attribute explicitly rejected.

4. **Pre-ship code review caught stale XML doc** — reviewer found `DbcOptions.cs:22-33` still claimed "intentionally unwired" (v1.6.6-only context) on `DbcErrorCode.FileTooLarge` despite v1.6.7 wiring `ErrorCode.DbcFileTooLarge`. The 4 production-code commits didn't touch `DbcOptions.cs`. Lesson: when adding a new enum value, scan all XML docs referencing the enum (or its member slots) for stale assumptions about the wiring state. (Reviewer also flagged MEDIUM `DbcApi.Load` script observability — out of v1.6.7 scope.)

5. **Test-method name must match the assertion, not the brief** — v1.6.6 PATCH Lesson #5 (test-assertion-alignment sub-shape 9) inherited: reviewer's MEDIUM noted that the brief-vs-source drift cycle on the previous PATCH could have caught this. The renamed test method `..._With_DbcFileTooLarge_Error_Code` correctly matches its assertion `Be(ErrorCode.DbcFileTooLarge)`.

6. **ErrorCode enum extension vs Error record overload** (Design Decision 1, now realized) — chose `ErrorCode` enum append (Option A) over `Error(DbcErrorCode, string)` ctor overload (Option B) or `Error.DbcCode` discriminator field (Option C). Rejection rationale (determinative): `Error` is `sealed record` with single positional ctor — adding overload breaks positional `with` expressions in callers; adding discriminator doubles the API surface for the same end result. Enum extension is wire-stable per `ErrorCode.cs:4-6`. Decision applies broadly to other `CategoricalEnum`-style code paths that want to flow through the shared envelope.

## Brief-vs-source drift (continued, 11-of-11+)

| # | Brief claim | Source reality | Drift sub-shape |
|---|-------------|----------------|-----------------|
| 1 | "Wire `DbcErrorCode.FileTooLarge`" | Picked Option A (ErrorCode enum extension) at Plan-time. `DbcErrorCode.FileTooLarge` becomes forward-compat duplicate. | (Plan-time decision, no drift) |
| 2 | "Unify sentinel to 0" | Implementation removed 4-line `effectiveCap` conversion + the unreachable `ThrowIfNegative`. Negative-value semantic changed from throw → unlimited; 1 of 2 boundary tests specifically covers this. | Test 2.2 captures the change proactively |
| 3 | "Concurrent test characterizes only" | No production lock; if test flake persists, separate Architectural decision required. | (deferred to v1.6.8+ if needed) |
| 4 | "Replace `<c>file:line</c>` with `<see cref/>`" | DbcSendViewModel.cs:134 + :140 only; release notes / design docs / historical files left untouched. | (scope-disciplined per design doc Non-Goal 7) |
| 5 | (Plan-doc stale claim) "0 test fixture migration (Item 3 reuses WriteTempDbc)" | Confirmed. pre-existing 14 fixture-migration-candidates + 0 new from Item 3. | None — verified |

Drift caught at: Phase 2.5 brief-drift-correction (design-time file-existence + signature verification), pre-ship code-reviewer (HIGH #1 caught stale `DbcOptions.cs` XML doc inline before merge).

## Files changed

```
 docs/release-notes-v1.6.7.md                                   (new, this file)
 src/PeakCan.Host.Core/ErrorCode.cs                            (+3 LOC: append DbcFileTooLarge)
 src/PeakCan.Host.Core/Dbc/DbcErrorCode.cs                     (XML doc tightened)
 src/PeakCan.Host.Core/Dbc/DbcParser.cs                        (5 surgical sentinel unify)
 src/PeakCan.Host.App/Services/DbcService.cs                   (ErrorCode.ParseFailure → DbcFileTooLarge for size cap)
 src/PeakCan.Host.App/Services/DbcOptions.cs                   (XML doc refreshed)
 src/PeakCan.Host.App/ViewModels/DbcSendViewModel.cs          (2 doc anchor replacements)
 tests/PeakCan.Host.App.Tests/Services/DbcServiceLimitTests.cs (Item 1 rename + assertion flip; Item 3 concurrent test)
 tests/PeakCan.Host.Core.Tests/DbcParserTests.cs               (+2 boundary tests)
```

## Known follow-ups

- **v1.6.0 MINOR still deferred** (10th consecutive release notes list, was 9th; **2 remaining items**): V8 sandbox hardening + OEM `IKeyDerivationAlgorithm` concrete. v1.6.7 PATCH = v1.6.0 MINOR 5-item decomposition PATCH 4 of 5. 4 of 5 items closed (path norm root + rate limit + DBC limits + DbcErrorCode wiring).
- **v1.6.8 PATCH candidate**: DbcApi.Load `LoadFailed` subscription (script observability) — pre-ship code review MEDIUM #1. Decision deferred to next-cycle brainstorm. Alternative candidates: per-sender `RejectedFrameCount` UI exposure, configurable unlock pattern, [Retry(3)] reversal decision.
- **Core-safe PEAK classic-code mapping** (carry-over from v1.6.4): enables `BaudRate.FromDescriptor(descriptor, name, classicCode)` 3-arg overload. Deferred to v1.6.x MINOR.
- **Config-driven path allowlist** (carry-over from v1.6.4): `Path:AllowedRoots:[]` in `appsettings.json`. When added, future DBC `NormalizeRestricted` wiring would slot in.
- **Future rate-limit/dbc-limit extensions** (carry-over from v1.6.5 + v1.6.6): per-caller quota + multi-config (`Replay:MaxFramesPerSecond` separate) — all explicitly deferred.
- **Race-test full stability verification**: pre-existing flake confirmed in v1.6.7 PATCH (7-of-7+ occurrences). Same `Cyclic{Dbc,}SendServiceRaceTests` 2-3 intermittent failures per full-suite run; pass in isolation. Mitigation deferred (deterministic timer stub vs `[Retry(3)]` xUnit attribute — `[Retry(3)]` explicitly DECIDED NOT to add in v1.6.1 PATCH Decision 5).
- **Long-term Non-Goals** (since v1.4.0): DBC Value table encoding + Multiplexed signal groups UI + Replay→Trace auto-load.
- **v1.6.7 PATCH ship-new carry-overs**: 0 HIGH closed inline (`DbcOptions.cs` + `DbcErrorCode.cs` XML docs). 1 MEDIUM (`DbcApi.Load` script observability) deferred to v1.6.8. 2 MEDIUMs accepted as-is + 3 LOWs informational.
- **Untracked design/plan docs**: 6 files in `docs/superpowers/{plans,specs}/` from v1.6.5 + v1.6.6 + v1.6.7 PATCH sessions are untracked on `main`. Per design doc Non-Goal + plan Task 12 Option B: leave as-is until separate housekeeping cycle.

## Ship method

```
1. git checkout -b feature/v1-6-7-patch (from main @ f26fd3d)    [DONE]
2. 4 task commits (RED 6c28d04, GREEN 8e6138f, Item 4 docs a1304ee,
   review-fixup a1a53d6) (DONE)
3. Pre-ship code-reviewer subagent: 0C/1H/3M/3L WARNING           [DONE]
4. docs/release-notes-v1.6.7.md (this file)                       [DONE]
5. git -c http.proxy="http://127.0.0.1:7897" -c https.proxy=... push
   -u origin feature/v1-6-7-patch (5th consecutive PATCH with
   proxy-ON ship pattern)                                         [pending]
6. gh pr create --base main                                       [pending]
7. gh pr merge --squash --delete-branch                          [pending]
8. git fetch origin main + git reset --hard origin/main          [pending]
9. git tag v1.6.7 + git push origin v1.6.7                       [pending]
10. gh release create v1.6.7 --notes-file docs/release-notes-v1.6.7.md
                                                                  [pending]
11. Update MEMORY.md + write peakcan-host-v1-6-7-shipped.md    [pending]
```

## Cross-references

- `[[peakcan-host-v1-6-6-shipped]]` — previous PATCH (DbcOptions in-service + 4 entry points + opt-in config). Carries 5 MEDIUM carry-overs; v1.6.7 closes 3 of them (Items 1, 2, 3).
- `[[phase-2-5-brief-drift-correction]]` — 11 sub-shapes confirmed. v1.6.7 had 0 brief-drift catches at Phase 2.5 (lighter scope than v1.6.6); 1 drift catch at pre-ship code review (HIGH #1, `DbcOptions.cs` stale doc).
- `[[Workflow overhead feedback]]` — Tidy PATCH (≤2 weeks): established workflow followed (Phase 1 explore → Phase 2.5 brief-drift → Phase 2 plan → Phase 3 TDD → Phase 4 review → Phase 5 ship). 1 review round.
- `[[Git push network workaround]]` — 5th consecutive PATCH with proxy-ON first-try success (v1.6.3 + v1.6.4 + v1.6.5 + v1.6.6 + v1.6.7). Network state stable.

## Open Questions

- None. v1.6.7 PATCH scope is closed; 4 items shipped. 1 HIGH finding addressed in review-fixup commit. 1 MEDIUM deferred to v1.6.8. 2 MEDIUMs + 3 LOWs accepted as-is.
