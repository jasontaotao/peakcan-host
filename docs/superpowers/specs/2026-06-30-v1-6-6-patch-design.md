# v1.6.6 PATCH — DBC size + message-count limits (opt-in, in-service DbcOptions)

**Date:** 2026-06-30
**Branch:** `feature/v1-6-6-patch` (cut from main @ v1.6.5 squash `dcf2cac` after `git reset --hard origin/main` to align)
**Target version:** v1.6.6 (PATCH)
**Spec owner:** main agent
**Reviewer:** code-reviewer subagent (pre-ship)
**Pre-flight:** Phase 2.5 actual code exploration (DbcService + DbcParser + DbcTokenizer + DbcErrorCode + 4 caller paths + AppHostBuilder + appsettings.json + DbcParserTests + DbcServiceTests + E51_PT_CAN-BMS.dbc fixture + test fixture grep)

## 概述

v1.6.6 PATCH is a **1-item PATCH** (v1.6.0 MINOR 5-item decomposition, PATCH 3 of 5), closes v1.6.5 PATCH release notes "Known follow-ups" line ("v1.6.6 PATCH candidate: DBC size/token limits"). **v1.6.0 MINOR remaining 2 items stay deferred** (V8 sandbox hardening + OEM `IKeyDerivationAlgorithm` concrete).

| # | Item | User-facing | Severity |
|---|------|-------------|----------|
| 1 | **`DbcOptions` config record + size pre-read + message-count mid-parse caps** — `Dbc: { MaxFileSizeBytes: 0, MaxMessageCount: 0 }` config keys (both 0/unlimited by default → opt-in). `DbcService.LoadAsync` checks `new FileInfo(Normalize(path)).Length` against `MaxFileSizeBytes` **before** `File.ReadAllBytesAsync`. `DbcParser.Parse` accepts a NEW `maxMessageCount` ctor arg; inside `ParseMessage`, increments a counter; throws `DbcParseException` (caught at `LoadAsync` and converted to `Result.Fail(ErrorCode.FileTooLarge, ...)`) if exceeded. Rejected loads surface via existing `DbcViewModel.Status` `"FAIL: {code} {msg}"` pattern. **Both caps independent** — operator can set either, both, or neither. | Yes (status string surfaces limit rejection) | MEDIUM |

### memory vs spec scope reconciliation

memory `MEMORY.md` "v1.6.0 MINOR still deferred" lists 5 items; item 3 is "DBC size/token limits". Phase 2.5 retitles and resizes:

- **"DBC size/token limits"** strictly means **two opt-in caps**: (a) `MaxFileSizeBytes` (file-level, pre-read) and (b) `MaxMessageCount` (parse-level, per-message). NOT a single DBC "size" or "token" count. The "token" word in carry-over text is informal — actual limit is message count (not signal count or character count).
- **"All 4 callers need limit"** is correct — `DbcService.LoadAsync` is the chokepoint; all 4 callers funnel through it (DbcViewModel.OpenAsync, DbcSendViewModel.OnLoaded, Scripting/DbcApi.Load, future appsettings path). Single chokepoint covers all 4.
- **YAGNI guard**: skip per-message signal cap, skip per-`Signal.Length` cap, skip per-message `DLC` cap — these are already bounded indirectly by `MaxFileSizeBytes` (a 10 MB DBC cannot have a 64-byte × 10M-signal message).
- **YAGNI guard 2**: skip `appsettings.json`-level allowlist of trusted DBC paths. Path security is a separate concern (already partly covered by `PathNormalizer.Normalize` per v1.5.0; root restriction per v1.6.4 PATCH). DBC size/message limits are independent.

## 起源

| Source | Item | Severity |
|--------|------|----------|
| v1.6.5 PATCH release notes "Known follow-ups" | "v1.6.6 PATCH candidate: DBC size/token limits" | MEDIUM |
| v1.6.0 MINOR security/limits audit (2026-05-25 joint review, never shipped) | DBC size + message-count limits as item 3 of 5-item decomposition | MEDIUM |
| `DbcTokenizer.cs:28` | `DefaultMaxLine = 1_000_000` — existing line-count cap (independent of new caps; KEEP) | (precedent) |
| `DbcService.cs:79` | `LoadAsync` is the chokepoint for 4 callers | (enables architecture) |
| `DbcErrorCode.cs:19` | `FileTooLarge` enum value declared but unused (forward-compat slot) | (enables Decision 3) |

### Brief drift history (12-of-12+ 记录)

