# v1.4.0 MINOR — Replay + Send DBC Signal Encoding Design Spec

**Date:** 2026-06-28
**Branch:** `feature/v1-4-0-minor` (cut from main @ v1.3.1 `a45af20`)
**Target version:** v1.4.0 (MINOR)
**Spec owner:** main agent
**Reviewer:** code-reviewer subagent (pre-ship)
**Pre-flight:** Phase 2.5 actual code exploration (ASC writer format, DBC structure, ChannelRouter routing)

## 起源

v1.3.0 MINOR release notes §"Decomposition context" + v1.3.1 PATCH ship notes §"Known follow-ups" 标出 v1.4.0 MINOR scope：

| Item | Source | Severity |
|------|--------|----------|
| Replay (ASC parser + time-based replay + speed control) | v1.3.0 §"Decomposition context" (Replay was always deferred) | MINOR (user value) |
| Send DBC signal encoding (signal values → frame bytes) | v1.3.0 §"Decomposition context" (Send DBC was always deferred) | MINOR (user value) |
| Atomic concurrent mid-handshake race test for SecurityAccessAsync | v1.3.1 PATCH pre-ship review (deferred from PATCH) | LOW (test-only, defers to v1.4.1 PATCH) |

### Decomposition decision

v1.4.0 MINOR = Replay + Send DBC (both big user-facing features). Atomic race test defers to v1.4.1 PATCH because:
- Test-only change (no user value)
- 1 timing-dependent test is too small to justify its own MINOR
- Adjacent to UDS SecurityAccess; fits future UDS PATCH cycle

### Phase 2.5 actual code exploration findings

实际读 v1.3.1 shipped 代码确认 brief 描述准确 + 锁定关键 design points：

| Assumption | Phase 2.5 actual |
|---|---|
| "Replay needs ASC parser" | **ASC writer exists** at `RecordService.cs:294-315`. Format: `{timestamp:F6} {channel:X2}  {id:X}  {dlc}  {dataHex}{fd}{brs}{esi}{error}`. Parser must be tolerant of: header lines (`date`, `base`, `internal events`), comment lines (`//`), empty lines, trailing footer (`// {elapsed:F3} s`). |
| "Send DBC needs frame encoder" | **Decoder exists** at `SignalDecoder.cs` (`DecodeRaw`, `Decode`, `ReadLittleEndian`, `ReadBigEndian`). Encoder is the inverse: take physical value → apply inverse scale/offset → raw bits → write to byte buffer at correct bit positions. Same little/big-endian convention. Same `StartBit` ushort + `Length` byte field layout. |
| "Replay needs frame routing" | **ChannelRouter** at `src/PeakCan.Host.Infrastructure/Channel/` exists with `IFrameSink` interface. Replay can inject frames via this interface. |
| "Signal multiplexing support" | **Multiplexor support exists** in `Signal` record (`IsMultiplexor`, `IsMultiplexed`, `MultiplexValue`) — Task 6 of v1.3.0 added these. Encoder must respect mux gating (only signals with matching mux value are sent). |
| "Value table lookup" | `Signal.ValueTableName` + `DbcDocument.ValueTables` dict. Encoder needs to lookup value table for enum signals (user enters text "OFF" → encoder maps to raw int 0). |

## Scope

2 MINOR items = Replay + Send DBC encoder. Both Core/Infra + App UI integration.

| # | Item | 组件 | 工作 | 来源 | Severity |
|---|------|------|------|------|----------|
| 1 | **Replay** | Core: `AscParser`, `ReplayTimeline`, `IReplayService` + App: `ReplayView`, `ReplayViewModel` | ASC parser (tolerant of headers/comments) + time-based playback scheduler (1ms timer resolution) + speed control (0.25/0.5/1/2/4x) + Play/Pause/Resume/Seek public surface + frame sink injection via ChannelRouter | v1.3.0 decomposition | MINOR |
| 2 | **Send DBC** | Core: `DbcEncodeService` + App: extend `SendView` with DBC mode tab + new `DbcSendViewModel` (or sub-VM) | Signal values → frame bytes (inverse of SignalDecoder) + multiplexor support + value table lookup + signed/unsigned/float/double encoding + scale/offset inverse transform + UI binding | v1.3.0 decomposition | MINOR |

