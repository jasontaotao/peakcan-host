# v1.5.0 MINOR — Channel Picker + Path Normalization + Replay Follow-up Design Spec

**Date:** 2026-06-29
**Branch:** `feature/v1-5-0-minor` (cut from main @ v1.4.1 `cbd17f5`)
**Target version:** v1.5.0 (MINOR)
**Spec owner:** main agent
**Reviewer:** code-reviewer subagent (pre-ship)
**Pre-flight:** Phase 2.5 actual code exploration (path call sites, channel enumeration, replay service)

## 起源

v1.4.0 MINOR release notes §"Decomposition context" + v1.4.1 PATCH release notes §"Known follow-ups" 标出 v1.5.0 MINOR 候选 scope：

| Item | Source | Severity | Decision |
|------|--------|----------|----------|
| V8 sandbox hardening | v1.3.0 decomposition | MINOR | **Deferred to v1.6.0** (scope uncertain) |
| CanApi rate limit | v1.3.0 decomposition | MINOR | **Deferred to v1.6.0** (defense-in-depth, low user visibility) |
| DBC size/token limit | v1.3.0 decomposition | MINOR | **Deferred to v1.6.0** (DoS, niche) |
| Path normalization | v1.3.0 decomposition | MINOR | **IN v1.5.0** |
| OEM `IKeyDerivationAlgorithm` concrete | v1.3.0 decomposition | MINOR | **Deferred to v1.6.0** (no OEM spec, demo impl unclear value) |
| Channel picker | v1.3.0 decomposition | MINOR | **IN v1.5.0** (UX gap — file does not exist) |
| Replay loop / CAN ID filter / time-range filter | v1.4.0 spec §Non-Goals (line 54) | MINOR | **IN v1.5.0** (loop + CAN ID filter only) |
| Periodic DBC send | v1.4.0 spec §Non-Goals (line 55) | MINOR | **Deferred to v1.5.1 PATCH** (`CyclicSendService` flakes) |
| `UdsSecurity.SetSeed` wipes AttemptCount HIGH | v1.4.1 PATCH release notes §"Out-of-scope findings" | HIGH | **Deferred to v1.4.2 PATCH** (Product decision pending) |

### Decomposition decision

v1.5.0 MINOR = **Path normalization (Security) + Channel picker (UX) + Replay follow-up (UX)**. 3 items, balanced Security+UX, no Product decision dependencies.

Deferred to v1.5.1 PATCH: Replay time-range filter (carved out of v1.5.0 to keep scope tight).
Deferred to v1.6.0 MINOR: V8 sandbox + CanApi rate limit + DBC limits + OEM KeyDerivation concrete.
Deferred to v1.4.2 PATCH: SetSeed HIGH bug.

### Phase 2.5 actual code exploration findings

实际读 v1.4.1 shipped 代码确认 brief 描述准确 + 锁定关键 design points：

| Assumption | Phase 2.5 actual |
|---|---|
| "Path normalization needed" | **4 unvalidated file IO call sites**: `DbcService.cs:File.ReadAllBytesAsync`, `DidDatabase.cs:File.ReadAllText` (×2), `RoutineDatabase.cs:File.ReadAllText` (×2), `ReplayService.cs:File.OpenRead`. All accept arbitrary string. No traversal check. |
| "Channel picker doesn't exist" | **Confirmed**: no `IChannel*` or `*ChannelPicker*` file in `src/`. `SessionPanelViewModel` carries hint text suggesting channel is hardcoded or DI-only. `PCANBasic` API exists in Infrastructure but no enumeration wrapper. |
| `IKeyDerivationAlgorithm` "concrete" | **Interface exists** at `src/PeakCan.Host.Core/Uds/IKeyDerivationAlgorithm.cs` (1 method `ComputeKey(seed, level)`). DI registered as `PlaceholderKeyAlgorithm` (no-op stub that throws `KeyAlgorithmNotConfiguredException`). "Concrete" OEM impl requires OEM-specific spec — no spec available, so concrete impl would be demo-only. **Drop from v1.5.0.** |
| "Replay Loop / Filter easy add" | **`IReplayService` has 7 public methods + 4 properties**: `LoadAsync` / `Play` / `Pause` / `Resume` / `Seek` / `SetSpeed` / `Stop` + `State` / `CurrentTimestamp` / `TotalDuration` / `Speed` + `FrameEmitted` event. Loop + CanIdFilter are additive properties. No breaking change. |
| "Channel enumeration testable" | **`PCANBasic` is a native DLL** (`PCANBasic.dll`). Pure unit tests must use `IChannelEnumerator` interface + `FakeChannelEnumerator` (mirrors v1.4.0 `DbcService` test-seam pattern from `DbcViewModelTests`). |
| "Path normalization can break callers" | All 4 call sites are passed absolute paths from `OpenFileDialog` (WPF returns absolute). `Path.GetFullPath` on absolute path is idempotent. Risk of regression LOW. |