- **"DBC size limit"** strictly means raw byte length of the file, NOT parsed-message byte length, NOT DBC character count.
- **"DBC token limit"** (carry-over text) strictly means `Message` count (top-level `BO_` blocks), NOT signal count, NOT DBC keyword token count. YAGNI on signal cap.
- **"Apply to all 4 callers"** strictly means **single check inside `DbcService.LoadAsync`**; no caller-side changes. 0 changes to `DbcViewModel`, `DbcSendViewModel`, `Scripting/DbcApi`, future `appsettings.json` path.
- **"Default policy"** strictly means **opt-in via config** (`Dbc:MaxFileSizeBytes: 0` and `Dbc:MaxMessageCount: 0` = unlimited), NOT hard-coded 10 MB / 1000 messages.
- **"`DbcErrorCode.FileTooLarge`"** is currently a **forward-compat slot** — declared in the enum but NEVER emitted. Parser failures flow through `Result.Fail(ErrorCode.ParseFailure)` exclusively (`DbcParser.cs:43, 47, 90, 120, etc.`). v1.6.6 PATCH **wires `FileTooLarge` for the size cap** (cheap, distinct semantic, no new enum value); **reuses `ParseFailure` for the message-count cap** (mid-parse, same surface as other parse errors). Decision 3 details.
- **Architecture**: NOT decorator (breaks v1.6.5 PATCH pattern precedent). **In-service** check via `DbcOptions` record injected at DI seam. **Rationale**: 2 chokepoints (size pre-read + message count mid-parse) → decorator would require 2 stacked wrappers (`SizeLimitDbcService → MessageCountLimitDbcService → DbcService`), ugly. In-service is architecturally cleaner. **Document this deviation explicitly.**
- **`DbcService` is `partial class` (not sealed)**: any future subclass (test fakes) must declare `partial` per v1.6.5 PATCH lesson.
- **DbcParser is `static class`** — cap must be passed via ctor arg on a NEW Parse overload; cannot mutate static class state.

### Phase 2.5 actual code exploration findings