## Non-Goals

- **v1.4.1 PATCH** (carry-over from v1.3.1 review): atomic concurrent mid-handshake race test for `SecurityAccessAsync` — **deferred**.
- **v1.5.0 MINOR**: V8 sandbox hardening + CanApi rate limit + DBC size/token limit + path normalization + OEM `IKeyDerivationAlgorithm` concrete + Channel picker — **deferred**.
- **Replay scope**: loop playback, CAN ID filter, time-range filter — **deferred** (not in v1.4.0, possible v1.4.2 PATCH or v1.5.0).
- **Periodic DBC send** (DBC message auto-sent at fixed interval) — **deferred**. Would require `CyclicSendService` integration; risk too high for v1.4.0 (memory v1.2.12 lesson 4: `CyclicSendServiceRaceTests` has known transient flakes).
- **Replay→Trace integration** (auto-load ASC into Trace view) — **deferred**. Replay is independent view with its own transport bar.
- **Multiplexed signal groups UI** (auto-show only valid mux signals) — **out of scope**. UI shows all signals; user responsible for mux value. Encoder throws if mux constraint violated.
- 公开 API 移除 / 依赖升级 / 用户数据 schema migration — MINOR 纪律（只 additive）。
- 新增 NuGet 包。
- 改现有 `IKeyDerivationAlgorithm` / `SignalDecoder` / `DbcParser` 接口签名。

## 设计决策

### Decision 1: Replay architecture — Core pipeline + App UI

**选项 A** (推荐): Core pipeline (`AscParser` + `ReplayTimeline` + `IReplayService`) + App UI (`ReplayView` + `ReplayViewModel`). Core = pure logic, App = WPF binding.

**选项 B**: 全部 App 层。优点：零 Core API surface change。缺点：unit test 需 STA WPF，与 memory v1.2.11 + v1.2.12 STA-WPF xunit race 历史问题叠加。

**选项 C**: 全部 Infra。Core 跟 Replay 无直接关系，强行放 Infra 不自然。

**决策**: A。理由：Replay 逻辑（时间线调度、速度换算、frame 路由）完全可在 STA-free 单元测试。App UI 只负责 binding + 文件对话框。

### Decision 2: DBC encoder location — Core (new service, alongside `SignalDecoder`)

**选项 A** (推荐): 新 `DbcEncodeService` 在 `src/PeakCan.Host.Core/Dbc/DbcEncodeService.cs`，与 `SignalDecoder` (静态类) 镜像但作为 instance service。

**选项 B**: 静态类 `DbcEncoder.Encode(message, signalValues) -> byte[]`，无 DI。

**选项 C**: 加 method 到现有 `DbcService` (App 层)。

**决策**: A。理由：
- DI singleton 与项目现有 pattern 一致（`DbcService`, `DbcDecodeBackgroundService` 都是 DI）。
- 未来 Scripting engine 扩展可注入 `DbcEncodeService` 做 `sendDbcSignal` API（v1.0.0 加的 ScriptEngine）。
- 静态类难以 mock test；instance service 可以。

### Decision 3: ASC parser tolerance level

**选项 A** (推荐): Tolerant — skip malformed lines, log warning, continue parsing. Throw only on fatal errors (file not found, IO error, no frames after N attempts).

**选项 B**: Strict — throw on any malformed line.

**决策**: A。理由：ASC 是事实标准（CANoe/CANalyzer），可能有 vendor-specific 扩展或注释行。Tolerant parser 能 load 这些文件，UX 更好。

### Decision 4: Replay frame sink — DI-injected `IReplayFrameSink`

**选项 A** (推荐): 新接口 `IReplayFrameSink` in Core/Infra，默认实现 `ChannelRouterReplayFrameSink`（通过 `ChannelRouter.SendFrameAsync` 注入）。Tests 用 `FakeReplayFrameSink` 捕获 emitted frames。

**选项 B**: Replay 直接 call `ChannelRouter.SendFrameAsync` 静态方法。

