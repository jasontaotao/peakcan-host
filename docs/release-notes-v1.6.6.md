# Release Notes — v1.6.6 PATCH

**Date:** 2026-06-30
**Version:** v1.6.6 (PATCH)
**Previous:** v1.6.5 (PATCH)
**Commits since v1.6.5 (`907c449`):** 3 task commits (`6dcb95a` RED + `5fd6c08` GREEN + `641a557` review-fixup)

## 概述

v1.6.6 PATCH 是 **v1.6.0 MINOR 5-项拆解策略的第三个 PATCH**。v1.6.4 / v1.6.5 ship 后剩 3 项 long延 follow-ups；本次 ship 关闭其中 DBC size/token limits：

| # | Item | Status |
|---|------|--------|
| 1 | V8 sandbox hardening | 仍 deferred（architectural，可能自成一 MINOR） |
| 2 | ~~CanApi rate limit~~ | ✓ v1.6.5 PATCH ship (previous) |
| 3 | **DBC size/token limits** | **✓ v1.6.6 PATCH ship** |
| 4 | ~~Path norm root restriction~~ | ✓ v1.6.4 PATCH ship (previous-previous) |
| 5 | OEM `IKeyDerivationAlgorithm` concrete | 仍 deferred（crypto review needed） |

**本次 ship**: DBC file-size + message-count caps (v1.6.0 MINOR 5 项中第三小单项)。closes v1.6.5 release notes "Known follow-ups" → "v1.6.6 PATCH candidate: DBC size/token limits"。

| # | Item | Severity | User-facing |
|---|------|----------|-------------|
| 1 | `DbcOptions` config record + size pre/post-read cap + message-count mid-parse cap (opt-in via `Dbc:MaxFileSizeBytes` + `Dbc:MaxMessageCount`) | MEDIUM | Yes (status string surfaces "exceeds MaxFileSizeBytes N" / "exceeds MaxMessageCount N") |

**Scope discipline**:
- 0 test fixture migration (Item 1 introduces no path/network/process restriction; pre-existing 13 + 1 new = 14 fixture-migration-candidate hits, the +1 is the new test's `%TEMP%` helper legitimately outside the v1.6.4 `%LOCALAPPDATA%\PeakCan.Host\` restriction).
- 0 `DbcErrorCode.FileTooLarge` wiring — the canonical error envelope (`Error(ErrorCode Code, string Message)`) doesn't accept `DbcErrorCode`. Size cap rejection uses `ErrorCode.ParseFailure` + disambiguating message string. The `DbcErrorCode.FileTooLarge` slot declared in `DbcErrorCode.cs:19` remains intentionally unwired (forward-compat for future categorical DBC errors).
- 0 caller-side changes: 4 entry points (`DbcViewModel.OpenAsync`, `DbcSendViewModel.OnLoaded`, `DbcApi.Load`, app config) all funnel through `DbcService.LoadAsync`. Cap applies to all 4 transparently.
- Hard-coded default 0/unlimited for both caps — opt-in via config only (mirror v1.6.5 `Send:MaxFramesPerSecond: 0` precedent).

**Architectural deviation from v1.6.5 (documented)**: v1.6.5 used decorator pattern (`RateLimitedSendService : SendService` with DI factory). v1.6.6 uses **in-service check** via `DbcOptions` config record. Rationale: 2 chokepoints (size pre-read + message-count mid-parse) require either 2 stacked decorators (ugly) or this in-service pattern. Both legitimate; choice driven by seam geometry, not consistency. The decorator pattern stays valid for single-chokepoint use cases.

## Items

### Item 1 — `DbcOptions` + size + message-count caps

**Files**:
- `src/PeakCan.Host.App/Services/DbcOptions.cs` (NEW, ~50 lines) — `internal sealed record DbcOptions(long MaxFileSizeBytes, int MaxMessageCount)` with `static Unlimited { } = new(0, 0)`. Bound from `appsettings.json:Dbc` section via DI factory.
- `src/PeakCan.Host.Core/Dbc/DbcParser.cs` (MODIFY) — new `Parse(string text, int maxMessageCount, CancellationToken ct = default)` overload. Existing 2-arg delegates to new 3-arg with `int.MaxValue` (back-compat sentinel). `ParserState` ctor accepts `maxMessageCount = int.MaxValue` default. Mid-parse cap check in `ParseDocument` after each `BO_` accept — throws `DbcParseException` (caught by `DbcParser.Parse` envelope, converted to `Result<DbcDocument>.Fail(ParseFailure, msg)`).
- `src/PeakCan.Host.App/Services/DbcService.cs` (MODIFY) — `_options` field. New `internal DbcService(ILogger<DbcService>, DbcOptions)` 2-arg ctor; existing 1-arg ctor delegates with `DbcOptions.Unlimited` (back-compat). `LoadAsync` reads bytes via new `ReadDbcBytesAsync` helper, then post-read check `bytes.Length > MaxFileSizeBytes` (TOCTOU-free, see Process Lesson #1). New `ReadDbcText(byte[])` decoder (BOM + UTF-8-with-Latin-1-fallback). `LoadFailed` event raised on cap violation with `ErrorCode.ParseFailure` + disambiguating message. New `LogLoadSizeFailed` source-gen warning.
- `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` (MODIFY) — `DbcOptions` factory reading `IConfiguration.GetSection("Dbc")` + `DbcService` 2-arg factory (around line 180).
- `appsettings.json` (MODIFY, additive) — `Dbc: { MaxFileSizeBytes: 0, MaxMessageCount: 0 }`. Default 0/unlimited opt-in.
- `tests/PeakCan.Host.Core.Tests/DbcParserTests.cs` (MODIFY, +2 tests for `Parse(string, int, CT)` overload).
- `tests/PeakCan.Host.App.Tests/Services/DbcServiceLimitTests.cs` (NEW, ~180 lines, 5 tests).

**Background**: v1.6.0 MINOR security/limits audit (2026-05-25 joint review, never shipped intact) listed DBC size + token limits as item 3 of a 5-item decomposition. 5+ consecutive release notes listed it as deferred. v1.6.4 / v1.6.5 PATCHes closed items 4 (path norm root) + 2 (rate limit); v1.6.6 closes item 3.

**Attack surface closed**:
- File-level DoS: attacker-supplied 2 GB DBC allocates a 2 GB `byte[]` via `File.ReadAllBytesAsync`, exhausting process memory or causing OOM. New: post-read check `bytes.Length > MaxFileSizeBytes` aborts the load before `string` allocation (the bytes never reach the decoder). Cap operates on the just-read byte count — TOCTOU window closed (see Process Lesson #1).
- Parse-level DoS: attacker-supplied DBC with 100 000 `BO_` messages accumulates `Dictionary<uint, Message>` + `List<Message>` + per-message `List<Signal>` during parse. New: mid-parse check `_pendingMessages.Count > _maxMessageCount` after each `BO_` accept throws before subsequent allocations.

**Change**:

1. **`DbcOptions.cs`** — config record:
   ```csharp
   internal sealed record DbcOptions(long MaxFileSizeBytes, int MaxMessageCount)
   {
       public static DbcOptions Unlimited { get; } = new(0, 0);
   }
   ```
   `internal` because cap configuration is an App-layer DI seam concern (NetArchTest rule 2: Core must not depend on PEAK SDK; option records belong in App). Test project gains access via `InternalsVisibleTo`.

2. **`DbcParser.cs`** — new 3-arg overload + cap check:
   ```csharp
   public static Result<DbcDocument> Parse(string text, CancellationToken ct = default)
       => Parse(text, maxMessageCount: int.MaxValue, ct);

   public static Result<DbcDocument> Parse(string text, int maxMessageCount, CancellationToken ct = default)
   {
       ArgumentNullException.ThrowIfNull(text);
       ArgumentOutOfRangeException.ThrowIfNegative(maxMessageCount);
       // 0 (or any value <= 0) disables the cap — convert to int.MaxValue so
       // ParserState's mid-parse check is a no-op.
       var effectiveCap = maxMessageCount <= 0 ? int.MaxValue : maxMessageCount;
       try { /* tokenize + parse + check ct + return Result.Ok / Fail */ }
       catch (DbcParseException ex)
       { return Result<DbcDocument>.Fail(ErrorCode.ParseFailure, ex.Message); }
   }
   ```
   Inside `ParseDocument`, after each `BO_` accept (`_pendingMessages.Add(msg)`):
   ```csharp
   if (_maxMessageCount < _pendingMessages.Count)
   {
       throw new DbcParseException(
           $"message count {_pendingMessages.Count} exceeds MaxMessageCount {_maxMessageCount}",
           Current.Line, Current.Column);
   }
   ```
   Use `<` (not `>=`) so the `(N+1)`th message triggers a more informative diagnostic.

3. **`DbcService.cs`** — 2-arg ctor + post-read TOCTOU-free check + bytes-with-text refactor:
   ```csharp
   private readonly DbcOptions _options;

   public DbcService(ILogger<DbcService> logger)
       : this(logger, DbcOptions.Unlimited) { }   // back-compat

   internal DbcService(ILogger<DbcService> logger, DbcOptions options)
   {
       _logger = logger ?? throw new ArgumentNullException(nameof(logger));
       ArgumentNullException.ThrowIfNull(options);
       _options = options;
   }

   public virtual async Task LoadAsync(string path, CancellationToken ct = default)
   {
       try
       {
           var bytes = await ReadDbcBytesAsync(path, ct).ConfigureAwait(false);

           // TOCTOU-free cap: bytes.Length is the bytes we just read.
           if (_options.MaxFileSizeBytes > 0 && bytes.Length > _options.MaxFileSizeBytes)
           {
               var err = new Error(ErrorCode.ParseFailure,
                   $"file size {bytes.Length} bytes exceeds MaxFileSizeBytes {_options.MaxFileSizeBytes} at {path}");
               LogLoadSizeFailed(_logger, path, _options.MaxFileSizeBytes, bytes.Length);
               LoadFailed?.Invoke(err);
               return;
           }

           var text = ReadDbcText(bytes);
           var r = await Task.Run(() => DbcParser.Parse(text, _options.MaxMessageCount, ct), ct).ConfigureAwait(false);
           ct.ThrowIfCancellationRequested();
           // ... r.IsSuccess / r.Error handling ...
       }
       catch ... { /* existing IO / cancel / last-resort */ }
   }

   private static async Task<byte[]> ReadDbcBytesAsync(string path, CancellationToken ct)
   {
       return await File.ReadAllBytesAsync(PathNormalizer.Normalize(path), ct).ConfigureAwait(false);
   }

   private static string ReadDbcText(byte[] bytes)
   {
       // BOM detection + UTF-8 with Latin-1 fallback (existing logic,
       // unchanged from pre-v1.6.6 read-and-decode-in-one helper).
   }
   ```

4. **`AppHostBuilder.cs`** — DI factory wiring:
   ```csharp
   builder.Services.AddSingleton(sp =>
   {
       var config = sp.GetRequiredService<IConfiguration>().GetSection("Dbc");
       return new DbcOptions(
           MaxFileSizeBytes: config.GetValue<long>("MaxFileSizeBytes"),
           MaxMessageCount: config.GetValue<int>("MaxMessageCount"));
   });
   builder.Services.AddSingleton<DbcService>(sp =>
       new DbcService(
           sp.GetRequiredService<ILogger<DbcService>>(),
           sp.GetRequiredService<DbcOptions>()));
   ```

5. **`appsettings.json`** — additive opt-in config:
   ```json
   {
     "Channel": { "SelectedHandle": null },
     "Send":    { "MaxFramesPerSecond": 0 },
     "Dbc":     { "MaxFileSizeBytes": 0, "MaxMessageCount": 0 }
   }
   ```

**Tests** (7 new):

Core `DbcParserTests.cs` (+2):
1. `Parse_With_MaxMessageCount_Below_Actual_Returns_ParseFailure` — 5 messages, cap 3 → reject on 4th with `ErrorCode.ParseFailure` + message contains "exceeds MaxMessageCount 3".
2. `Parse_With_MaxMessageCount_Above_Actual_Returns_Success` — 3 messages, cap 100 → all accepted.

App `DbcServiceLimitTests.cs` (NEW, 5):
3. `LoadAsync_File_Above_MaxFileSize_Fires_LoadFailed_With_ParseFailure_Message` — write DBC padded > 512 bytes, cap 512 → reject.
4. `LoadAsync_File_Below_MaxFileSize_Fires_DbcLoaded` — small DBC, cap 10 KB → success.
5. `LoadAsync_Both_Caps_Zero_Unlimited` — back-compat path verifies unlimited behavior preserved.
6. `LoadAsync_MessageCount_Exceeds_Cap_Fires_LoadFailed_With_ParseFailure` — 5 messages, cap 3 → reject via ParseFailure.
7. `LoadAsync_Low_Both_Caps_Rejects_Large_Real_Fixture` — real `E51_PT_CAN-BMS.dbc` (77 KB, 256 messages) with 10 KB / 100 caps → reject (file size and/or message count).

Test fixture migrations (none triggered): 8th sub-shape grep returns 14 fixture-migration-candidates (was 13 baseline; +1 = `DbcServiceLimitTests.WriteTempDbc` helper writing to `%TEMP%`). NOT a migration — Item 1 introduces no path/network/process restriction; the +1 is legitimate test-only usage of `%TEMP%` (the v1.6.4 `%LOCALAPPDATA%\PeakCan.Host\` allowlist doesn't apply to test fixtures).

**Limitation acknowledged**: 
- Message-count cap enforced mid-parse in `DbcParser.ParseMessage`. Per-message signal growth NOT separately capped (YAGNI — per-message signal `Length` is bounded to 64 by `DbcEncodeService` / `SignalDecoder` regardless).
- `DbcErrorCode.FileTooLarge` slot at `DbcErrorCode.cs:19` declared but intentionally unused (forward-compat for future categorical DBC errors; v1.6.6 routes cap errors through the shared `ErrorCode.ParseFailure` envelope with disambiguating message strings).

## Test counts

| Suite | v1.6.5 baseline | v1.6.6 PATCH | Delta |
|---|---|---|---|
| Core | 345 | 347 | +2 (DbcParser overload tests) |
| App | 412 | 419 | +7 (DbcServiceLimitTests new file) |
| Infra | 84 | 84 | 0 |
| **Total** | **843** | **850** | **+7** (6 SKIP unchanged → 850 + 6 SKIP) |

Confirmed via `dotnet test PeakCan.Host.slnx -c Debug` on the feature branch after fixup: **847 + 6 SKIP / 0 fail** on a clean full-suite run (race-test flakes inherited from v1.6.2/v1.6.3/v1.6.4/v1.6.5 not observed this run). 8th sub-shape (production-restriction-fixture-migration): not triggered — Item 1 is a runtime rejection policy, not a path/network/process restriction.

## Process lessons (NEW)

1. **TOCTOU-free cap pattern**: hoist the byte read out of any helper that does both read + cap-check. The cap check must use the just-read byte count, not a separate `FileInfo.Length` probe. The pre-read `FileInfo.Length` check is a "fast-fail" optimization but bypassable; the post-read `bytes.Length` check is the primary defense. Concrete: v1.6.6 refactored `ReadDbcTextAsync(string, CT)` (read + decode in one) into `ReadDbcBytesAsync` (raw read) + `ReadDbcText(byte[])` (pure decode) so `LoadAsync` could check `bytes.Length` immediately after the read. **Lesson**: any time a cap check races a file boundary (read, mmap, network read), do the check against the consumed bytes, not a separate metadata probe.

2. **Architectural pattern selection (2-of-2 confirmed)**: decorator (`v1.6.5 RateLimitedSendService`) vs in-service config-bound record (`v1.6.6 DbcOptions`) is conditional on seam geometry, not absolute. Decision rule: **1 chokepoint with clean DI seam → decorator**; **N chokepoints or chokepoint needs pre-read availability → in-service config-bound record**. Document the choice explicitly when departing from the most recent PATCH's pattern.

3. **`PathNormalizer` strictness can shadow refactors**: when refactoring a chokepoint that previously used `FileInfo.Length` (auto-resolves `..`) to use a path-validation step (here: `PathNormalizer.Normalize` rejects `..` as defense-in-depth), test fixtures that construct paths via `Path.Combine(...., '..', '..', ...)` will start failing. **Lesson**: pre-emptive grep `git grep -n "Path.Combine.*\\.\\." tests/` in the planner phase catches these fixtures.

4. **`DbcErrorCode` envelope mismatch (CS1503-cleared at RED step)**: the `Error` record's `Code` field is `ErrorCode`, not `DbcErrorCode`. Any code path that wants to emit a `DbcErrorCode` value through the existing `Error` envelope must either (a) add a new ctor overload, (b) extend `ErrorCode` to include DBC-specific values, or (c) use a string-based code in a parallel field. v1.6.6 chose (c)-adjacent: pass `ErrorCode.ParseFailure` plus disambiguating `Message`. The `DbcErrorCode.FileTooLarge` slot remains a forward-compat hook for future categorical DBC errors.

5. **Test-method name should match assertion** (NEW variant of brief-drift shape 4): when a test name references one value (`With_FileTooLarge`) but the body asserts another (`ErrorCode.ParseFailure`), the name is misleading. Rename to match the assertion; document WHY in the comment. The renaming pattern would catch this at code-reviewer pass rather than later.

## Brief-vs-source drift (continued, 9-of-9+)

| # | Brief claim | Source reality | Drift sub-shape |
|---|-------------|----------------|-----------------|
| 1 | "DBC size/token limits" → opt-in via two config keys | Phase 1 resolved → **Candidate C**: size = file bytes; token = message count (`MaxFileSizeBytes` + `MaxMessageCount`) | Abbreviated-shorthand (shape 4) → resolved |
| 2 | "Apply same decorator pattern as v1.6.5" | 2 chokepoints → in-service `DbcOptions` config record instead | Wrong-API-surface (shape 3) → architectural alternative (justified) |
| 3 | "Wire `DbcErrorCode.FileTooLarge` for size cap" | `Error` ctor (`Error.cs:8`) only accepts `ErrorCode Code` not `DbcErrorCode`. Adapted: `ErrorCode.ParseFailure` + disambiguating message string | Wrong-API-surface (shape 3) + Test name alignment (NEW) |
| 4 | "0 test fixture migration" (8th sub-shape) | Confirmed. Item 1 introduces no path/network/process restriction. The +1 fixture-migration-candidate is a test-side `%TEMP%` write that doesn't trigger production restriction. | None — verified |
| 5 | Planner's pre-read cap → reality of post-read TOCTOU-free cap | Code-reviewer caught TOCTOU between pre-read `FileInfo.Length` and `File.ReadAllBytesAsync` (HIGH #2). Refactored: hoist byte read into `ReadDbcBytesAsync`; post-read `bytes.Length` check. | (post-implementation review catch) |
| 6 | Planner spec said cap message includes `FileTooLarge` token | The string is "exceeds MaxFileSizeBytes N" not "FileTooLarge". Test renamed `With_FileTooLarge` → `With_ParseFailure_Message`. | Test/assertion alignment (NEW sub-shape 9) |

Drift caught at: Phase 2.5 brief-drift-correction (shape 4 abbreviated-shorthand resolution at Plan-time), Plan dispatch (shape 3 wrong-API-surface caught at RED step via CS1503), Pre-ship code-reviewer (HIGH #1 + HIGH #2 caught inline before merge).

## Files changed

```
 docs/release-notes-v1.6.6.md                                          (new, this file)
 src/PeakCan.Host.App/Services/DbcOptions.cs                          (new, ~50 lines)
 src/PeakCan.Host.App/Services/DbcService.cs                          (+ReadDbcBytesAsync / +ReadDbcText / +2-arg ctor / post-read cap)
 src/PeakCan.Host.Core/Dbc/DbcParser.cs                              (+3-arg Parse / +ParserState cap field / +cap check)
 src/PeakCan.Host.App/Composition/AppHostBuilder.cs                   (DbcOptions factory + DbcService 2-arg factory)
 appsettings.json                                                     (+Dbc section)
 tests/PeakCan.Host.Core.Tests/DbcParserTests.cs                      (+2 tests)
 tests/PeakCan.Host.App.Tests/Services/DbcServiceLimitTests.cs        (new, ~180 lines, 5 tests)
```

## Known follow-ups

- **v1.6.0 MINOR still deferred** (9th consecutive release notes list, was 8th; now **2 remaining items**): V8 sandbox hardening + OEM `IKeyDerivationAlgorithm` concrete. v1.6.6 PATCH = v1.6.0 MINOR 5-item decomposition PATCH 3 of 5. 3 items closed (path norm root + rate limit + DBC limits).
- **v1.6.7 PATCH candidate**: V8 sandbox hardening (architectural; may end up as its own MINOR) OR OEM `IKeyDerivationAlgorithm` concrete (requires crypto review; may end up as its own MINOR). Decision deferred.
- **Race-test full stability verification**: pre-existing flake confirmed in v1.6.6 PATCH (5-of-5+ occurrences across v1.6.2 / v1.6.3 / v1.6.4 / v1.6.5 / v1.6.6). Same `Cyclic{Dbc,}SendServiceRaceTests` 2-3 intermittent failures per full-suite run; pass in isolation. Mitigation deferred (deterministic timer stub vs `[Retry(3)]` xUnit attribute — `[Retry(3)]` explicitly DECIDED NOT to add in v1.6.1 PATCH Decision 5).
- **Code-reviewer MEDIUM deferrals**: 4 of 6 MEDIUM deferred to v1.6.7 PATCH or v1.6.x MINOR (concurrent caller test on cap concurrency, configurable unlock pattern, `int.MaxValue` vs `0` sentinel asymmetry, `DbcErrorCode.FileTooLarge` categorical-error slot wiring).
- **`DbcErrorCode.FileTooLarge` slot**: forward-compat for 8+ releases. Future PATCH that needs categorical DBC errors can wire it via (a) `Error(DbcErrorCode, string)` ctor overload, (b) `ErrorCode` enum extension including DBC values, or (c) string-based code in parallel field. Decision deferred.
- **Future rate-limit/dbc-limit extensions** (carry-over from v1.6.5): per-caller quota + `RejectedFrameCount` UI exposure + multi-config (`Replay:MaxFramesPerSecond` separate) — all explicitly deferred.
- **Config-driven allowlist** (carry-over from v1.6.4): `Path:AllowedRoots:[]` in `appsettings.json` (deferred per v1.5.0 spec line 63). When added, future DBC `NormalizeRestricted` wiring would slot in.
- **Core-safe PEAK classic-code mapping** (carry-over from v1.6.4): enables `BaudRate.FromDescriptor(descriptor, name, classicCode)` 3-arg overload. Deferred to v1.6.x MINOR.
- **Long-term Non-Goals** (since v1.4.0): DBC Value table encoding + Multiplexed signal groups UI + Replay→Trace auto-load.
- **v1.6.6 PATCH ship-new carry-overs**: 4 code-reviewer MEDIUM deferred (see MEDIUM deferrals bullet above). Plus `DbcErrorCode.FileTooLarge` slot still needs categorical-error wiring in a future PATCH.

## Ship method

```
1. git checkout -b feature/v1-6-6-patch (from main @ 907c449)    [DONE]
2. 3 task commits (RED tests 6dcb95a, GREEN impl 5fd6c08,         [DONE]
3.    review-fixup 641a557 — HIGH #1 + HIGH #2 + PathNormalizer)
   Pre-ship code-reviewer subagent: 0C/2H/6M/5L WARNING            [DONE]
4. docs/release-notes-v1.6.6.md (this file)                        [DONE]
5. git push -u origin feature/v1-6-6-patch (proxy ON)              [pending]
6. gh pr create --base main                                          [pending]
7. gh pr merge --squash --delete-branch                             [pending]
8. git fetch origin main + git reset --hard origin/main             [pending]
9. git tag v1.6.6 + git push origin v1.6.6                          [pending]
10. gh release create v1.6.6 --notes-file docs/release-notes-v1.6.6.md  [pending]
11. Update MEMORY.md + write peakcan-host-v1-6-6-shipped.md     [pending]
```

## Open Questions

- None. PATCH scope is closed; single item ships as v1.6.6. 2 HIGH findings explicitly addressed in review-fixup commit. 4 of 6 MEDIUM accepted as known follow-ups.