| Assumption | Phase 2.5 actual |
|---|---|
| `DbcService.LoadAsync` is the chokepoint | Confirmed `DbcService.cs:79`. `var bytes = await File.ReadAllBytesAsync(PathNormalizer.Normalize(path), ct).ConfigureAwait(false);` at line 150. ALL of the 4 callers funnel through here. |
| `DbcService` is `partial class` not sealed | Confirmed `DbcService.cs:34`: `public partial class DbcService`. `LoadAsync` is `virtual` (line 79). Subclass approach viable. |
| `DbcParser` is `static class` | Confirmed `DbcParser.cs:24`: `public static class DbcParser`. Cap must be passed via overload arg. |
| `DbcTokenizer.DefaultMaxLine = 1_000_000` is existing cap | Confirmed `DbcTokenizer.cs:28`. **KEEP AS-IS** — independent of new caps. Tokenizer rejects DBC with > 1M lines; this is a parser-level cap. |
| `DbcErrorCode.FileTooLarge` exists | Confirmed `DbcErrorCode.cs:19`. Currently a forward-compat slot — never emitted. Parser uses `ErrorCode.ParseFailure` exclusively (`DbcParser.cs:43, 47, 90, 120, ...`). v1.6.6 PATCH **wires `FileTooLarge` to size cap rejection**. |
| `DbcViewModel.Status` displays FAIL info | Confirmed `DbcViewModel.cs:202-204` (mirror `SendViewModel.cs:202-204`). Existing `"FAIL: {code} {msg}"` pattern auto-displays `FileTooLarge` reject. NO VM change. |
| `DbcService.LoadFailed` event surface | Confirmed `DbcService.cs:57`: `event Action<Error>? LoadFailed;` — `Error` carries `Code` + `Message`. `DbcViewModel.LoadFailed` handler (line 114) updates Status. |
| 4 callers funnelling through `DbcService.LoadAsync` | `DbcViewModel.cs:101-110 OpenAsync`, `DbcSendViewModel.cs:142 OnLoaded`, `Scripting/DbcApi.cs:49 Load`, future `appsettings.json` path. All 4 OK with single chokepoint check. |
| `appsettings.json` currently has `Channel:SelectedHandle` + `Send:MaxFramesPerSecond: 0` | Confirmed 8-line file. Adding `Dbc: { MaxFileSizeBytes: 0, MaxMessageCount: 0 }` is additive. |
| `IConfiguration.GetValue<long>("Dbc:MaxFileSizeBytes")` pattern | Precedent: `AppShellViewModel.cs:211, 262` for `IConfiguration["Channel:SelectedHandle"]`. Use same pattern. |
| NetArchTest rule 2 (Core no PEAK SDK) | `DbcService` lives in App layer (`src\PeakCan.Host.App\Services\DbcService.cs`); `DbcParser` is in Core (`src\PeakCan.Host.Core\Dbc\`). Limits live in App DI factory + `DbcService.LoadAsync`; the message-count cap threads into `DbcParser` via ctor arg — pure Core, no PEAK SDK. |
| Inline-string DbcParserTests pattern | Confirmed `DbcParserTests.cs:24-32`: C# raw string literal `"""..."""`. For tiny fixtures. |
| Temp-file DbcServiceTests pattern | Confirmed `DbcServiceTests.cs:65`: `Path.Combine(Path.GetTempPath(), $"peakcan-dbc-test-{Guid.NewGuid():N}.dbc")` + finally cleanup. |
| Real fixture E51_PT_CAN-BMS.dbc | Confirmed `E51PTCANBMSDbcFixtureTests.cs:21`: Vector-generated 77 KB DBC with 256 messages / 820 signals. **"Still loads" smoke test** under token cap (must NOT exceed cap if cap is set low). |
| `FakeDbcService : DbcService` at AppShellViewModelTests | Confirmed `AppShellViewModelTests.cs:54-58`. DbcService is NOT sealed; subclass approach viable. |
| `DbcService.cs:31` class doc confirms `virtual LoadAsync` for test stubs | Confirmed. New subclass (test `FakeDbcService` variant) MUST declare `partial` per v1.6.5 PATCH lesson. |
| Test fixture grep (8th sub-shape) | `git grep -n "Path.GetTempPath\|Path.Combine.*Temp\|Guid.NewGuid" tests/` returns 13 files. Item 1 does NOT introduce path/network/process restriction, so expected 0 fixture migration. PR plan Task 0 must verify. |

### Brief drift cautions (memory `phase-2-5-brief-drift-correction` 12-of-12+)

1. "DBC size limit" strictly means raw byte length; not character count, not parsed-message byte length.
2. "DBC token limit" strictly means `Message` count; not signal count, not DBC keyword token count.
3. "Apply to all 4 callers" strictly means single chokepoint check inside `DbcService.LoadAsync`; 0 caller-side changes.
4. "Default policy" strictly means opt-in via config; hard-coded 10 MB / 1000 messages is product decision deferred.
5. `DbcErrorCode.FileTooLarge` is currently a forward-compat slot — v1.6.6 PATCH wires it for size cap; reuses `ParseFailure` for message-count cap.
6. Architecture is in-service, NOT decorator (breaks v1.6.5 PATCH pattern). 2-chokepoint geometry justifies deviation.
7. `DbcService` is `partial class` (not sealed): new test subclass must declare `partial`.
8. `DbcParser` is `static class`: cap must be passed via new Parse overload arg, not via ctor on a non-existent instance.
9. PATCH discipline: only Item 1 in-service check + DI factory + config schema + ~7 tests. No other production code changes.
10. Release notes template: mirror v1.6.5 / v1.6.4 format.
11. Test fixture migration (8th sub-shape): Item 1 expected 0 fixture migration. PR plan Task 0 grep verifies.

## Scope

| # | Item | 组件 | 工作 | Source | Severity |
|---|------|------|------|--------|----------|
| 1 | **`DbcOptions` config record + size pre-read + message-count mid-parse caps + opt-in `appsettings.json` schema** | App: `Services/DbcOptions.cs` (NEW — `internal sealed record DbcOptions(long MaxFileSizeBytes, int MaxMessageCount)` + `DbcOptions.Unlimited` static factory) / App: `Services/DbcService.cs` (MODIFY `LoadAsync` — add `DbcOptions` ctor dep, pre-read `FileInfo.Length` check before `File.ReadAllBytesAsync`, thread `MaxMessageCount` into `DbcParser.Parse(...)` call) / Core: `Dbc/DbcParser.cs` (NEW overload `Parse(string text, int maxMessageCount, CancellationToken ct)` — tracks seen messages, throws `DbcParseException` if exceeded; original 2-arg overload delegates with `int.MaxValue` for back-compat) / App: `Composition/AppHostBuilder.cs` (MODIFY `AddSingleton<DbcService>()` line 180 area — add `DbcOptions` factory binding from `IConfiguration["Dbc"]` section, pass into `DbcService` ctor) / App: `appsettings.json` (MODIFY: add `"Dbc": { "MaxFileSizeBytes": 0, "MaxMessageCount": 0 }`) / App: `PeakCan.Host.App.csproj` (VERIFY: `<InternalsVisibleTo>` present for `DbcOptions` test exposure) / Tests: `App.Tests/Services/DbcServiceLimitTests.cs` (NEW — 5 tests covering size reject, message-count reject, opt-out=0 both, opt-in low, opt-in high smoke) + `Core.Tests/DbcParserTests.cs` (MODIFY: add 2 tests for new Parse overload) | in-service check + DI factory + config binding + tests | v1.6.5 release notes + v1.6.0 MINOR security/limits audit | MEDIUM |

## Non-Goals

- **v1.6.0 MINOR remaining 2 items**: V8 sandbox hardening + OEM `IKeyDerivationAlgorithm` concrete — explicitly deferred, not in v1.6.6 PATCH.
- **Per-message signal cap**: YAGNI. Bounded indirectly by `MaxFileSizeBytes`.
- **Per-message DLC cap**: YAGNI. DBC DLC ≤ 64 already enforced by `ParseByte` in `DbcParser.cs:236` (line 627-640).
- **appsettings.json-level allowlist of trusted DBC paths**: separate concern (path security). v1.6.4 PATCH added `PathNormalizer.NormalizeRestricted`; DBC size/message limits are independent.
- **`DbcErrorCode` enum changes**: keep current 9 values. `FileTooLarge` is wired (Decision 3). No new enum value.
- **Hard-coded default caps**: opt-in via config only. v1.5.0 spec line 63 defer-to-Product pattern.
- **Decorator pattern** (which v1.6.5 PATCH used for rate-limit): rejected due to 2-chokepoint geometry (Decision 1).
- **Test fakes (FakeDbcService variants)**: optional; not required by Item 1. Production `DbcService` ctor accepts `DbcOptions`; tests can pass `DbcOptions.Unlimited` for default behavior.
- **Mid-parse streaming limit**: NOT streamed — full file already in memory after `File.ReadAllBytesAsync` line 150. Size cap happens BEFORE read.
- **Pre-existing 13 temp-file test fixtures**: 0 migration. Item 1 introduces no path/network/process restriction.
- **Negative-size/negative-message config validation**: `GetValue<long>("Dbc:MaxFileSizeBytes")` returns 0 for missing key. Negative values silently treated as unlimited (matches v1.6.5 PATCH `Send:MaxFramesPerSecond` semantic).

## 设计决策 (open / proposed)

### Decision 1: Architecture — in-service check vs decorator

**选项 A (adopt)**: In-service check via `DbcOptions` record injected at DI seam. `DbcService` ctor accepts `DbcOptions opts`; `LoadAsync` body checks `opts.MaxFileSizeBytes` against `new FileInfo(Normalize(path)).Length` BEFORE `File.ReadAllBytesAsync`; passes `opts.MaxMessageCount` into NEW `DbcParser.Parse(text, maxMessageCount, ct)` overload.

**选项 B (rejected)**: Decorator pattern (`SizeLimitDbcService : DbcService` → `MessageCountLimitDbcService : DbcService` → `DbcService` raw). v1.6.5 PATCH precedent.
- 2 stacked wrappers required (one per cap).
- Each wrapper must forward `LoadAsync` to the inner; no public surface to inject cap (would need ctor dep on wrapper).
- DI registration: register `DbcService` as 2-deep factory chain. 3 type registrations (`DbcService` raw + 2 wrappers) for a single conceptual feature.
- Class doc on `DbcService.cs:25-31` explicitly says class is `partial` + `virtual LoadAsync` so tests can swap in stubs; decorator would also need to be `partial` (per v1.6.5 lesson). 3 partial classes to maintain.
- **Reject**: 2-chokepoint geometry does not fit decorator's single-wrap seam. In-service is cleaner.

**选项 C (rejected)**: Extension method on `DbcService` (`withSizeLimit(opts)` returns wrapper). Same issues as B.

**决策**: A. In-service `DbcOptions` + `DbcService` ctor dep. Breaks v1.6.5 PATCH decorator precedent; justified by 2-chokepoint geometry. **Document deviation explicitly in spec scope section + commit body.**

### Decision 2: Size cap chokepoint — pre-read vs post-read vs stream

**选项 A (adopt)**: Pre-read check via `new FileInfo(Normalize(path)).Length` BEFORE `File.ReadAllBytesAsync`. Rationale: (a) cheapest path; (b) avoids allocating `byte[]` for a 2 GB malicious DBC; (c) `FileInfo.Length` is a single stat call (no I/O wait for stat on NTFS). O(1).

**选项 B**: Post-read check via `bytes.Length` after `File.ReadAllBytesAsync`. Issues: (a) the 2 GB DBC is already in memory; (b) GC pressure on a rejected file.

**选项 C**: Stream-read with `Stream.Length` check during read. Issues: (a) requires refactor of `ReadDbcTextAsync` helper; (b) mid-stream check is awkward; (c) no benefit over (a).

**决策**: A. Cheapest; mirrors `FileInfo.Length` best practice.

### Decision 3: Error code — `FileTooLarge` vs `ParseFailure` vs new value

**Current state**: `DbcErrorCode` is **declared but unused** (`DbcErrorCode.cs:5-7` XML doc: "Currently unused by the parser — parser failures flow through the shared `Result<T>` with `ErrorCode.ParseFailure`. Kept for forward-compatibility when sub-errors need finer classification."). All parser failures flow through `Result.Fail(ErrorCode.ParseFailure, ...)`. The `DbcErrorCode` enum's `FileTooLarge` slot is a forward-compat placeholder.

**选项 A (adopt)**: Wire `DbcErrorCode.FileTooLarge` for size cap; reuse `ErrorCode.ParseFailure` for message-count cap.
- Size cap: pre-read check fires BEFORE parser is invoked. Convert to `Result.Fail(DbcErrorCode.FileTooLarge, $"file size {N} bytes exceeds MaxFileSizeBytes {M}")`.
- Message-count cap: mid-parse throw of `DbcParseException` is wrapped by existing `catch (DbcParseException ex)` in `DbcParser.Parse` → `Result.Fail(ErrorCode.ParseFailure, ex.Message)`. **Reuses existing parse-failure path.** The error MESSAGE includes the limit reason (operator can read it).
- Rationale: `FileTooLarge` slot is forward-compat and the size cap is the only "too large" semantic. Message-count is a different concern (parse-time vs file-level).
- **Cost**: 0 new enum values. `DbcErrorCode.FileTooLarge` slot is wired.

**选项 B**: Reuse `ErrorCode.ParseFailure` for both. YAGNI on `FileTooLarge` wiring. Issues: (a) loses the "size limit" semantic distinction in the error code (message still has reason); (b) leaves the forward-compat slot unwired for another cycle.

**选项 C**: Add new `DbcErrorCode.TooManyMessages`. YAGNI. `ParseFailure` mid-parse is sufficient; operator can read the message.

**决策**: A. Wire `DbcErrorCode.FileTooLarge` for size cap; reuse `ErrorCode.ParseFailure` for message-count cap.

### Decision 4: Message-count cap — mid-parse throw vs post-parse check

**选项 A (adopt)**: Mid-parse throw inside `ParseMessage` (after a successful `Result<Message>` build, before adding to `_pendingMessages`). Throws `DbcParseException($"message count {N} exceeds MaxMessageCount {M} at line {line}")`. Caught by existing `catch (DbcParseException ex)` in `DbcParser.Parse` → `Result.Fail(ErrorCode.ParseFailure, ex.Message)`.

**选项 B (rejected)**: Post-parse check in `DbcService.LoadAsync` after parse completes. Issues: (a) parse already OOM'd on a malicious DBC with 10M messages; (b) no fail-fast; (c) defeats the purpose of a cap.

**选项 C (rejected)**: Streaming limit on `DbcTokenizer`. `DbcTokenizer` is per-line; would need to count message-start tokens specifically. Complex; out of scope.

**决策**: A. Mid-parse throw; existing `DbcParseException` wrap path; reuses `ParseFailure` semantic.

### Decision 5: New `DbcParser.Parse` overload signature

**选项 A (adopt)**: NEW overload `public static Result<DbcDocument> Parse(string text, int maxMessageCount, CancellationToken ct = default)`. Original 2-arg `Parse(string text, CancellationToken ct = default)` delegates to new overload with `maxMessageCount: int.MaxValue` (preserves back-compat — no existing caller behavior change).

**选项 B (rejected)**: Mutate static state on `DbcParser`. Breaks `DbcParser.cs:21-22` class doc "pure static API, safe to call concurrently".

**选项 C (rejected)**: Make `DbcParser` non-static. Larger refactor; breaks the "pure Core, no I/O" pattern.

**决策**: A. New overload. Back-compat preserved.

### Decision 6: `DbcOptions` shape

**选项 A (adopt)**: `internal sealed record DbcOptions(long MaxFileSizeBytes, int MaxMessageCount)` with `public static DbcOptions Unlimited { get; } = new(0, 0);`. Two fields; 0/unlimited sentinel.

**选项 B (rejected)**: Two separate ctor args on `DbcService`. Less cohesive; harder to extend (e.g., future `MaxSignalCount`).

**选项 C (rejected)**: Nested class inside `DbcService`. Namespace pollution.

**决策**: A. Record + Unlimited static factory. ~15 lines.

### Decision 7: DI registration shape

**选项 A (adopt)**: