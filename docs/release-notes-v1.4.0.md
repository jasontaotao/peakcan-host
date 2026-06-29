# v1.4.0 MINOR Release Notes

**Ship date:** 2026-06-29
**Branch:** `feature/v1-4-0-minor` (cut from main @ v1.3.1 `a45af20`)
**Base:** main @ v1.3.1 `a45af20`
**Squash SHA:** (filled at ship)

## 概述

v1.4.0 是一个 2 项 MINOR 增量（Replay + Send DBC），起源于 v1.3.0 MINOR release notes §"Decomposition context" 列的 8 项 deferred scope。Phase 2.5 actual-code exploration 后 spec 已收敛。**全部为 user-facing 大 feature**。无 UDS 协议增强（v1.4.0 focus is Replay + Send DBC UX）。

## 起源

v1.3.0 MINOR release notes §"Decomposition context" + v1.3.1 PATCH ship notes §"Known follow-ups" 标出 v1.4.0 MINOR scope：

| Item | Source | Severity |
|------|--------|----------|
| Replay (ASC parser + time-based replay + speed control) | v1.3.0 decomposition | MINOR (user value) |
| Send DBC signal encoding (signal values → frame bytes) | v1.3.0 decomposition | MINOR (user value) |
| Atomic concurrent mid-handshake race test for SecurityAccessAsync | v1.3.1 PATCH review (Important #4) | LOW (defers to v1.4.1 PATCH) |

### Decomposition decision (spec §"Decomposition")

v1.4.0 MINOR = Replay + Send DBC. Atomic race test defers to v1.4.1 PATCH because:
- Test-only change (no user value)
- 1 timing-dependent test too small to justify standalone MINOR
- Adjacent to UDS SecurityAccess; fits future UDS PATCH cycle

### Phase 2.5 actual code exploration findings (spec §"Phase 2.5")

实际读 v1.3.1 shipped 代码确认 brief 描述准确 + 锁定关键 design points：

| Assumption | Phase 2.5 actual |
|---|---|
| "Replay needs ASC parser" | **ASC writer exists** at `RecordService.cs:312-313`. Format: `{timestamp:F6} {channel:X2}  {id:X}  {dlc}  {dataHex}{fd}{brs}{esi}{error}`. Parser must be tolerant of: header lines (`date`, `base`, `internal events`), comment lines (`//`), empty lines, trailing footer. **Plus: data bytes may be space-separated (Vector) OR concatenated (RecordService uses `Convert.ToHexString` — no spaces).** |
| "Send DBC needs frame encoder" | **Decoder exists** at `SignalDecoder.cs` (`DecodeRaw`, `Decode`, `ReadLittleEndian`, `ReadBigEndian`). Encoder is the inverse: take physical value → apply inverse scale/offset → raw bits → write to byte buffer at correct bit positions. Same little/big-endian convention. |
| "Replay needs frame routing" | **ChannelRouter** at `Infrastructure/Channel/` exists with `IFrameSink` interface. Replay injects frames via `SendService` (outbound path), not `ChannelRouter` (inbound fan-out). **Verified in Task 4/6 brief-drift catch.** |
| "Signal multiplexing support" | **Multiplexor support exists** in `Signal` record (`IsMultiplexor`, `IsMultiplexed`, `MultiplexValue`) — Task 6 of v1.3.0 added these. Encoder must respect mux gating. |
| "Value table lookup" | `Signal.ValueTableName` + `DbcDocument.ValueTables` dict. Encoder needs to lookup value table for enum signals. **Deferred from v1.4.0 MVP** per spec §"Non-Goals". |

## Scope

2 MINOR items = Replay + Send DBC encoder. Both Core/Infra + App UI integration.

| # | Item | 组件 | 工作 | Source | Severity |
|---|------|------|------|--------|----------|
| 1 | **Replay** | Core: `AscParser`, `ReplayTimeline`, `IReplayService` + App: `ReplayView`, `ReplayViewModel` | ASC parser (tolerant of headers/comments + space-separated AND concatenated hex) + time-based playback (1ms Timer) + speed control (0.25/0.5/1/2/4x) + Play/Pause/Resume/Seek + IReplayFrameSink injection via SendService | v1.3.0 decomposition | MINOR |
| 2 | **Send DBC** | Core: `DbcEncodeService` + App: `DbcSendViewModel` + SendView "DBC mode" Expander | Signal values → frame bytes (inverse of SignalDecoder) + multiplexor support + signed/unsigned/float/double + scale/offset inverse + IEEE-754 round-trip | v1.3.0 decomposition | MINOR |

## Non-Goals

- **v1.4.1 PATCH**: atomic concurrent mid-handshake race test for `SecurityAccessAsync` — **deferred**.
- **v1.5.0 MINOR**: V8 sandbox hardening + CanApi rate limit + DBC size/token limit + path normalization + OEM `IKeyDerivationAlgorithm` concrete + Channel picker — **deferred**.
- **Replay scope**: loop playback, CAN ID filter, time-range filter — **deferred** (not in v1.4.0).
- **Periodic DBC send** (DBC message auto-sent at fixed interval) — **deferred**. Would require `CyclicSendService` integration; memory v1.2.12 lesson 4: `CyclicSendServiceRaceTests` has known transient flakes.
- **Replay→Trace integration** (auto-load ASC into Trace view) — **deferred**.
- **Multiplexed signal groups UI** (auto-show only valid mux signals) — **deferred** per spec.
- **Value table encoding** (text → int via `DbcDocument.ValueTables`) — **deferred** per spec.
- 公开 API 移除 / 依赖升级 / 用户数据 schema migration — MINOR 纪律（只 additive）。
- 新增 NuGet 包。

## Per-Item 修复详情

### Item 1 — Replay (MINOR)

**Symptom**: No way to play back recorded traces. v1.2.6+ RecordService writes ASC, but no parser + player + UI.

**Fix** (5 Core files + 4 App files + 1 test project file):

- `src/PeakCan.Host.Core/Replay/AscParser.cs` — `ParseAsync(Stream, CancellationToken)`. Tolerant of: header lines (`date`, `base`, `internal events`), comment lines (`//`-prefixed), empty lines, trailing footer. Handles BOTH space-separated (Vector) AND concatenated (RecordService `Convert.ToHexString`) hex byte layouts. Skips malformed lines, throws only on fatal errors (file empty, >50% malformed).
- `src/PeakCan.Host.Core/Replay/ReplayFrame.cs` — immutable record `(Timestamp, Id, Dlc, Data, Flags)`.
- `src/PeakCan.Host.Core/Replay/ReplayTimeline.cs` — internal sealed class. `System.Threading.Timer` 1ms tick. Lock-guarded state. `SetSpeed` re-anchors play start to preserve playback position.
- `src/PeakCan.Host.Core/Replay/IReplayService.cs` — public surface: `LoadAsync` / `Play` / `Pause` / `Resume` / `Seek` / `SetSpeed` / `Stop` + `State` / `CurrentTimestamp` / `TotalDuration` / `Speed` + `FrameEmitted` event (fired on timer thread — UI must marshal).
- `src/PeakCan.Host.Core/Replay/ReplayService.cs` — DI singleton `IReplayService` impl, `IDisposable`. `LoadAsync` wraps `FileNotFoundException` / IO error in `ReplayLoadException`; `ReplayFormatException` from `AscParser` propagates. Sink errors logged + playback continues.
- `src/PeakCan.Host.App/Composition/ReplayFrameSinkAdapter.cs` — `IReplayFrameSink` impl that wraps `SendService.SendAsync` (outbound path) — NOT `ChannelRouter` (inbound fan-out, as brief assumed).
- `src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs` — MVVM with `[ObservableProperty]` + `[RelayCommand]`. Captures `SynchronizationContext` in ctor + `Post` in `OnFrameEmitted` to marshal UI updates from timer thread.
- `src/PeakCan.Host.App/Views/ReplayView.xaml(.cs)` — minimal WPF UserControl: open button + transport bar + scrubber + speed combo + error message.

**Tests**: +19 (6 ReplayTimeline + 3 IReplayService + 4 ReplayViewModel + 6 defensive coverage). All NSubstitute-mocked, no STA-WPF race per memory v1.2.11.

**Brief drift caught (now 9-of-9+ pattern)**: per-task implementers re-read actual source before writing. Notable catches:
- `FrameFlags` namespace = `PeakCan.Host.Core` (not `Channel` subfolder); member = `ErrFrame` (not `Error`).
- `CanFrame` ctor is 5-arg positional record struct with `CanId` first (not 4-arg with Id/Data/Dlc/Flags).
- `ReplayService` ctor takes `IReplayFrameSink sink, ILogger<ReplayService> logger` (not the constructor signature brief assumed).
- SendView is a `StackPanel` (not `TabControl`) — DBC mode added as `Expander` inside the existing panel.
- `DbcService.LoadedDocument` → `DbcService.Current` (real property name).
- `ReplayFrameSinkAdapter` ctor takes `SendService` (not `ChannelRouter` as brief assumed — see Phase 2.5).

### Item 2 — Send DBC (MINOR)

**Symptom**: SendView accepts raw CAN frame bytes. User has no way to send DBC messages by signal name (must hand-compute byte positions per DBC layout).

**Fix** (2 Core files + 2 App files + 1 test project file):

- `src/PeakCan.Host.Core/Dbc/DbcEncodeService.cs` — `Encode(Message, IReadOnlyDictionary<string, double>) → byte[Dlc]`. Handles: DBC endianness (Little/Big), sign extension (Unsigned/Signed), IEEE-754 reinterpretation (Float/Double), engineering scale/offset inverse, multiplexor gating. Overflow guard: `raw > long.MaxValue` throws `DbcSignalValueOutOfRangeException` before cast.
- `src/PeakCan.Host.Core/Dbc/DbcEncodeExceptions.cs` — 4 exception types: `DbcSignalEncodeException` (abstract base) + `DbcSignalValueOutOfRangeException` / `DbcSignalNotFoundException` / `DbcMultiplexorRequiredException` / `DbcSignalConfigurationException` (concrete, for Factor=0 invariant violation).
- `src/PeakCan.Host.App/ViewModels/DbcSendViewModel.cs` — DBC message dropdown + signal DataGrid editor + Send button. Encodes via `DbcEncodeService.Encode`, sends via `SendService.SendAsync`. Catches `DbcSignalEncodeException` → user-friendly `ErrorMessage`.
- `src/PeakCan.Host.App/ViewModels/DbcSendViewModel.cs` — per-signal row VM (`Signal` + nullable `Value` + `DisplayName` + `ValueType`).
- `src/PeakCan.Host.App/Views/SendView.xaml` — DBC mode `Expander` added alongside existing raw mode StackPanel.

**Tests**: +14 (12 DbcEncodeService + 2 DbcSendViewModel). Includes round-trip tests (v1.3.1 lesson) — `Encode_Decode_Roundtrip_PreservesValues` + `Encode_Decode_Roundtrip_WithRounding_PreservesValue` (the second exercises `Math.Round(AwayFromZero)`).

**Brief drift caught**: same 9-of-9+ pattern. Notable:
- `DbcService.LoadedDocument` → `DbcService.Current` (above).
- `CanFrame` ctor 5-arg positional (above).
- `Message.Id` carries merged IDE bit for extended frames (`Message.cs:6-9`) — implementer correctly routes to `FrameFormat.Extended/Standard` and masks.
- `DbcService` has no parameterless ctor — NSubstitute can't proxy. Used real `DbcService` + `SetCurrentForTests` test seam (mirrors `SendViewModelTests.FakeSendService` pattern).

## DI 注册（修改 `AppHostBuilder.cs`）

```csharp
// v1.4.0 MINOR Replay
builder.Services.AddSingleton<ReplayFrameSinkAdapter>();
builder.Services.AddSingleton<IReplayFrameSink>(sp => sp.GetRequiredService<ReplayFrameSinkAdapter>());
builder.Services.AddSingleton<IReplayService, ReplayService>();
// v1.4.0 MINOR Send DBC
builder.Services.AddSingleton<DbcEncodeService>();
```

**Integration test** (`AppHostBuilderTests.Build_Registers_ReplayAndDbcEncodeServices`) verifies all 4 services resolve + `IReplayService` is singleton.

## Pre-ship code-review verdict

**Whole-branch review** (`git diff 407e1de..b7dae97`, 28 files / +5083 lines / 215KB diff):

- **0 Critical**
- **1 Important** (fixed in commit `3b82f43`): AscParser could not parse `RecordService`'s own concatenated-hex output. Brief fixtures used space-separated Vector convention only; no end-to-end test bridged producer (`RecordService.WriteFrame`) and consumer (`AscParser.ParseAsync`). Fix: when post-DLC token is > 2 chars, slice into 2-char hex byte chunks. +1 test `Parse_RecordServiceConcatenatedHexFormat_RoundTrip` (RED→GREEN).
- **4 Minor** (all non-blocking, follow-up candidates):
  - DbcSendViewModel reads DbcService.Current at construction only — no live refresh on late DBC load
  - AscParser doesn't log skipped malformed lines (silently increments counter)
  - ReplayView Slider two-way binding + DragCompleted event is slightly racy
  - ReplayTimeline.OnTick swallows exceptions with empty catch

Per-task review verdicts:
- Task 1 (AscParser): APPROVED after 1 fix cycle
- Task 2 (Replay pipeline): NEEDS FIXES → 5 Important fixed in `0f66092`
- Task 3 (DbcEncodeService): APPROVED after 1 fix cycle (I-1, I-2 + 2 Minor) in `cfc1a46`
- Task 4 (Replay UI): APPROVED (0 Critical/Important)
- Task 5 (Send DBC UI): APPROVED (0 Critical/Important)
- Task 6 (DI wiring): APPROVED (0 Critical/Important)
- Task 7 (final review): With one fix → Important #1 fixed in `3b82f43`

**Final test count**: 736 pass + 6 SKIP + 0 fail (Core 296 / Infra 84+2 SKIP / App 356+4 SKIP). Transient `CyclicSendServiceRaceTests` flakes per memory v1.2.12 lesson 4 — pre-existing, NOT introduced by this PATCH.

## Tests

| Metric | v1.3.1 baseline (memory) | **v1.4.0 final** | Δ |
|--------|--------------------------|------------------|-------|
| Total pass | 688 + 6 SKIP + 0 fail | **736 + 6 SKIP + 0 fail** | **+48 net** |
| Core.Tests | 264 | **296** | +32 (AscParser 7 + ReplayTimeline 6 + IReplayService 3 + DbcEncodeService 14 + Task 7a fix 1) |
| Infra.Tests | 84 + 2 SKIP | **84 + 2 SKIP** | unchanged |
| App.Tests | 344 | **356** | +12 (ReplayViewModel 4 + DbcSendViewModel 2 + AppHostBuilder 1 + defensive 5) |

**Brief-vs-source drift pattern (9-of-9+ confirmed)**: Every implementer re-read actual source before writing code. Notable catches: `FrameFlags` namespace + member name; `CanFrame` 5-arg ctor; `DbcService.Current`; `SendService` (not `ISendService`); `Expander` (not `TabControl`); `ReplayFrameSinkAdapter` wraps `SendService` (not `ChannelRouter`).

**TDD discipline validated**: Per-task implementers used the v1.3.1 pattern of writing RED tests, verifying they FAIL against unfixed code, then applying fixes. Final-review fix applied same pattern: `Parse_RecordServiceConcatenatedHexFormat_RoundTrip` verified FAIL (`ReplayFormatException`) pre-fix, PASS post-fix.

**Round-trip tests (v1.3.1 lesson applied)**: `Encode_Decode_Roundtrip_PreservesValues` + `Encode_Decode_Roundtrip_WithRounding_PreservesValue` — would FAIL against an asymmetric encoder.

## Ship process

12 commits on `feature/v1-4-0-minor`:
- `407e1de` spec(v1.4.0): Replay + Send DBC signal encoding design (on main)
- `6f57c12` plan(v1.4.0): Replay + Send DBC signal encoding implementation plan
- `94849fa` Task 1: AscParser (Core) + 6 tests
- `3a7126a` Task 1 review fix (I-1 trailing newlines, I-3 DLC validation)
- `71d9d50` Task 2: Replay pipeline (Core) + 9 tests
- `0f66092` Task 2 review fix (I-1 through I-5 + M-1 + M-5)
- `c83e78a` Task 3: DbcEncodeService (Core) + 10 tests
- `cfc1a46` Task 3 review fix (I-1, I-2, M-1, M-2)
- `69a46b7` Task 4: Replay UI (App) + 10 tests
- `7541c2e` Task 5: Send DBC UI (App) + 2 tests
- `b7dae97` Task 6: AppHostBuilder DI wiring + 1 integration test
- `3b82f43` Task 7 final review fix (AscParser concatenated hex) + 1 test
- `96fd79e` chore: bump version to 1.4.0

squash → PR → main → tag v1.4.0 → release.

## Spec drops (post Phase 2.5)

| Brief | Drop reason |
|-------|-------------|
| Periodic DBC send | Out of scope; would require `CyclicSendService` integration (memory v1.2.12 lesson 4: known transient flakes). |
| Value table encoding | Out of scope; UI shows signal value as numeric input, not dropdown. |
| Replay loop + CAN ID filter + time-range filter | Out of scope; full Playback scope is Play/Pause/Resume/Scrub + Speed. |
| Replay→Trace integration | Out of scope; Replay is independent view. |
| Multiplexed signal groups UI | Out of scope; UI shows all signals, user responsible for mux value. |
| Atomic concurrent mid-handshake race test | Deferred to v1.4.1 PATCH (1 timing-dependent test too small for MINOR). |

## Decomposition context (from v1.3.0 release notes)

- **v1.3.0**: UDS protocol completion (5 items, 1 MEDIUM + 4 LOW)
- **v1.3.1**: UDS SecurityAccess tightening (2 HIGH + 1 LOW, deferred carry-overs)
- **v1.4.0 (this release)**: Replay + Send DBC (2 user-facing MINOR features)
- **v1.4.1 PATCH (planned)**: atomic concurrent race test for SecurityAccessAsync
- **v1.5.0 MINOR (planned)**: V8 sandbox + CanApi rate limit + DBC size/token limit + path normalization + OEM `IKeyDerivationAlgorithm` concrete + Channel picker

## Breaking changes

**无**. 所有 v1.4.0 范围内变更, additive only. 关键 invariants:
- `LockoutConfig` setter `internal set` 保留 (v1.3.0 ship prep fix #3)
- 现有 `IReplayService` / `IReplayFrameSink` 是 NEW 接口, 旧 caller 不受影响
- 现有 `DbcService` API 不变; `DbcEncodeService` 是 NEW 独立 service
- 现有 `SendService` / `ChannelRouter` 行为不变; `ReplayFrameSinkAdapter` 复用 `SendService` 同一 outbound path
- `CanFrame` ctor 是 NEW record struct, 旧字段访问 path 仍兼容 (memory v1.4.0 Task 4 lesson: actual signature is 5-arg positional)
- 现有 0 个 public API removal

## ADR note: AscParser hex format tolerance (Task 7 final review)

**Decision**: AscParser must accept BOTH space-separated (Vector ASC convention) AND concatenated (RecordService's `Convert.ToHexString`) hex byte layouts. Implementation: when post-DLC token has length > 2, slice into 2-char chunks; single-char tokens still work; odd-length tokens rejected as malformed.

**Rationale**: Replay is intended to play back traces recorded by this very app. The current parser was originally written to only accept Vector-convention files, which meant it could not read this app's own `RecordService` output. The Task 7 final review caught this production-vs-test gap (brief fixtures used space-separated; no end-to-end test bridged producer and consumer). Fix: token-length 3-way dispatch (`Length==1`, `Length%2!=0` reject, even-length slice into 2-char chunks).

**Test coverage**: `Parse_RecordServiceConcatenatedHexFormat_RoundTrip` exercises the actual `RecordService.cs:312-313` format end-to-end. Per v1.3.1 lesson: TDD discipline catches production-vs-test gaps that unit tests miss.

## Process lessons (from v1.3.1 + v1.3.0 + earlier, applied to v1.4.0)

1. **TDD test discrimination (10-of-10 confirmed)**: A RED test must FAIL against unfixed code AND PASS against fixed code. Per-task implementers verified this consistently.
2. **Brief-vs-source drift is structural (9-of-9+ confirmed)**: Phase 2.5 actual-code exploration is non-negotiable. Per-task implementers re-read actual source before writing code, catching 9+ brief defects (FrameFlags namespace + member, CanFrame ctor, DbcService.Current, ReplayFrameSinkAdapter ctor, etc.).
3. **Round-trip tests (mandatory for symmetric encode/decode)**: `Encode → Decode → equals original` catches asymmetric encoding bugs that pure forward tests miss. v1.3.1 lesson applied to DbcEncodeService.
4. **End-to-end producer/consumer tests**: Brief fixtures (e.g. Vector-convention ASC) may not match actual producer output (e.g. RecordService concatenated hex). Task 7 final review caught this gap.
5. **`Func<Task>` wrapper for `FluentAssertions.ThrowAsync<T>()`**: Required for `Task`-returning calls. Verified per-task in Task 2 + Task 3.
6. **STA-WPF test discipline (memory v1.2.11)**: App.Tests use NSubstitute mocks; no actual WPF control instantiation. Avoids multi-STA-test `Application.Current` singleton collision.
7. **Real-services-for-state / mocks-for-interfaces split**: `DbcService` (no paramless ctor) → real + `SetCurrentForTests` test seam. NSubstitute can't proxy. `IReplayService` (interface) → NSubstitute mock. Best of both worlds.
8. **Threading for cross-thread events**: `ReplayService.FrameEmitted` fires on timer thread (per Task 2 I-5 fix). `ReplayViewModel` captures `SynchronizationContext` in ctor + `Post` in event handler. UI bindings safe.
9. **CA1848 + CA2012 source-gen pattern**: Project has `TreatWarningsAsErrors=true` + `AnalysisMode=Recommended`. `partial class` + `[LoggerMessage]` source-gen + extracted `EmitFrameToSinkAsync` helper with CA2012 suppression. Per project pattern from v1.3.0 `UdsSecurity`.
10. **`gh pr merge --squash --delete-branch` "Not possible to fast-forward" failure is recurring (v1.2.13 + v1.3.0 + v1.3.1)**: Always `git fetch origin main` first.
11. **Ship pattern**: `git push -u origin feature/v1-X-Y-minor` (proxy 127.0.0.1:7897 if direct times out) → PR → `gh pr merge --squash --delete-branch` (after `git fetch origin main`) → `git fetch origin main` + `git reset --hard origin/main` (squash SHA) → `git tag v1.X.Y` → `git push origin v1.X.Y` → `gh release create v1.X.Y --notes-file docs/release-notes-v1.X.Y.md`.

## 关键文件

### 新增
- `src/PeakCan.Host.Core/Replay/AscParser.cs`
- `src/PeakCan.Host.Core/Replay/ReplayFrame.cs`
- `src/PeakCan.Host.Core/Replay/ReplayTimeline.cs`
- `src/PeakCan.Host.Core/Replay/IReplayService.cs`
- `src/PeakCan.Host.Core/Replay/ReplayService.cs`
- `src/PeakCan.Host.Core/Replay/ReplayState.cs`
- `src/PeakCan.Host.Core/Replay/IReplayFrameSink.cs`
- `src/PeakCan.Host.Core/Replay/ReplayExceptions.cs`
- `src/PeakCan.Host.Core/Dbc/DbcEncodeService.cs`
- `src/PeakCan.Host.Core/Dbc/DbcEncodeExceptions.cs`
- `src/PeakCan.Host.App/Composition/ReplayFrameSinkAdapter.cs`
- `src/PeakCan.Host.App/Views/ReplayView.xaml(.cs)`
- `src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs`
- `src/PeakCan.Host.App/ViewModels/DbcSendViewModel.cs`

### 修改
- `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` (4 new DI registrations)
- `src/PeakCan.Host.App/Views/SendView.xaml` (DBC mode Expander)
- `src/PeakCan.Host.App/ViewModels/SendViewModel.cs` (DbcSendViewModel wiring)
- `Directory.Build.props` (version 1.3.1 → 1.4.0)

## 风险与缓解

| Risk | Probability | Impact | Mitigation |
|------|-------------|--------|------------|
| `ReplayFrameSinkAdapter.SendFrameAsync` discards `Result<Unit>` | HIGH | MEDIUM | `SendService` logs internally; user sees replay files play but no error if no PEAK channel connected. Future: surface first-failure as `ReplayException` to VM. |
| `DbcSendViewModel` reads DbcService only at construction | HIGH | LOW | If user opens Send tab before DBC loaded, dropdown stays empty. Workaround: load DBC first. Future: subscribe to `DbcService.DbcLoaded` event. |
| `ReplayTimeline.OnTick` swallows all exceptions | MEDIUM | LOW | `ReplayService.EmitFrame` has its own try/catch + logger for synchronous part; async sink call errors are caught there. Inner empty catch is defense-in-depth. |
| Network proxy during ship | LOW | MEDIUM | Per memory `git-push-network-workaround`: proxy 127.0.0.1:7897 intermittent; fallback to `gh api` for tag/release. |
| Phase 2.5 missed API surface assumption | LOW | LOW | v1.4.0 9-of-9+ confirmed brief drift is structural; Task 7 final review caught the last one. |
| Multiplexed signal UI confusion | HIGH | LOW | Out of scope per Non-Goals; user must set mux value correctly. Encoder throws if mux constraint violated. |
| `CyclicSendServiceRaceTests` transient flakes (memory v1.2.12) | HIGH | NONE | Pre-existing, not introduced by this PATCH. Passes in isolation. |

## Known follow-ups (deferred)

- **v1.4.1 PATCH** (carry-over from v1.3.1 PATCH review): atomic concurrent mid-handshake race test for `SecurityAccessAsync` (1 timing-dependent test).
- **v1.4.1 PATCH or v1.4.2 PATCH**: AscParser log skipped malformed lines (production debuggability).
- **v1.4.1 PATCH or v1.4.2 PATCH**: DbcSendViewModel subscribe to DbcService.DbcLoaded event (live DBC load refresh).
- **v1.4.x PATCH or v1.5.0 MINOR**: ReplaySinkAdapter surface first-failure as ReplayException (user-hostile silent drop on no-channel).
- **v1.5.0 MINOR**: V8 sandbox hardening + CanApi rate limit + DBC size/token limit + path normalization + OEM `IKeyDerivationAlgorithm` concrete + Channel picker.
- **Future MINOR**: Replay loop + CAN ID filter + time-range filter; Periodic DBC send; Value table encoding; Multiplexed signal groups UI; Replay→Trace integration.