**决策**: A。理由：测试性 + DI 一致 + 不与 ChannelRouter 紧耦合。

### Decision 5: DBC encoder error model

**选项 A** (推荐): 显式 exception types (`DbcSignalValueOutOfRangeException`, `DbcSignalNotFoundException`, `DbcMultiplexorRequiredException`)。UI catches + shows user-friendly message。

**选项 B**: 返回 `Result<byte[], DbcEncodeError>` discriminated union。

**决策**: A。理由：与项目现有 exception pattern 一致 (`UdsException`, `UdsNegativeResponseException`, `UdsSecurityLockedException`)。

## 架构

### Replay pipeline

```
User (App.ReplayView)
  ↓ WpfFileDialogService.OpenFileDialog()
ReplayViewModel.LoadFileAsync(path)
  ↓ DI
IReplayService.LoadAsync(stream)
  ↓
AscParser.ParseAsync(stream)
  ↓ parse header + skip comments + parse frames
  → ReplayFrame[] sorted by timestamp
  ↓
ReplayTimeline.SetFrames(frames)
  ↓ ViewModel renders scrubber
User clicks "Play"
  ↓
ReplayViewModel.Play()
  ↓
IReplayService.Play()
  ↓ starts System.Threading.Timer (1ms tick)
  ↓
ReplayTimeline.Tick()
  ↓ for each frame where now >= frame.Timestamp * speed
    → IReplayFrameSink.SendFrameAsync(frame) (DI-injected)
User drags scrubber
  ↓
ReplayViewModel.Seek(timestamp)
  ↓
IReplayService.Seek(t) → ReplayTimeline.SetPosition(t)
```

### Send DBC pipeline

```
User (App.SendView "DBC mode" tab)
  ↓ DBC message dropdown
SendViewModel.DbcMessage = msg (resolved from DbcService.LoadedDocument)
  ↓ ViewModel renders signal grid (one row per Signal in msg.Signals)
User fills signal values
  ↓ SendViewModel.SignalValues[signalName] = double
User clicks "Send"
  ↓
SendViewModel.SendDbcAsync()
  ↓
DbcEncodeService.EncodeAsync(msg, SignalValues)
  ↓ for each signal: physical → raw via (raw = (physical - offset) / factor)
  ↓ write raw bits at signal.StartBit (little/big endian aware)
  → byte[] payload
  ↓
ISendService.SendAsync(new CanFrame(Id, payload, Dlc, Flags))
  ↓ bus
```

## 组件

### Core 新增

| File | Public surface |
|------|----------------|
| `src/PeakCan.Host.Core/Replay/AscParser.cs` | `static class AscParser { public static IReadOnlyList<ReplayFrame> ParseAsync(Stream stream, CancellationToken ct = default) }` |
| `src/PeakCan.Host.Core/Replay/ReplayFrame.cs` | `public sealed record ReplayFrame(double Timestamp, uint Id, byte Dlc, byte[] Data, FrameFlags Flags)` |
| `src/PeakCan.Host.Core/Replay/ReplayTimeline.cs` | `internal sealed class ReplayTimeline { public void SetFrames(IReadOnlyList<ReplayFrame>) / void Play() / void Pause() / void Resume() / void Seek(double timestamp) / void SetSpeed(double multiplier) / void Stop() }` |
| `src/PeakCan.Host.Core/Replay/IReplayService.cs` | `public interface IReplayService { Task LoadAsync(string path, CancellationToken ct = default) / void Play() / void Pause() / void Resume() / void Seek(double timestamp) / void SetSpeed(double multiplier) / void Stop() / ReplayState State { get; } / double CurrentTimestamp { get; } / double TotalDuration { get; } / event Action<ReplayFrame>? FrameEmitted }` |
| `src/PeakCan.Host.Core/Replay/ReplayService.cs` | `public sealed class ReplayService : IReplayService { ... }` — DI singleton, owns ReplayTimeline + Timer |
| `src/PeakCan.Host.Core/Replay/ReplayState.cs` | `public enum ReplayState { Stopped, Playing, Paused }` |
| `src/PeakCan.Host.Core/Replay/IReplayFrameSink.cs` | `public interface IReplayFrameSink { ValueTask SendFrameAsync(ReplayFrame frame, CancellationToken ct = default) }` |
| `src/PeakCan.Host.Core/Replay/ReplayExceptions.cs` | `ReplayLoadException`, `ReplayFormatException` |
| `src/PeakCan.Host.Core/Dbc/DbcEncodeService.cs` | `public sealed class DbcEncodeService { public byte[] Encode(Message message, IReadOnlyDictionary<string, double> signalValues) / public bool TryEncode(Message, IReadOnlyDictionary<string, double>, out byte[]? bytes, out string? error) }` |
| `src/PeakCan.Host.Core/Dbc/DbcEncodeExceptions.cs` | `DbcSignalValueOutOfRangeException`, `DbcSignalNotFoundException`, `DbcMultiplexorRequiredException`, `DbcSignalEncodeException` (base) |

