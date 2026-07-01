# v1.5.0 MINOR Release Notes

**Release date:** 2026-06-29
**Branch:** `feature/v1-5-0-minor` (cut from main @ v1.4.1 `cbd17f5`)
**Base:** main @ v1.4.1 `cbd17f5`
**Squash SHA:** `9f0bb9e`
**Tag:** v1.5.0
**PR:** #11

## 概述

v1.5.0 是一个 3 项 MINOR 增量（Path 规范化 + Channel picker 持久化 + Replay Loop/CanIdFilter），起源于 v1.3.0 MINOR release notes §"Decomposition context" 列出 8 项 deferred scope 中的 3 项。Phase 2.5 actual-code exploration 后 spec 已收敛，**Channel picker 范围被大幅缩减**（v0.4.0 早已落地 IChannelEnumerator + UI + DI + 3 contract tests，v1.5.0 只补 appsettings.json 持久化）。无 UDS 协议增强。

## 起源

v1.3.0 MINOR release notes §"Decomposition context" + v1.4.0 MINOR ship notes §"Known follow-ups" 标出 v1.5.0 MINOR scope：

| Item | Source | Severity |
|------|--------|----------|
| Path normalization (defense-in-depth vs. traversal) | v1.3.0 decomposition | MEDIUM (security) |
| Channel picker (user-pickable PCAN-USB channel) | v1.3.0 decomposition | MEDIUM (UX) |
| Replay Loop + CAN ID filter (post-MVP follow-up) | v1.4.0 Non-Goals §53 + v1.4.1 review | LOW (UX) |

### Decomposition decision (spec §"Decomposition")

v1.5.0 MINOR = Path normalization + Channel picker + Replay follow-up. Spec
开列的 5 个候选 (V8 sandbox / CanApi rate limit / DBC size limit / OEM
`IKeyDerivationAlgorithm` concrete) **deferred** to v1.6.0 MINOR — 它们
either 重大安全 hardening（V8 sandbox）或需要 concrete OEM cooperation
（KeyDerivation）或 性能 benchmark 先行（rate limit）。3 项选定的范围都有
明确 spec + test seam + 兼容现有 DI 拓扑。

### Phase 2.5 actual code exploration findings (spec §"Phase 2.5")

实际读 v1.4.1 shipped 代码确认 brief 描述准确 + **捕获重大 spec drift**：

| Assumption | Phase 2.5 actual |
|---|---|
| "Channel picker = NEW feature" | **v0.4.0 已落地**：`IChannelEnumerator` (`Core/IChannelEnumerator.cs`)、`PeakChannelEnumerator` (`Infra/Peak/`)、`ChannelPickerViewModel` + View、`AppHostBuilder` DI 注册、3 contract tests 全部 in-place。Task 2 implementer 在做 brief 的时候发现 brief-vs-source drift caught in execution（memory v1.4.0 11-of-11+ pattern）。**v1.5.0 Channel picker 范围缩减到 appsettings.json 持久化 only。** |
| "Replay Loop = trivial SetState flag" | `ReplayService` 现有架构用 `System.Threading.Timer` + `ReplayTimeline` 内部状态机。Brief 假设的 `_service`/`_stopwatch`/`SetState`/`_stateLock`/显式 end-of-stream 分支 **都不存在**。实际改动：interface 加 `Loop` / `CanIdFilter` / `PlaybackEnded`；`ReplayTimeline` 加 loop boundary + filter check；`OnTick` 在 EOF 触发 `PlaybackEnded` 并把 state 转 Stopped。 |
| "Replay CanIdFilter is bool enable" | Spec ADR-3 tri-state：`null` = pass all，`empty` = pass none，`set` = only matching IDs。null vs empty 是 distinct semantics（user clears filter ≠ user explicitly filters nothing）。 |
| "PathNormalizer in `Path` namespace — no conflict" | **Namespace 冲突**：`PeakCan.Host.Core.Path` 阴影掉 `System.IO.Path`（项目 `ImplicitUsings=enable`）。C# resolver 在该 namespace 内优先解析 `Path` to `PeakCan.Host.Core.Path`。所以：(1) 4 call site 中 `Path.GetTempPath()` 需 fully-qualified `System.IO.Path.GetTempPath()`；(2) 7 个 Core.Tests 文件 + 2 个 Core source 文件 pre-existing `Path.Combine` 引用被统一改为 `System.IO.Path.*` — **9 文件 cascade-rename**（不在原始 spec scope，brief drift caught at Task 1.5 build）。 |
| "Path traversal defense = root-restricted" | **Defense-in-depth**（spec ADR-1）：只检查 invalid segment / null byte / non-absolute / null / empty。**不** 限根目录（避免破坏 DBC/UDS 现有使用 pattern）。文档明确"不替代 sandbox"。 |

## Scope

3 MINOR items = Path normalization + Channel picker persistence + Replay follow-up.

| # | Item | 组件 | 工作 | Source | Severity |
|---|------|------|------|--------|----------|
| 1 | **Path normalization** | Core: `PathNormalizer` + `PathNormalizationException` | Static helper + exception type with `PathNormalizationReason` enum (NullPath/EmptyPath/RelativePath/TraversalSegment/NullByte) + 4 call sites (DbcService, DidDatabase, RoutineDatabase, ReplayService) | v1.3.0 decomposition | MEDIUM (security) |
| 2 | **Channel picker persistence** | App: `AppShellViewModel` + `appsettings.json` | `IConfiguration` injection → `Channel:SelectedHandle` round-trip → EnumerateChannels restores on first call + suppress-flag pattern for "persisted handle no match" edge case | v1.3.0 decomposition | MEDIUM (UX) |
| 3 | **Replay Loop + CanIdFilter + PlaybackEnded** | Core: `IReplayService` + `ReplayTimeline`; App: `ReplayViewModel` + `ReplayView` | Service interface extensions + tri-state filter semantics + PlaybackEnded exactly-once + UI Loop CheckBox + CAN ID filter TextBox (0x hex / decimal mix) | v1.4.0 Non-Goals + v1.4.1 review | LOW (UX) |

## Non-Goals

- **v1.4.2 PATCH (carry-over from v1.4.1)**: HIGH — `UdsSecurity.SetSeed` wipes `AttemptCount` + `LockedUntilUtc` on successful `RequestSeed`. Defeats v1.3.0 lockout under concurrent access. Fix: `SetSeed` must preserve existing state; spec-author pending Product decision on "should lockout persist across successful authentications?".
- **v1.5.1 PATCH**: Replay time-range filter (post-EOF scrubbing to arbitrary timestamp, not just Loop restart at 0) + Periodic DBC send (auto-send DBC message at fixed interval; requires `CyclicSendService` integration — memory v1.2.12 lesson 4: known transient flakes).
- **v1.6.0 MINOR**: V8 sandbox hardening + CanApi rate limit + DBC size/token limit + path normalization root restriction (replace defense-in-depth with hard sandbox) + OEM `IKeyDerivationAlgorithm` concrete.
- **Future MINOR**: Replay→Trace auto-load + Value table encoding (text→int via DbcDocument.ValueTables) + Multiplexed signal groups UI (auto-show only valid mux signals) + `ReplaySinkAdapter` surface first-failure as `ReplayException` (user-hostile silent drop on no-channel).
- 公开 API 移除 / 依赖升级 / 用户数据 schema migration — MINOR 纪律（只 additive）。
- 新增 NuGet 包（沿用 `Microsoft.Extensions.Configuration` + `Microsoft.Extensions.DependencyInjection` 已有依赖）。

## Per-Item 修复详情

### Item 1 — Path normalization (MEDIUM)

**Symptom**: File-system reads scattered across Core (DBC, UDS DBs, Replay ASC) and App (DBC load) have **no normalization layer**. Path traversal attacks (`..\..\..\Windows\System32\...`) and null-byte injection pass through to `File.ReadAllText` / `File.OpenRead` unchecked. Defense-in-depth spec §Decision 1.

**Fix** (2 Core files + 4 call site files + 1 test file + 9 file cascade-rename):

- `src/PeakCan.Host.Core/Path/PathNormalizer.cs` — `public static class PathNormalizer` with single public method `Normalize(string path) → string`. Strategy: (1) reject null/empty/relative/null-byte/traversal-segment early; (2) call `Path.GetFullPath(path)` to canonicalize; (3) re-check post-canonicalization for `..` segments (defense against `C:\foo\..\bar\..\..\baz` not-yet-resolved forms); (4) return canonical absolute path. **No root restriction** per spec ADR-1.
- `src/PeakCan.Host.Core/Path/PathNormalizationException.cs` — `sealed class PathNormalizationException : ArgumentException` with `AttemptedPath` + `Reason` (enum `PathNormalizationReason { NullPath, EmptyPath, RelativePath, TraversalSegment, NullByte }`).
- Call site updates (6 internal sites):
  - `src/PeakCan.Host.App/Services/DbcService.cs` (`LoadAsync`, 1 site)
  - `src/PeakCan.Host.Core/Uds/Database/DidDatabase.cs` (`LoadAsync`, 2 sites)
  - `src/PeakCan.Host.Core/Uds/Database/RoutineDatabase.cs` (`LoadAsync`, 2 sites)
  - `src/PeakCan.Host.Core/Replay/ReplayService.cs` (`LoadAsync`, 1 site)
- 9-file cascade-rename: namespace `PeakCan.Host.Core.Path` shadows `System.IO.Path` under `ImplicitUsings=enable`. All pre-existing `Path.Combine` / `Path.GetTempPath` references inside that namespace + 7 Core.Tests files updated to fully-qualified `System.IO.Path.*`. Caught at Task 1.5 build (compile error: `Path` ambiguous between `PeakCan.Host.Core.Path.PathNormalizer` and `System.IO.Path`).