## Scope

3 MINOR items = 1 Security + 2 UX. Mixes Security+UX as originally themed.

| # | Item | 组件 | 工作 | Source | Severity |
|---|------|------|------|--------|----------|
| 1 | **Path normalization** | Core: `PathNormalizer` + 4 call-site updates | Reject `..` traversal, null bytes, relative paths; allow absolute paths. Pure helper, no DI. | v1.3.0 decomposition | MINOR (security) |
| 2 | **Channel picker** | Core: `IChannelEnumerator` + `PeakCanChannelEnumerator`; App: `ChannelPickerViewModel` + `ChannelPickerView` + MainView integration; `AppHostBuilder` DI | Enumerate PEAK PCAN-USB FD channels + UI ComboBox in MainView header + persist last-selected channel to appsettings. | v1.3.0 decomposition | MINOR (UX) |
| 3 | **Replay loop + CAN ID filter** | Core: extend `IReplayService` (Loop / CanIdFilter) + `ReplayTimeline`; App: extend `ReplayViewModel` + `ReplayView.xaml` | Loop playback (auto-restart on EOF) + per-frame CanId filter (HashSet<uint> gating) + UI Loop CheckBox + CanIdFilter TextBox parser. | v1.4.0 spec §Non-Goals line 54 | MINOR (UX) |

## Non-Goals

- **v1.4.2 PATCH** (carry-over): HIGH `UdsSecurity.SetSeed` wipes AttemptCount — **deferred** (Product decision on lockout-across-successful-auth pending).
- **v1.5.1 PATCH** (v1.5.0 carve-out): Replay time-range filter (start/end timestamps) — **deferred** (3rd replay feature, ship 1 then iterate).
- **v1.5.1 PATCH**: Periodic DBC send (CyclicSendService integration) — **deferred** (memory v1.2.12 lesson 4: known transient flakes).
- **v1.6.0 MINOR**: V8 sandbox hardening + CanApi rate limit + DBC size/token limit + path normalization 限根目录 + OEM `IKeyDerivationAlgorithm` concrete.
- Path normalization **限根目录** (only allow files under `%APPDATA%/peakcan-host/`) — **deferred to v1.6.0** (breaking change risk; current callers pass absolute paths from `OpenFileDialog`; needs Product decision on which directories are "trusted").
- Channel picker hot-plug detection (USB insert/remove events) — **out of scope**. ComboBox refreshes on app start; user reopens to refresh.
- Channel picker `IsAvailable` indicator (LED-style status) — **out of scope**. List channel handles + names only; availability checked at Send attempt.
- Replay → Trace integration (auto-load ASC into Trace view) — **out of scope** (per v1.4.0 §Non-Goals).
- Multiplexed signal groups UI — **out of scope** (per v1.4.0 §Non-Goals).
- Value table encoding — **out of scope** (per v1.4.0 §Non-Goals).
- 公开 API 移除 / 依赖升级 / 用户数据 schema migration — MINOR 纪律（只 additive）。
- 新增 NuGet 包。

## 设计决策

### Decision 1: Path normalization level — reject traversal, don't restrict roots

**选项 A** (推荐): 拒绝 `..` 段、null bytes、relative paths; absolute paths 直接通过。简单、低回归。

**选项 B**: 限根目录到 `%APPDATA%/peakcan-host/` 等受信任路径。更安全，但破坏现有 caller 传进来的绝对路径（`OpenFileDialog` 返回的绝对路径不一定在白名单根下）。

**选项 C**: 完全不 normalize（仅 `Path.GetFullPath` 解 symbolic links）。最低风险，但无安全价值。

**决策**: A。理由：v1.5.0 范围是 defense-in-depth fix，不是 sandbox 化。3 个 deferred item 已经把限根目录推迟到 v1.6.0，那时再统一决定 roots。当前 fix 阻断 90% 的 traversal attack vectors（`../etc/passwd` 这类）。

### Decision 2: Channel enumeration — interface + DI, native impl separated

**选项 A** (推荐): `IChannelEnumerator` interface in Core + `PeakCanChannelEnumerator` in Infrastructure (native `PCANBasic` call) + `ChannelPickerViewModel` in App. Tests use `FakeChannelEnumerator` (mirrors v1.4.0 `DbcService` test-seam pattern).

**选项 B**: Static class `ChannelEnumerator.Enumerate()` with internal PCANBasic call. Hard to test.

**选项 C**: Channel enumeration embedded in `ChannelPickerViewModel` directly. Mixes UI + native.

**决策**: A。理由：项目所有 native API 都通过 Infrastructure 层（`PCANBasic` 在 `src/PeakCan.Host.Infrastructure/`）。Interface 让 unit test 不依赖 native。Fake enumerator 简单，3-5 个 test method 即可。

### Decision 3: Channel persistence — appsettings.json last-selected

**选项 A** (推荐): Last-selected channel handle 写到 `appsettings.json` (`Channel: { SelectedHandle: "PCAN_USBBUS1" }`)。App start时尝试 re-select。

**选项 B**: 不持久化，每次启动默认第一项。简单。

**选项 C**: `%APPDATA%/peakcan-host/last-channel.txt` 单独文件。隔离。

**决策**: A。理由：项目已有 `appsettings.json` DI pattern（`IConfiguration`）。用户期望"上次选的 channel 还在"是合理 UX。新增 1 个 JSON 节点，不需要新文件。

### Decision 4: Replay Loop semantics — restart at 0 on EOF, preserve position on enable

**选项 A** (推荐): Loop ON + playback 到达 `TotalDuration` → `_currentTimestamp` 重置为 0 + 继续 emit。Loop OFF + 到达末尾 → 状态变 `Stopped` + emit `PlaybackEnded` event (新增)。

**选项 B**: Loop ON 时，到达末尾后立即重 emit 起点帧（不等 1ms），造成 burst。

**选项 C**: Loop OFF 不发 `PlaybackEnded` event，保持当前 timestamp。

**决策**: A。理由：和 v1.4.0 `ReplayTimeline` 1ms timer + 现有 state machine 一致。`PlaybackEnded` event 让 VM 决定是显示"已结束"还是切到 Loop 模式。保持 timeline 行为最小化（v1.4.0 已 ship pattern 不变）。

### Decision 5: CanIdFilter implementation — `HashSet<uint>` on `IReplayService`, gating in timeline

**选项 A** (推荐): `IReplayService.CanIdFilter` property 类型 `IReadOnlySet<uint>?`。`null` = 全部通过；非空 = 集合内 ID 才 emit。`ReplayTimeline` 在 emit 前 check。

**选项 B**: Predicate delegate `Func<uint, bool>?`。更灵活但难序列化、难在 UI 双向 binding。

**选项 C**: Two sets (allow / deny)。复杂，超 v1.5.0 scope。

**决策**: A。理由：`HashSet<uint>` O(1) 查找，UI binding 友好（can be marshalled as `IReadOnlySet`）。null 表示"全通过"是 .NET 标准模式（vs `IEnumerable<uint>.Any()` 空集合 → 行为不一致）。`ReplayTimeline` 持有 `IReplayService` 引用，每次 emit 前 check 最新 filter（用户可以在播放时改 filter）。