### App 新增

| File | Purpose |
|------|---------|
| `src/PeakCan.Host.App/Views/ReplayView.xaml(.cs)` | Open file button + scrubber + transport bar (Play/Pause/Stop) + speed combo (0.25/0.5/1/2/4) + current/total timestamp display |
| `src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs` | MVVM bind to IReplayService state + commands |
| `src/PeakCan.Host.App/Composition/ReplayFrameSinkAdapter.cs` | `IReplayFrameSink` impl that calls `ChannelRouter.SendFrameAsync` |
| `src/PeakCan.Host.App/Views/SendView.xaml(.cs)` | Extend existing view: add `DbcMode` tab (alongside existing raw mode) |
| `src/PeakCan.Host.App/ViewModels/SendViewModel.cs` | Extend with DBC mode (sub-VM or merged) |
| `src/PeakCan.Host.App/ViewModels/DbcSendViewModel.cs` | New sub-VM for DBC mode (cleaner separation per SRP) |

### DI 注册（修改 `AppHostBuilder`）

```csharp
// Replay pipeline
builder.Services.AddSingleton<ReplayFrameSinkAdapter>();
builder.Services.AddSingleton<IReplayFrameSink>(sp => sp.GetRequiredService<ReplayFrameSinkAdapter>());
builder.Services.AddSingleton<IReplayService, ReplayService>();

// DBC encoder
builder.Services.AddSingleton<DbcEncodeService>();
```

## 数据流

### Replay happy path