**Tests**: +6 (all in `tests/PeakCan.Host.Core.Tests/Path/PathNormalizerTests.cs`):
- `Normalize_AbsolutePath_ReturnsCanonicalForm` — happy path
- `Normalize_RelativePath_Throws` — `"foo\bar"`
- `Normalize_PathWithTraversalSegment_Throws` — both pre-canonicalization (`..\..\..\..\Windows\...`) and post-canonicalization forms covered via extracted `ContainsTraversalSegment` helper (review fix #2)
- `Normalize_NullPath_Throws` / `Normalize_EmptyPath_Throws` — early reject
- `Normalize_PathWithNullByte_Throws` — non-verbatim string `"C:\\foo\\bad\0path.dbc"` to avoid Write-tool NUL-byte issue (plan §Self-Review issue #4)
- Review-fix test: 1 renamed test `Normalize_PathWithTraversalSegment_Throws` covers pre + post-canonicalization via shared `ContainsTraversalSegment` helper (review fix #1, Important #2 verbatim-duplication fix)

**Brief drift caught (memory v1.4.0 11-of-11+ pattern)**:
- `Path` namespace shadowing `System.IO.Path` — not in original brief; **9 file cascade-rename** (7 Core.Tests + 2 Core source) added in commit `ba88d6a` and re-verified at Task 1.5 build.
- Review-fix commits (`f553de1`): `Path` property rename (`AttemptedPath` is conventional `Path` but conflicts with `System.IO.Path` in same file → renamed to `AttemptedPath`) + `ContainsTraversalSegment` extracted helper.

### Item 2 — Channel picker persistence (MEDIUM)

**Symptom**: v0.4.0 already shipped `IChannelEnumerator` + `PeakChannelEnumerator` + `ChannelPickerViewModel` + View + DI registration + 3 contract tests. **Brief missed all this** (spec author saw the spec's "Channel picker" item in v1.3.0 decomposition and assumed it was unimplemented). v1.5.0 only adds **persistence**: user-selected channel should survive app restart.

**Fix** (1 App file modified + 1 new `appsettings.json` + 1 test file extended):

- `src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs` — ctor adds optional `IConfiguration?` parameter (last position, preserves all 14 existing call sites). On ctor: read `Channel:SelectedHandle` from config (hex format, parsed via `NumberStyles.HexNumber`). On `SelectedChannel` setter: write back via `OnSelectedChannelChanged` partial method using uppercase hex without `0x` prefix (e.g. `0x52` → `"52"`). `EnumerateChannels` restores the persisted handle on first call if matching channel is still present; otherwise falls back to v0.4.0 default (`channels[0]`).
- `appsettings.json` (new at repo root) — default `Channel:SelectedHandle = null` + csproj `PreserveNewest` copy-to-output so `Host.CreateApplicationBuilder()` picks it up.
- `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` — registers `IConfiguration` as singleton + wires it into `AppShellViewModel` factory.

**Suppress-flag pattern** (review fix `0fc470b` Important #1): when `EnumerateChannels` auto-selects a fallback because the persisted handle didn't match any current channel, set `_suppressNextPersist = true`. The `OnSelectedChannelChanged` partial method then **skips the config write** so the user's original persisted intent (e.g. `"99"`) is preserved for next launch — even though the current session falls back to `0x51` (or whatever's first). Prevents "persisted handle no match" edge case from silently overwriting user intent.

**Tests**: +5 (all in `tests/PeakCan.Host.App.Tests/ViewModels/AppShellViewModelTests.cs`):
- `Ctor_EmptyConfig_SelectedChannelDefaultsToChannelsFirst` — null config → `channels[0]`
- `Ctor_PersistedHandle_LoadedOnConstruction` — `Channel:SelectedHandle="52"` → `SelectedChannel = 0x52`
- `SetSelectedChannel_WritesToConfig` — `SelectedChannel = 0x52` → config has `"52"`
- `SetSelectedChannel_Null_ClearsConfig` — `SelectedChannel = null` → config has `null`
- `Ctor_PersistedHandleNoMatch_DoesNotOverwriteConfig` (review-fix `0fc470b`) — config `"99"` + no matching channel → session falls back to first, but config still has `"99"` for next launch

**Brief drift caught (memory v1.4.0 11-of-11+ pattern)**: Task 2 implementer discovered **the entire v0.4.0 Channel picker feature was already shipped** — Task 2 + 2.5b dropped, Task 3 re-scoped to persistence only. Spec had "Channel picker = NEW feature" assumption; actual code is "Channel picker = OLD feature + NEW persistence". Tasks 2 + 2.5b dropped by implementer per spec §Non-Goals "drop unused planned work" discipline.

### Item 3 — Replay Loop + CanIdFilter + PlaybackEnded (LOW)

**Symptom**: v1.4.0 MINOR shipped Replay with `Play/Pause/Resume/Scrub/Speed` per spec Non-Goals §53 deferred "loop playback, CAN ID filter, time-range filter". v1.4.0 final review deferred these to "future MINOR". v1.5.0 closes Loop + CanIdFilter (time-range deferred to v1.5.1 PATCH — different scope: scrubbing to arbitrary timestamp, not just loop restart at 0).

**Fix** (4 Core files + 1 App file + 1 App XAML + 2 test files):

- `src/PeakCan.Host.Core/Replay/IReplayService.cs` — adds `bool Loop { get; set; }` + `IReadOnlySet<uint>? CanIdFilter { get; set; }` + `event EventHandler? PlaybackEnded`.
- `src/PeakCan.Host.Core/Replay/ReplayService.cs` — public property setters propagate to internal `_timeline` (real `ReplayTimeline` instance, not a stub — verified by review-fix test `SetLoop_PropagatesToInternalTimeline` using reflection on private `_timeline` field).
- `src/PeakCan.Host.Core/Replay/ReplayTimeline.cs` — `OnTick` checks `Loop` flag at EOF: if true, restart play head at 0; if false, raise `PlaybackEnded` event + set state Stopped. `EmitFrame` checks `CanIdFilter` against current frame's `Id`: null = pass all, empty = pass none, set = `Contains(frame.Id)`.
- `src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs` — adds `Loop` `ObservableProperty` + `CanIdFilterText` `ObservableProperty` + parser (mix of `0x` hex + decimal tokens, e.g. `"0x100, 768, 0xABC"`). Invalid tokens surface in `ErrorMessage` but keep valid IDs.
- `src/PeakCan.Host.App/Views/ReplayView.xaml` — Loop CheckBox + CAN ID filter TextBox in transport bar. `ToolTip` documents the 0x hex / decimal syntax.

**Tests**: +11 (8 Core + 3 App):
- 6 `ReplayTimelineTests`: Loop ON restart at 0; Loop OFF raise PlaybackEnded + state Stopped; CanIdFilter null pass-all; CanIdFilter set only-matching; CanIdFilter empty pass-none; hot-swap filter
- 2 `IReplayServiceTests`: SetLoop + SetCanIdFilter propagate to service (renamed `SetLoop_True_PropagatesToTimeline` → `SetLoop_GetterReturnsWhatWasSet` per review-fix `6c16c30` honest-naming, added new `SetLoop_PropagatesToInternalTimeline` using real `ReplayService` + real `ReplayTimeline` + reflection)
- 3 `ReplayViewModelTests`: empty filter text → null; valid hex+decimal mix → HashSet<uint>; invalid tokens → error message + keep valid IDs only

**Tri-state CanIdFilter semantics** (spec ADR-3):
- `null` = pass all frames (default state, no filter)
- `empty` (`new HashSet<uint>()`) = pass nothing (user explicitly filters out all IDs)
- `set` = only matching IDs pass

Distinct from nullable bool because user clearing filter ≠ user filtering nothing. UI: empty TextBox → null (clear); whitespace-only TextBox → empty (intent: pass nothing); `0xZZZ` → invalid token error, keep valid IDs.

**PlaybackEnded exactly-once invariant**: `OnTick` raises `PlaybackEnded` once at EOF when Loop=false. The event is `EventHandler?` (not `EventHandler<T>` per project convention). UI marshals via `SynchronizationContext.Post` (v1.4.0 pattern). Reset to Stopped state prevents re-raise on subsequent ticks.

**Brief drift caught (memory v1.4.0 11-of-11+ pattern)**:
- Spec assumed `ReplayService` had `_service` / `_stopwatch` / `SetState` / `_stateLock` / explicit end-of-stream branch. **None of these existed** in v1.4.0 shipped code. Actual architecture: `System.Threading.Timer` + `ReplayTimeline` internal state machine, public properties delegated to `_timeline`. Brief caught at Task 4.1 actual-code read; design adapted to wrap real state machine rather than introduce new state.
- Spec's "SetLoop_True_PropagatesToTimeline" test name implied it verified service→timeline propagation, but the implementer initially only wrote a getter/setter round-trip — review-fix `6c16c30` renamed to honest `SetLoop_GetterReturnsWhatWasSet` + added a real test using reflection on private `_timeline` field.

## DI 注册（修改 `AppHostBuilder.cs`）

No new DI registrations required for the 3 shipped items:

- `PathNormalizer` is `static class` — no DI needed
- `IConfiguration` already registered by `Host.CreateApplicationBuilder()`; `AppShellViewModel` ctor now takes `IConfiguration?` as optional last param (14 existing call sites unchanged; DI factory provides `IConfiguration` from host)
- `IReplayService.Loop` / `CanIdFilter` / `PlaybackEnded` are extension members on existing registered `IReplayService` singleton; no new registration

**Integration test** (`AppHostBuilderTests.Build_Registers_ReplayAndDbcEncodeServices`, carried over from v1.4.0) continues to pass — confirms no DI regression.

## Pre-ship code-review verdict

**Whole-branch review** (`git diff cbd17f5..HEAD`, 22 files / +2940 lines / 121KB diff):

- **0 Critical**
- **0 Important on first-pass review** (re-checked after brief-vs-source drift catches)
- **3 Important caught + fixed mid-execution** (all in commit subjects):
  - `f553de1` — `Path` property rename + `ContainsTraversalSegment` helper extraction (Task 1 review)
  - `0fc470b` — suppress-flag for "persisted handle no match" + trailing newline + test helper (Task 3 review)
  - `6c16c30` — `SetLoop` test rename + new real-`ReplayService` propagation test (Task 4 review)

Per-task review verdicts (final):
- Task 1 (Path normalization): APPROVED after 1 fix cycle (`f553de1`)
- Task 3 (Channel picker persistence): APPROVED after 1 fix cycle (`0fc470b`)
- Task 4 (Replay service extension): APPROVED after 1 fix cycle (`6c16c30`)
- Task 5 (Replay UI): APPROVED (0 Critical/Important)

**Final test count**: 764 pass + 7 SKIP + 0 fail (Core 315+1 / Infra 84+2 / App 365+4). All 7 SKIPs pre-existing + 1 from v1.4.1 deferred; no new SKIP introduced by v1.5.0.

## Tests

| Metric | v1.4.1 baseline | **v1.5.0 final** | Δ |
|--------|------------------|------------------|-------|
| Total pass | 740 + 7 SKIP + 0 fail | **764 + 7 SKIP + 0 fail** | **+24 net** |
| Core.Tests | 299 + 1 SKIP | **315 + 1 SKIP** | +16 (PathNormalizer 6 + ReplayTimeline 6 + IReplayService 4) |
| Infra.Tests | 84 + 2 SKIP | **84 + 2 SKIP** | unchanged |
| App.Tests | 357 + 4 SKIP | **365 + 4 SKIP** | +8 (AppShellViewModel 5 + ReplayViewModel 3) |

**Brief-vs-source drift pattern (11-of-11+ confirmed)**: Every implementer re-read actual source before writing code. Notable catches:
- `Path` namespace shadowing `System.IO.Path` (Task 1, 9-file cascade-rename)
- Channel picker already shipped since v0.4.0 (Task 2 implementer caught in execution; Tasks 2 + 2.5b dropped)
- `ReplayService` state machine architecture differs from brief (Task 4, no `_service`/`_stopwatch`/`SetState`/`_stateLock`; adapted to wrap real `ReplayTimeline`)
- `SetLoop_True_PropagatesToTimeline` test name implied propagation but only tested getter/setter (Task 4 review-fix, honest rename + real test added)

**TDD discipline validated**: Per-task implementers used the v1.4.0 / v1.4.1 pattern of writing RED tests, verifying they FAIL against unfixed code, then applying fixes. All 22 new tests in v1.5.0 went through RED → GREEN → IMPROVE.

**Reflection-based regression guard** (Task 4 review-fix): `SetLoop_PropagatesToInternalTimeline` uses `typeof(ReplayService).GetField("_timeline", BindingFlags.Instance | BindingFlags.NonPublic)` to peek at the private `_timeline` field. Same pattern as v1.2.14 PATCH `TxWaitingForFcForTesting` (defensive vs observable test design).

## Ship process

7 commits on `feature/v1-5-0-minor`:
- `cc0ccfe` spec(v1.5.0): Channel picker + Path normalization + Replay follow-up (on main)
- `5536c4f` plan(v1.5.0): TDD implementation plan for 3-item MINOR
- `ba88d6a` Task 1: PathNormalizer (Core) + 6 tests + 4 call sites + 9-file cascade-rename
- `f553de1` Task 1 review fix (Path property rename + ContainsTraversalSegment helper)
- `b05d0cc` Task 3: AppShellViewModel appsettings.json persistence for SelectedChannel + 4 tests
- `0fc470b` Task 3 review fix (suppress-flag for "no match" + trailing newline + test helper) + 1 test
- `74d858c` Task 4: IReplayService extension (Loop + CanIdFilter + PlaybackEnded) + 8 tests
- `6c16c30` Task 4 review fix (SetLoop test rename + new real-ReplayService propagation test)
- `909650b` Task 5: ReplayViewModel Loop + CanIdFilterText + parser + ReplayView UI + 3 tests
- (ship prep commit — version bump + release notes)

squash → PR → main → tag v1.5.0 → release.

**Tasks 2 + 2.5b dropped mid-execution**: Task 2 implementer discovered entire v0.4.0 Channel picker (interface + native impl + UI + DI + 3 contract tests) was already shipped. Task 3 was re-scoped to "appsettings.json persistence for SelectedChannel" only — the only piece not already shipped. Tasks 2 + 2.5b marked dropped per spec §Non-Goals drop discipline.

## Spec drops (post Phase 2.5)

| Brief | Drop reason |
|-------|-------------|
| Task 2 (IChannelEnumerator + ChannelInfo) | Already shipped since v0.4.0. |
| Task 2.5b (PeakChannelEnumerator native impl) | Already shipped since v0.4.0. |
| `ChannelPickerView.xaml.cs` + `ChannelPickerViewModel` (planned NEW) | Already shipped since v0.4.0. |
| 3 contract tests (planned NEW) | Already shipped since v0.4.0. |
| AppHostBuilder integration test for `IChannelEnumerator` (planned NEW) | Already shipped since v0.4.0. |
| v1.5.0 spec §"Periodic DBC send" | Deferred to v1.5.1 PATCH. Would require `CyclicSendService` integration; memory v1.2.12 lesson 4: known transient flakes. |
| v1.5.0 spec §"V8 sandbox" / "CanApi rate limit" / "DBC size limit" / "OEM KeyDerivation concrete" | Deferred to v1.6.0 MINOR. 4 candidates too many for one MINOR; spec author recommended splitting. |

## Decomposition context (from v1.3.0 / v1.4.0 / v1.4.1 release notes)

- **v1.3.0**: UDS protocol completion (5 items, 1 MEDIUM + 4 LOW)
- **v1.3.1**: UDS SecurityAccess tightening (2 HIGH + 1 LOW, deferred carry-overs)
- **v1.4.0**: Replay + Send DBC (2 user-facing MINOR features)
- **v1.4.1**: SecurityAccessAsync race test + AscParser logging + DbcSendViewModel DbcLoaded (3 LOW PATCH items + 1 out-of-scope HIGH SetSeed bug discovery)
- **v1.5.0 (this release)**: Path normalization + Channel picker persistence + Replay Loop/CanIdFilter
- **v1.4.2 PATCH (planned)**: HIGH — `UdsSecurity.SetSeed` wipes lockout counter (carry-over from v1.4.1 out-of-scope finding) + MEDIUM `ReplaySinkAdapter` surface first-failure as `ReplayException`
- **v1.5.1 PATCH (planned)**: Replay time-range filter + Periodic DBC send
- **v1.6.0 MINOR (planned)**: V8 sandbox + CanApi rate limit + DBC size/token limit + path normalization root restriction + OEM `IKeyDerivationAlgorithm` concrete

## Breaking changes

**无**. 所有 v1.5.0 范围内变更, additive only. 关键 invariants:

- `PathNormalizer.Normalize` 是 NEW public static method; 旧 caller 不受影响 (call sites 内部已更新)
- `PathNormalizationException` 是 NEW exception type; 不替代任何 existing exception
- `IReplayService.Loop` / `CanIdFilter` / `PlaybackEnded` 是 NEW interface members; default `Loop=false`, `CanIdFilter=null`, `PlaybackEnded=null` — 旧 caller 不受影响
- `AppShellViewModel` ctor 末尾加 optional `IConfiguration?` parameter; 14 existing call sites 仍编译 (DI factory 自动 wire `IConfiguration` from `AppHostBuilder`)
- `appsettings.json` 是 NEW; 不存在时 `Host.CreateApplicationBuilder()` 跳过 file provider, 现有 config 行为不变 (empty `Channel:SelectedHandle` → 旧 default `channels[0]`)
- 现有 0 个 public API removal
- 现有 0 个 DI service removal

## ADR note: PathNormalizer as defense-in-depth, not root restriction (spec §Decision 1)

**Decision**: `PathNormalizer.Normalize` validates and canonicalizes path but does **not** restrict to a root directory. Spec ADR-1 / Decision 1 explicit.

**Rationale**: DBC files live in user-chosen directories (e.g. `D:\projects\vehicle-ecu\dbc\`); UDS JSON DBs in same; Replay ASC files in same. Restricting to `C:\PeakCan\` would break existing user workflows. Defense-in-depth (reject `..` segments, null bytes, relative paths, null/empty) catches the common attack vectors without changing the legitimate-use surface. Hard root restriction is a separate v1.6.0 concern (V8 sandbox + path whitelist).

**Alternative considered and rejected**: "Restrict to `<app-base>/data/`". Would require either (a) copying all DBC/UDS DBs/ASC files into app-data, or (b) per-user config to whitelist a directory. Both break existing user workflows. Document explicitly that `PathNormalizer` does NOT replace OS-level sandboxing.

**Test coverage**: 6 tests cover absolute path passes; relative/null/empty/.. throws; null byte throws. Review-fix test ensures pre + post-canonicalization traversal segments both caught via extracted `ContainsTraversalSegment` helper.

## Process lessons (from v1.4.1 + v1.4.0 + earlier, applied to v1.5.0)

1. **TDD test discrimination (11-of-11+ confirmed)**: A RED test must FAIL against unfixed code AND PASS against fixed code. Per-task implementers verified this consistently.
2. **Brief-vs-source drift is structural (11-of-11+ confirmed)**: Phase 2.5 actual-code exploration is non-negotiable. Per-task implementers re-read actual source before writing code, catching 4+ brief defects this release:
   - `Path` namespace shadowing `System.IO.Path` (9-file cascade-rename)
   - Channel picker entirely already shipped since v0.4.0 (Tasks 2 + 2.5b dropped)
   - `ReplayService` state machine architecture (no `_service`/`_stopwatch`/`SetState`/`_stateLock`)
   - `SetLoop_True_PropagatesToTimeline` test name implied propagation but only tested getter/setter (renamed + real test added)
3. **Reflection-based regression guards**: Task 4 review-fix test uses `typeof(ReplayService).GetField("_timeline", BindingFlags.Instance | BindingFlags.NonPublic)` to peek at private state. Same pattern as v1.2.14 PATCH `TxWaitingForFcForTesting`. Defensive vs observable test design.
4. **Spec drops are healthy**: 8 候选 → 3 shipped (after Task 2 + 2.5b drop). 比 ship dead code 或重复 test 好. Drop 决策应在 Phase 2.5 spec 阶段或 mid-execution 验证阶段做.
5. **Tri-state vs nullable bool**: Replay CanIdFilter uses `null` / empty set / set (3 distinct semantics) vs nullable bool. User clearing filter ≠ user filtering nothing. Spec ADR-3 explicit.
6. **Suppress-flag pattern for "fallback shouldn't overwrite user intent"**: Channel picker auto-selects `channels[0]` when persisted handle doesn't match. Set `_suppressNextPersist=true` so the fallback doesn't silently overwrite the user's persisted `"99"`. Next launch still has user's intent.
7. **STA-WPF test discipline (memory v1.2.11)**: App.Tests use NSubstitute mocks; no actual WPF control instantiation. Avoids multi-STA-test `Application.Current` singleton collision. v1.5.0 App.Tests follow same discipline.
8. **CA1848 + CA2012 source-gen pattern (where logging added)**: Project has `TreatWarningsAsErrors=true` + `AnalysisMode=Recommended`. v1.5.0 did not add new logging in shipped items, but pattern is ready for any future additions.
9. **Path normalizer in `Path` namespace causes `System.IO.Path` shadowing**: Document this in XML doc-comment of `PathNormalizer`. 9-file cascade-rename in Task 1 was expensive but correct; future maintainers should NOT add new files to `PeakCan.Host.Core.Path` namespace without re-checking `System.IO.Path` usage.
10. **Mid-handshake lockout flip test SKIP rationale preserved across releases (memory v1.4.1 lesson 1)**: v1.4.1 PATCH Item 1 Test 2 SKIPPED with rationale "defer to v1.4.2 PATCH after SetSeed fix". v1.4.2 PATCH must either re-enable the test (after SetSeed fix) or carry the SKIP forward with updated rationale.

## 关键文件

### 新增
- `src/PeakCan.Host.Core/Path/PathNormalizer.cs`
- `src/PeakCan.Host.Core/Path/PathNormalizationException.cs`
- `appsettings.json` (at repo root)

### 修改
- `Directory.Build.props` (version 1.4.1 → 1.5.0)
- `src/PeakCan.Host.App/Services/DbcService.cs` (1 Normalize call)
- `src/PeakCan.Host.Core/Uds/Database/DidDatabase.cs` (2 Normalize calls)
- `src/PeakCan.Host.Core/Uds/Database/RoutineDatabase.cs` (2 Normalize calls)
- `src/PeakCan.Host.Core/Replay/ReplayService.cs` (1 Normalize call)
- `src/PeakCan.Host.Core/Uds/Database/DidDatabaseDefaults.cs` (Path.Combine → System.IO.Path.Combine)
- `src/PeakCan.Host.Core/Uds/Database/RoutineDatabaseDefaults.cs` (Path.Combine → System.IO.Path.Combine)
- `src/PeakCan.Host.Core/Replay/IReplayService.cs` (Loop + CanIdFilter + PlaybackAdded)
- `src/PeakCan.Host.Core/Replay/ReplayService.cs` (Loop + CanIdFilter properties)
- `src/PeakCan.Host.Core/Replay/ReplayTimeline.cs` (loop boundary + filter check)
- `src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs` (IConfiguration + OnSelectedChannelChanged + suppress-flag)
- `src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs` (Loop + CanIdFilterText + parser)
- `src/PeakCan.Host.App/Views/ReplayView.xaml` (Loop CheckBox + CAN ID filter TextBox)
- `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` (IConfiguration singleton + AppShellViewModel factory wire)
- `src/PeakCan.Host.App/PeakCan.Host.App.csproj` (PreserveNewest appsettings.json copy)
- 7 Core.Tests files: `E51PTCANBMSDbcFixtureTests.cs`, `IReplayServiceTests.cs`, `DidDatabaseNreTests.cs`, `DidDatabaseTests.cs`, `RoutineDatabaseNreTests.cs`, `RoutineDatabaseTests.cs`, `UdsClientSecurityAccessOverloadTests.cs` (Path.Combine → System.IO.Path.Combine)

### 测试新增
- `tests/PeakCan.Host.Core.Tests/Path/PathNormalizerTests.cs` (6 tests)
- `tests/PeakCan.Host.App.Tests/ViewModels/AppShellViewModelTests.cs` (+5 tests)
- `tests/PeakCan.Host.Core.Tests/Replay/ReplayTimelineTests.cs` (+6 tests)
- `tests/PeakCan.Host.Core.Tests/Replay/IReplayServiceTests.cs` (+2 tests + 1 review-fix test)
- `tests/PeakCan.Host.App.Tests/ViewModels/ReplayViewModelTests.cs` (+3 tests)

## 风险与缓解

| Risk | Probability | Impact | Mitigation |
|------|-------------|--------|------------|
| `PathNormalizer` rejects legitimate paths with `..` segments (e.g. `D:\projects\..\projects\foo\dbc\test.dbc`) | MEDIUM | LOW | Spec §Decision 1 explicit: defense-in-depth, not root-restricted. Users who hit this should use absolute canonical path. v1.6.0 will add proper root restriction. |
| `Path` namespace shadowing `System.IO.Path` causes future maintainer confusion | MEDIUM | LOW | XML doc-comment on `PathNormalizer` namespace warns. 9-file cascade-rename establishes precedent. CI build catches any new occurrence at compile time. |
| `Channel:SelectedHandle` config schema change in future breaks persisted handle | LOW | LOW | Hex format + null handling is minimal schema. Future schema change requires migration plan. |
| Replay Loop=ON + ASC file with corrupt frames → infinite loop | LOW | LOW | Loop restarts at 0; corrupt frames at end still consumed. `ReplayService.LoadAsync` validates >50% malformed. |
| Replay CanIdFilter hot-swap mid-playback causes race | LOW | LOW | `CanIdFilter` is `IReadOnlySet<uint>?` property; ReplayTimeline reads on each `EmitFrame` (not cached). Atomic reference swap. |
| AppHostBuilder new `IConfiguration` parameter breaks 14 existing `AppShellViewModel` ctor call sites | RESOLVED | n/a | Optional last param + DI factory; verified all 14 call sites compile clean. |
| `CyclicSendServiceRaceTests` transient flakes (memory v1.2.12) | HIGH | NONE | Pre-existing, not introduced by this MINOR. Passes in isolation. |
| Network proxy during ship | LOW | MEDIUM | Per memory `git-push-network-workaround`: proxy 127.0.0.1:7897 intermittent; fallback to `gh api` for tag/release. |

## Known follow-ups (deferred)

- **v1.4.2 PATCH** (carry-over from v1.4.1 out-of-scope finding):
  - HIGH: `UdsSecurity.SetSeed` wipes `AttemptCount` + `LockedUntilUtc` (defeats v1.3.0 lockout under concurrent access). Fix requires `SetSeed` to preserve existing state. Product pending: "should lockout persist across successful authentications?".
  - Re-enable v1.4.1 PATCH Item 1 mid-handshake lockout flip test once SetSeed is fixed.
  - MEDIUM: `ReplaySinkAdapter` surface first-failure as `ReplayException` (user-hostile silent drop on no-channel).
- **v1.5.1 PATCH**:
  - LOW: Replay time-range filter (post-EOF scrubbing to arbitrary timestamp, not just Loop restart at 0).
  - LOW: Periodic DBC send (auto-send DBC message at fixed interval; requires `CyclicSendService` integration — memory v1.2.12 lesson 4: known transient flakes).
- **v1.6.0 MINOR** (carry-over from v1.3.0 / v1.4.0 / v1.5.0 spec §Non-Goals):
  - HIGH: V8 sandbox hardening (replace defense-in-depth with hard root restriction + path whitelist).
  - MEDIUM: CanApi rate limit (require benchmark + spec review before implementation).
  - MEDIUM: DBC size/token limit (prevent DoS via 100MB DBC files).
  - MEDIUM: Path normalization root restriction (replace v1.5.0 defense-in-depth with `Path.GetFullPath` rooted-at-`baseDirectory`).
  - MEDIUM: OEM `IKeyDerivationAlgorithm` concrete (requires OEM cooperation for algorithm spec).
- **Future MINOR**: Replay→Trace auto-load + Value table encoding + Multiplexed signal groups UI + `ReplaySinkAdapter` first-failure propagation.