### Decision 6: CanIdFilter UI parsing — hex/decimal CSV in TextBox

**选项 A** (推荐): 单行 TextBox，逗号/空格分隔，每项支持 `0x` 前缀十六进制或纯十进制。Parse 失败显示 inline error，不打断 playback。

**选项 B**: Multi-line TextBox，每行一个 ID。复杂。

**选项 C**: DataGrid 增删行。Overkill for 1 字段。

**决策**: A。理由：Replay 用户的 filter 列表通常很短（1-10 个 ID），单行够用。Parse 容错：失败项忽略 + 提示（per v1.4.0 `AscParser` 同样 tolerant 哲学）。

## 架构

### Path normalization

```
Caller (DbcService / DidDatabase / RoutineDatabase / ReplayService)
  ↓ string path
PathNormalizer.Normalize(path)
  ↓ throws PathNormalizationException | returns canonical absolute path
File.ReadAllBytesAsync / File.OpenRead (unchanged signature)
```

`PathNormalizer` (new):
- `public static class PathNormalizer`
- `public static string Normalize(string path)`
- Throws `PathNormalizationException` on:
  - `null` / empty
  - Contains `..` segment (after `Path.GetFullPath` if needed)
  - Contains null byte (`\0`)
  - Is relative path (doesn't start with drive letter or `\\`)
- Returns: `Path.GetFullPath(path)` (canonical absolute form)

`PathNormalizationException` (new):
- Extends `ArgumentException` (matches .NET convention for invalid path args)
- Carries `Path` property + `Reason` enum (`NullPath`, `EmptyPath`, `RelativePath`, `TraversalSegment`, `NullByte`)

### Channel picker

```
App startup
  ↓ DI
ChannelPickerViewModel
  ↓ IChannelEnumerator
PeakCanChannelEnumerator (Infra, native PCANBasic)
  ↓ List<ChannelDescriptor>
ChannelPickerViewModel.AvailableChannels
  ↓ WPF binding
ChannelPickerView (ComboBox in MainView header)

User selects channel
  ↓ ChannelChanged event
ChannelPickerViewModel.SelectedChannel
  ↓ IConfiguration
appsettings.json persistence

Other services (e.g. SendService) listen to ChannelChanged event
```

`IChannelEnumerator` (Core, new):
```csharp
public interface IChannelEnumerator
{
    IReadOnlyList<ChannelDescriptor> Enumerate();
}

public sealed record ChannelDescriptor(string Handle, string Name);
```

`PeakCanChannelEnumerator` (Infrastructure, new):
- Implements `IChannelEnumerator`
- Calls `PCANBasic.GetValue(PCAN_LISTAVAILABLE)` or equivalent
- Returns `[]` on native call failure (logged warning)
- Disposable — releases `PCANBasic` handle

`ChannelPickerViewModel` (App, new):
- DI singleton
- `IReadOnlyList<ChannelDescriptor> AvailableChannels { get; }`
- `ChannelDescriptor? SelectedChannel { get; set; }` (raises `ChannelChanged`)
- On ctor: call `IChannelEnumerator.Enumerate()`, read last-selected from `IConfiguration`, set `SelectedChannel`
- On setter: write to `IConfiguration` (or call `IConfigurationPersister.Save()`)

`ChannelPickerView` (App, new):
- `UserControl` with `ComboBox` bound to `AvailableChannels` + `SelectedChannel`
- Display member path: `Name`
- `IsEnabled` bound to `AvailableChannels.Count > 0`

`AppHostBuilder` updates:
- Register `IChannelEnumerator` → `PeakCanChannelEnumerator` (singleton, infrastructure)
- Register `ChannelPickerViewModel` (singleton, app)
- Wire `MainView` to host `ChannelPickerView` in header region

`appsettings.json` schema addition:
```json
{
  "Channel": {
    "SelectedHandle": "PCAN_USBBUS1"
  }
}
```

### Replay Loop + CanIdFilter

```
IReplayService (extended)
  + bool Loop { get; set; }
  + IReadOnlySet<uint>? CanIdFilter { get; set; }
  + event EventHandler? PlaybackEnded  (new)
  ↓
ReplayTimeline (extended)
  Loop ON + CurrentTimestamp >= TotalDuration:
    _currentTimestamp = 0
    emit continues
  Loop OFF + CurrentTimestamp >= TotalDuration:
    state = Stopped
    raise PlaybackEnded
  EmitFrame (per tick):
    if CanIdFilter != null && !CanIdFilter.Contains(frame.Id):
      skip (don't emit, don't advance)
    else:
      emit as before

ReplayViewModel (extended)
  + bool Loop (proxy to IReplayService.Loop)
  + string CanIdFilterText
  + OnCanIdFilterTextChanged: parse, update CanIdFilter

ReplayView.xaml (extended)
  + CheckBox "Loop" bound to Loop
  + TextBox "CAN ID filter (e.g. 0x100, 0x200, 256)" bound to CanIdFilterText
```

`IReplayService` extension (additive, non-breaking):
```csharp
public interface IReplayService
{
    // Existing (unchanged)
    Task LoadAsync(Stream stream, CancellationToken ct = default);
    void Play();
    void Pause();
    void Resume();
    void Seek(TimeSpan position);
    void SetSpeed(double multiplier);
    void Stop();
    ReplayState State { get; }
    TimeSpan CurrentTimestamp { get; }
    TimeSpan TotalDuration { get; }
    double Speed { get; }
    event EventHandler<ReplayFrame>? FrameEmitted;
    
    // New (v1.5.0)
    bool Loop { get; set; }
    IReadOnlySet<uint>? CanIdFilter { get; set; }
    event EventHandler? PlaybackEnded;
}
```

`ReplayTimeline` internal changes:
- `OnTick` end-of-stream branch: if `Loop` → reset `_currentTimestamp = TimeSpan.Zero` and `_playStartTimestamp = _stopwatch.Elapsed`; else → `_service.SetState(Stopped)` + raise `PlaybackEnded`.
- `EmitFrame` (or equivalent method): add `if (CanIdFilter != null && !CanIdFilter.Contains(frame.Id)) return;` guard before sink emit.

`ReplayViewModel` CanIdFilterText parser:
- Trim input
- Split on `,` or whitespace
- Per token: `TryParse` with `NumberStyles.HexNumber` if starts with `0x`/`0X`, else `NumberStyles.Integer`
- Collect valid IDs into `HashSet<uint>`
- Assign to `IReplayService.CanIdFilter`
- Set `CanIdFilterError` string (UI inline error) for invalid tokens
- Empty string → `CanIdFilter = null` (全通过)

`ReplayView.xaml` additions:
- `<CheckBox Content="Loop" IsChecked="{Binding Loop}" />`
- `<TextBox Text="{Binding CanIdFilterText, UpdateSourceTrigger=PropertyChanged}" />`
- `<TextBlock Text="{Binding CanIdFilterError}" Foreground="Red" />`

## DI 注册（修改 `AppHostBuilder.cs`）

```csharp
// v1.5.0 MINOR Channel picker
builder.Services.AddSingleton<PeakCan.Host.Core.Channel.IChannelEnumerator,
                              PeakCan.Host.Infrastructure.Channel.PeakCanChannelEnumerator>();
builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.ChannelPickerViewModel>();

// v1.5.0 MINOR Replay follow-up
// IReplayService already registered as singleton (v1.4.0) — extended interface, no new registration
// IReplayFrameSink already registered (v1.4.0) — unchanged

// v1.5.0 MINOR Path normalization
// No DI — static helper class
```

**Integration test** (`AppHostBuilderTests.Build_Registers_ChannelPickerServices`): verify `IChannelEnumerator` + `ChannelPickerViewModel` resolve + `ChannelPickerViewModel` is singleton.

## 测试策略

### Core.Tests (target +18)

**PathNormalizer** (6 tests):
- `Normalize_AbsolutePath_ReturnsCanonical`
- `Normalize_RelativePath_Throws`
- `Normalize_PathWithTraversalSegment_Throws`
- `Normalize_PathWithNullByte_Throws`
- `Normalize_NullOrEmpty_Throws`
- `Normalize_AllowsForwardSlashes` (Windows accepts both)

**PeakCanChannelEnumerator** (4 tests, using `FakeChannelEnumerator`):
- `Enumerate_ReturnsListFromUnderlyingApi` (mock native)
- `Enumerate_NativeCallFails_ReturnsEmptyAndLogs`
- `Enumerate_Disposable_ReleasesNativeHandle`

**ReplayTimeline** (6 tests, extend existing test class):
- `OnTick_ReachesEnd_LoopTrue_RestartsAtZero`
- `OnTick_ReachesEnd_LoopFalse_RaisesPlaybackEnded`
- `EmitFrame_CanIdFilterNull_PassesAll`
- `EmitFrame_CanIdFilterSet_OnlyMatchingIds`
- `EmitFrame_CanIdFilterSet_EmptySet_PassesNone`
- `EmitFrame_CanIdFilterChangedAtRuntime_TakesEffectImmediately`

**IReplayService extension** (2 tests, extend existing test class):
- `SetLoop_True_PropagatesToTimeline`
- `SetCanIdFilter_UpdatesService`

### App.Tests (target +4)

**ChannelPickerViewModel** (3 tests):
- `Ctor_PopulatesAvailableChannels_FromEnumerator`
- `SelectedChannel_PersistsToConfiguration`
- `SelectedChannel_RaisesChannelChanged`

**ReplayViewModel** (3 tests, extend existing):
- `CanIdFilterText_Empty_ClearsFilter`
- `CanIdFilterText_ValidHexAndDecimal_ParsesToSet`
- `CanIdFilterText_InvalidToken_ShowsErrorKeepsPriorFilter`

### Test count projection

| Suite | v1.4.1 baseline | **v1.5.0 target** | Δ |
|-------|-----------------|---------------------|---|
| Core.Tests | 299 + 1 SKIP | **317 + 1 SKIP** | +18 pass (SKIP unchanged) |
| Infra.Tests | 84 + 2 SKIP | **84 + 2 SKIP** | unchanged |
| App.Tests | 357 + 4 SKIP | **361 + 4 SKIP** | +4 pass (SKIP unchanged) |
| **Total** | 740 + 7 SKIP + 0 fail | **762 + 7 SKIP + 0 fail** | **+22 net pass** |

Note: v1.4.1 SetSeed SKIPPED test is **not** re-enabled in v1.5.0 — it still requires the v1.4.2 PATCH SetSeed fix (carry-over, Product decision pending). The 1 SKIP on Core.Tests stays.

## Pre-flight risk assessment

| Risk | Probability | Impact | Mitigation |
|------|-------------|--------|------------|
| `PCANBasic` enumerator 不可测（native call） | HIGH | MED | `IChannelEnumerator` interface + 真实实现 + 测试用 `FakeChannelEnumerator`（per v1.4.0 `DbcService` test seam 模式 — `DbcViewModelTests.cs:70-71`） |
| Path normalization breaking existing configs | LOW | HIGH | 4 call sites 全是 absolute path 输入，normalize 不会改变 absolute path 的结果；6 个 test 覆盖边界 case |
| Replay Loop 边界 race（timer 触发 + pause 同时到达末尾） | MED | LOW | 复用 v1.4.0 `_stateLock` 模式（`ReplayTimeline.cs:_stateLock`）；新增测试 `OnTick_LoopAtPauseBoundary_HandlesRace` |
| CanIdFilter hot-swap（播放中改 filter）丢帧或双发 | LOW | MED | Filter check 紧贴 emit 之前；同一 tick 内 filter change 立即生效。测试 `EmitFrame_CanIdFilterChangedAtRuntime_TakesEffectImmediately` |
| Channel picker ComboBox 启动时为空（无 PEAK 设备） | MED | LOW | ComboBox IsEnabled 绑 `AvailableChannels.Count > 0`；空时显示 hint "No PCAN-USB FD detected" |
| `appsettings.json` schema 兼容性（旧版无 `Channel` 节点） | MED | LOW | `IConfiguration.GetSection("Channel:SelectedHandle")` returns null if missing; ViewModel 退到 "first available" |
| Replay 1ms timer 在 CanIdFilter 全部不匹配时 busy-loop | LOW | LOW | 现有 timeline tick 是按 `_frameIndex` 推进；filter 全部不匹配 = 跳到下一帧（不重）；burst 风险 LOW |

## Breaking changes

**无**. 所有 v1.5.0 范围内变更，additive only. 关键 invariants：

- `IReplayService` 扩展是 additive（新增 1 property + 1 property + 1 event）
- `IChannelEnumerator` 是 NEW interface，旧 caller 不受影响
- `PathNormalizer` 是 NEW static class，4 call sites 是内部更新
- `ChannelPickerViewModel` 是 NEW class
- `ChannelPickerView` 是 NEW UserControl，集成到 `MainView` header
- `appsettings.json` 节点 `Channel:SelectedHandle` 是 OPTIONAL（缺省 null）
- 现有 0 个 public API removal

## Process lessons to apply (from v1.4.0 / v1.4.1 / earlier)

1. **TDD test discrimination (11-of-11 confirmed in v1.4.0)**: RED test must FAIL against unfixed code AND PASS against fixed code. Per-task implementers verify.
2. **Brief-vs-source drift is structural (10-of-10+ confirmed)**: Phase 2.5 actual-code exploration is non-negotiable. Per-task implementers re-read actual source before writing code. Catches: namespace/member renames, ctor signatures, property names (v1.4.0: `DbcService.Current` not `LoadedDocument`, `CanFrame` 5-arg ctor, etc.).
3. **Round-trip tests (v1.4.0 lesson)**: `Encode → Decode → equals original` for symmetric operations. Not applicable to v1.5.0 (no symmetric encode/decode) but apply if any "set + read back" pattern arises.
4. **End-to-end producer/consumer tests (v1.4.0 lesson)**: Brief fixtures may not match actual producer output. v1.5.0: Channel enumerator test must use realistic `PCANBasic` handle format (e.g. `PCAN_USBBUS1`).
5. **`Func<Task>` wrapper for `FluentAssertions.ThrowAsync<T>()`**: Required for `Task`-returning calls. Verify per-task.
6. **STA-WPF test discipline**: App.Tests use NSubstitute mocks; no actual WPF control instantiation. Avoids multi-STA-test `Application.Current` singleton collision.
7. **Real-services-for-state / mocks-for-interfaces split**: `DbcService` (no paramless ctor) → real + `SetCurrentForTests`. `IChannelEnumerator` (interface) → NSubstitute mock. Same pattern.
8. **Threading for cross-thread events**: `ChannelChanged` event fires on UI thread (ViewModel property setter); `PlaybackEnded` event fires on timer thread — UI must marshal. Apply v1.4.0 `SynchronizationContext` capture pattern if needed.
9. **CA1848 + CA2012 source-gen pattern**: Project has `TreatWarningsAsErrors=true` + `AnalysisMode=Recommended`. `partial class` + `[LoggerMessage]` source-gen + extracted helper with CA2012 suppression. Per project pattern.
10. **Phase 2.5 in spec design**: This spec embeds Phase 2.5 actual-code findings table (per v1.4.0 spec pattern). Implementers MUST re-read source at task-execution time regardless.
11. **Ship pattern**: `git push -u origin feature/v1-X-Y-minor` (proxy 127.0.0.1:7897 if direct times out) → PR → `gh pr merge --squash --delete-branch` (after `git fetch origin main`) → `git fetch origin main` + `git reset --hard origin/main` → `git tag v1.X.Y` → `git push origin v1.X.Y` → `gh release create v1.X.Y --notes-file docs/release-notes-v1.X.Y.md`.

## 关键文件

### 新增

- `src/PeakCan.Host.Core/PathNormalizer.cs` (static helper)
- `src/PeakCan.Host.Core/PathNormalizationException.cs` (exception type)
- `src/PeakCan.Host.Core/Channel/IChannelEnumerator.cs` (interface)
- `src/PeakCan.Host.Core/Channel/ChannelDescriptor.cs` (record)
- `src/PeakCan.Host.Infrastructure/Channel/PeakCanChannelEnumerator.cs` (native impl)
- `src/PeakCan.Host.App/ViewModels/ChannelPickerViewModel.cs`
- `src/PeakCan.Host.App/Views/ChannelPickerView.xaml(.cs)`

### 修改

- `src/PeakCan.Host.App/Services/DbcService.cs` (add `PathNormalizer.Normalize` call)
- `src/PeakCan.Host.Core/Uds/Database/DidDatabase.cs` (add `PathNormalizer.Normalize` call)
- `src/PeakCan.Host.Core/Uds/Database/RoutineDatabase.cs` (add `PathNormalizer.Normalize` call)
- `src/PeakCan.Host.Core/Replay/ReplayService.cs` (add `PathNormalizer.Normalize` call)
- `src/PeakCan.Host.Core/Replay/IReplayService.cs` (add Loop + CanIdFilter + PlaybackEnded)
- `src/PeakCan.Host.Core/Replay/ReplayTimeline.cs` (loop boundary + filter check)
- `src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs` (Loop + CanIdFilterText + parse)
- `src/PeakCan.Host.App/Views/ReplayView.xaml` (Loop CheckBox + CanIdFilter TextBox)
- `src/PeakCan.Host.App/Views/MainView.xaml` (host `ChannelPickerView` in header)
- `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` (register `IChannelEnumerator` + `ChannelPickerViewModel`)
- `appsettings.json` (add `Channel:SelectedHandle` node, optional)
- `Directory.Build.props` (version 1.4.1 → 1.5.0)
- `docs/release-notes-v1.5.0.md` (new)

## ADR (Architecture Decision Records)

### ADR-1: Path normalization is defense-in-depth, not sandbox

**Context**: 4 file IO call sites accept unvalidated user input paths. Path traversal is a known attack vector.

**Decision**: Reject `..` segments, null bytes, relative paths. Do NOT restrict to root directories (deferred to v1.6.0).

**Consequences**:
- (+) Simple, low regression risk. 4 call sites already pass absolute paths; `Path.GetFullPath` is idempotent.
- (+) Closes 90% of traversal attack vectors (e.g. `../../../etc/passwd`).
- (-) Doesn't protect against maliciously constructed absolute paths within allowed roots. v1.6.0 will add root allowlist.

### ADR-2: Channel enumeration testable via interface + fake

**Context**: `PCANBasic` is a native Windows DLL. Direct test calls fail on non-Windows CI (Linux containers).

**Decision**: `IChannelEnumerator` interface in Core + native impl in Infrastructure. Tests use NSubstitute mock or `FakeChannelEnumerator` (per v1.4.0 `DbcService` test-seam pattern).

**Consequences**:
- (+) Tests run on all platforms.
- (+) Real `PCANBasic` call isolated to single class, easy to mock.
- (-) Production code path untested by unit tests (mitigated by manual integration test on Windows).

### ADR-3: Replay CanIdFilter is `IReadOnlySet<uint>?`, null = all pass

**Context**: User can specify which CAN IDs to replay. Empty filter is a real use case ("play nothing").

**Decision**: `IReadOnlySet<uint>? CanIdFilter`. `null` = all frames pass. Empty `HashSet` = no frames pass.

**Consequences**:
- (+) Tri-state semantics (all / filtered / none) explicit.
- (+) `HashSet<uint>` O(1) lookup, easy to UI bind (`IReadOnlySet` view).
- (-) Caller must distinguish null vs empty. Documented in `IReplayService` XML doc.