1. User clicks "Open" in `ReplayView` → `WpfFileDialogService.OpenFileDialog(filter: ".asc")` returns path
2. `ReplayViewModel.LoadFileAsync(path)`:
   - Opens `FileStream(path, FileMode.Open)`
   - Calls `IReplayService.LoadAsync(stream)`:
     - `AscParser.ParseAsync(stream)` reads line-by-line, skips `//`-prefixed comments and `date`/`base`/`internal events` headers
     - Validates each data line: `{timestamp} {channel}  {id}  {dlc}  {dataHex} [flags]`
     - Malformed lines: log warning + skip (don't throw)
     - Returns `IReadOnlyList<ReplayFrame>` sorted by timestamp
   - Calls `ReplayTimeline.SetFrames(frames)`:
     - Sets `_frames`, `_totalDuration = frames[^1].Timestamp`
     - Notifies subscribers of `_totalDuration` change
3. `ReplayViewModel` updates: `TotalDuration`, `IsLoaded = true`, `ScrubberMaxValue = TotalDuration`
4. User clicks "Play" → `ReplayViewModel.Play()` → `IReplayService.Play()`:
   - `ReplayTimeline.Play()` starts `System.Threading.Timer` at 1ms tick
   - Each tick: `now = (DateTime.UtcNow - _playStartTime).TotalSeconds * _speed`, find next frame where `frame.Timestamp <= now`, emit via `IReplayFrameSink.SendFrameAsync`
5. User drags scrubber to t=10.5s → `ReplayViewModel.Seek(10.5)` → `ReplayTimeline.Seek(10.5)`:
   - `_currentTimestamp = 10.5`, skip ahead to frame index where `frame.Timestamp >= 10.5`
6. User clicks "Pause" → `ReplayTimeline.Pause()` → stop timer, keep `_currentTimestamp`
7. User clicks "Resume" → `ReplayTimeline.Resume()` → restart timer from `_currentTimestamp`
8. User changes speed combo from 1x to 2x → `ReplayTimeline.SetSpeed(2.0)` → `_speed = 2.0`, next tick plays 2x faster

### Send DBC happy path

1. User opens `SendView`, switches to "DBC mode" tab
2. `SendViewModel.DbcMessages` populated from `DbcService.LoadedDocument.Messages`
3. User selects `EngineData` message → `SendViewModel.SelectedDbcMessage = engineData`
4. `SendViewModel.DbcSignals` populated: `[{Name, ValueType, Min, Max, Unit, ValueTableName}, ...]`
5. `SendViewModel` renders signal grid (one row per signal, with editor: numeric input OR value table dropdown if `ValueTableName != null`)
6. User fills values: `EngineRPM = 2500.0`, `VehicleSpeed = 60.0`, ...
7. User clicks "Send" → `SendViewModel.SendDbcAsync()`:
   - Calls `DbcEncodeService.Encode(engineData, signalValues)`:
     - For each signal: `raw = (physical - offset) / factor` (round to nearest int)
     - Validate range: `signal.Min <= physical <= signal.Max` else throw `DbcSignalValueOutOfRangeException`
     - Pack raw bits at signal.StartBit (little/big endian aware, signed/unsigned aware)
   - Returns `byte[]` payload
   - Calls `ISendService.SendAsync(new CanFrame(engineData.Id, payload, engineData.Dlc, FrameFlags.None))`

## 错误处理

| Error | Source | Handling |
|-------|--------|----------|
| File not found | `ReplayService.LoadAsync` | Throw `ReplayLoadException` → UI shows snackbar "ASC file not found" |
| IO error reading file | `ReplayService.LoadAsync` | Throw `ReplayLoadException` → UI shows "Cannot read ASC file: {message}" |
| Empty ASC file (no data lines) | `AscParser.ParseAsync` | Throw `ReplayFormatException` → UI shows "ASC file has no frames" |
| >50% malformed lines | `AscParser.ParseAsync` | Throw `ReplayFormatException` → UI shows "ASC file appears corrupted (X% malformed)" |
| Timer fault during playback | `ReplayTimeline.Tick` | Log + auto-pause, raise `FrameEmitted` event with error payload |
| Frame send failure (bus disconnect) | `IReplayFrameSink.SendFrameAsync` | Log + continue playback (don't stall) |
| Signal out of range | `DbcEncodeService.Encode` | Throw `DbcSignalValueOutOfRangeException(signalName, value, min, max)` → UI shows per-signal error |
| Signal name not in message | `DbcEncodeService.Encode` | Throw `DbcSignalNotFoundException(signalName, messageName)` → UI shows error |
| Multiplexor value required but missing | `DbcEncodeService.Encode` | Throw `DbcMultiplexorRequiredException(messageName)` → UI shows "set multiplexor value" |
| Multiplexed signal sent with wrong mux value | `DbcEncodeService.Encode` | Throw `DbcSignalMultiplexMismatchException(signalName, expectedMux, actualMux)` → UI shows error |

**No silent failures**. All errors throw → UI shows user-friendly message via MVVM error state.

## 测试策略

### AscParser tests (~6 tests in Core.Tests)

1. `Parse_ValidAsc_ReturnsAllFrames` — well-formed ASC with 100 frames → all parsed, sorted by timestamp
2. `Parse_TolerantOfComments` — file with `// comment` lines → comments skipped, frames still parsed
3. `Parse_TolerantOfHeaders` — file with `date`, `base`, `internal events` headers → headers skipped
4. `Parse_EmptyFile_Throws` — file with no data lines → throw `ReplayFormatException`
5. `Parse_MalformedLines_LogsAndSkips` — file with 10% malformed lines → returns valid frames, logs warnings
6. `Parse_LargeFile_Performance` — 10000 frames → parses in <100ms (CI gate)

### ReplayTimeline tests (~6 tests in Core.Tests)

1. `Play_EmitsFramesAtCorrectTimestamps` — load frames with t=0,1,2,3s → play → emits at correct wall-clock time
2. `Pause_HaltsPlayback` — play 1s → pause → no more frames emitted
3. `Resume_ContinuesFromPausePoint` — pause at t=2s → resume → next frame after t=2s emitted
4. `Seek_JumpsToTimestamp` — seek to t=5s → next frame emitted is at t>=5s
5. `SetSpeed_ScalesTimestamps` — speed=2x → frames emitted at half the original wall-clock time
6. `EndOfStream_StopsTimer` — play to end → `State == Stopped`, timer disposed

### IReplayService / DI tests (~3 tests in Core.Tests)

1. `Service_DI_ResolvesSameInstance` — DI singleton returns same instance across requests
2. `Service_RaisesFrameEmitted_Event` — load + play → subscribers receive frames
3. `Service_LoadFromPath_OpensFile` — `LoadAsync(path)` reads file successfully

### DbcEncodeService tests (~10 tests in Core.Tests)

1. `Encode_SimpleSignal_NoScaleNoOffset` — Signal(Factor=1, Offset=0) → raw = physical
2. `Encode_SignedSignal_NegativeValue` — Signal(ValueType.Signed, Length=8) → raw = two's-complement
3. `Encode_LittleEndianSignal_PacksAtStartBit` — Signal(Order=LittleEndian) → bits packed LSB-first at StartBit
4. `Encode_BigEndianSignal_PacksAtStartBit` — Signal(Order=BigEndian) → bits packed MSB-first at StartBit
5. `Encode_FloatSignal_Ieee754Bits` — Signal(ValueType=Float, Length=32) → raw = IEEE-754 single-precision bits
6. `Encode_DoubleSignal_Ieee754Bits` — Signal(ValueType=Double, Length=64) → raw = IEEE-754 double-precision bits
7. `Encode_ScaleAndOffset_InverseTransform` — Signal(Factor=0.1, Offset=-40) → raw = round((physical - (-40)) / 0.1) = (physical + 40) * 10
8. `Encode_MultiplexedMessage_PacksMuxValue` — Message with multiplexor + multiplexed signal → both mux value + signal packed
9. `Encode_ValueTableLookup_TextToRawInt` — Signal with ValueTableName + table "OFF"=0, "ON"=1 → encode("OFF") packs raw=0
10. `Encode_SignalOutOfRange_Throws` — physical > Max → throw `DbcSignalValueOutOfRangeException`
11. `Encode_UnknownSignal_Throws` — values dict contains unknown signal name → throw `DbcSignalNotFoundException`
12. `Encode_MissingMultiplexor_Throws` — Message.IsMultiplexed but mux value not in values dict → throw `DbcMultiplexorRequiredException`

### App.Tests (~6 tests)

1. `ReplayViewModel_LoadFile_UpdatesScrubberBounds` — load file → ScrubberMaxValue = TotalDuration
2. `ReplayViewModel_Play_TogglesIsPlaying` — play → IsPlaying=true, IsPaused=false
3. `ReplayViewModel_Pause_TogglesIsPaused` — play then pause → IsPlaying=false, IsPaused=true
4. `ReplayViewModel_SetSpeed_UpdatesSpeed` — set speed=2.0 → Speed=2.0, UI re-renders
5. `DbcSendViewModel_SelectMessage_PopulatesSignals` — select message → DbcSignals populated
6. `DbcSendViewModel_Send_InvokesEncodeService` — fill values + send → EncodeService.Encode called with correct args

### Test count expectations

| Suite | v1.3.1 baseline | **v1.4.0 final** | Δ |
|-------|-----------------|------------------|---|
| Core.Tests | 264 | **289** | +25 (AscParser 6 + ReplayTimeline 6 + IReplayService 3 + DbcEncodeService 12) |
| Infra.Tests | 84 + 2 SKIP | **84 + 2 SKIP** | unchanged |
| App.Tests | ~347 (343-345 pass + 4 SKIP, 1-2 flakes) | **~353 (349-351 pass + 4 SKIP)** | +6 (ReplayViewModel 4 + DbcSendViewModel 2) |
| **Total** | ~697 | **~728** | **+31 net** |

### TDD discipline guard (per v1.3.0 + v1.3.1 lessons)

Brief作者必须验证每个 RED test 真的 FAIL against unfixed code (避免 v1.3.1 PATCH 出现的 1-attempt non-discriminating 断言问题)。Plan Task 1 中应明示:
- AscParser test 用真实 ASC fixture (推荐 from existing RecordService output — record a session then replay)
- DbcEncodeService tests 用 round-trip: encode value → decode via SignalDecoder → assert equal to original (catches asymmetric encoding bugs)

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
- `src/PeakCan.Host.App/Views/ReplayView.xaml(.cs)`
- `src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs`
- `src/PeakCan.Host.App/ViewModels/DbcSendViewModel.cs`
- `src/PeakCan.Host.App/Composition/ReplayFrameSinkAdapter.cs`

### 修改
- `src/PeakCan.Host.App/Views/SendView.xaml(.cs)` — add DBC mode tab
- `src/PeakCan.Host.App/ViewModels/SendViewModel.cs` — wire DbcSendViewModel
- `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` — register Replay + DbcEncodeService
- `docs/release-notes-v1.4.0.md` — new
- `Directory.Build.props` — version bump 1.3.1 → 1.4.0

## 风险与缓解

| Risk | Probability | Impact | Mitigation |
|------|-------------|--------|------------|
| **ASC parser tolerance hides real bugs** | MEDIUM | LOW | Log all skipped lines with line number + reason; UI shows "X lines skipped" warning on load |
| **Replay timer drift** (1ms resolution vs 100μs typical CAN frame interval) | HIGH | LOW | Document in `ReplayTimeline` XML doc that 1ms timer is sufficient for typical CAN rates (<500fps). Faster rates will queue frames; not a correctness issue. |
| **DBC encoder IEEE-754 host endianness** | MEDIUM | MEDIUM | DBC convention is wire-order; encoder must write IEEE-754 bits in the exact same order as `SignalDecoder.DecodeRaw` reads them. Plan must include a round-trip test: `SignalDecoder.Decode(SignalDecoder.Encode(v)) == v` for float/double. |
| **Replay→ChannelRouter integration race** | MEDIUM | MEDIUM | Use existing `ChannelRouter.SendFrameAsync` (already tested). New `IReplayFrameSink` adapter wraps it; tests use fake sink. |
| **Multiplexed signal UI confusion** | HIGH | LOW | Out of scope per Non-Goals; document in DbcSendView XML doc that user must set mux value correctly. |
| **Network proxy during ship** | LOW | MEDIUM | Per memory `git-push-network-workaround`: proxy 127.0.0.1:7897 intermittent; fallback to `gh api` for tag/release. |
| **Phase 2.5 missed API surface assumption** | MEDIUM | LOW | v1.3.0 + v1.3.1 4-of-4 confirmed brief drift is structural; Plan Task 1 will re-read `RecordService.WriteFrame` + `SignalDecoder.Read*Endian` to confirm exact wire format. |

## Ship process

1. `git checkout -b feature/v1-4-0-minor` (from main @ v1.3.1 `a45af20`)
2. Per-Item TDD (2 implementation tasks: Replay, Send DBC) — each Task with reviewer subagent
3. Pre-ship code-review (whole-branch)
4. Bump `Directory.Build.props` 1.3.1 → 1.4.0
5. Write `docs/release-notes-v1.4.0.md`
6. Push → PR → squash → tag v1.4.0 → release
7. Update memory `peakcan-host-v1-4-0-shipped.md`

## 后续 (deferred to later releases)

- **v1.4.1 PATCH**: atomic concurrent mid-handshake race test for `SecurityAccessAsync` (v1.3.1 carry-over)
- **v1.4.2 PATCH or v1.5.0 MINOR**: Replay loop playback + CAN ID filter + time-range filter
- **v1.5.0 MINOR**: V8 sandbox hardening + CanApi rate limit + DBC size/token limit + path normalization + OEM `IKeyDerivationAlgorithm` concrete + Channel picker + periodic DBC send (if deferred from v1.4.0)