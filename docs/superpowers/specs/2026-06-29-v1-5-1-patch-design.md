# v1.5.1 PATCH — Replay time-range filter + Periodic DBC send + remove stale [Obsolete] SendCount

**Date:** 2026-06-29
**Branch:** `feature/v1-5-1-patch` (cut from main @ v1.4.2 `a77191a`)
**Target version:** v1.5.1 (PATCH)
**Spec owner:** main agent
**Reviewer:** code-reviewer subagent (pre-ship)
**Pre-flight:** Phase 2.5 actual code exploration (3 parallel Explore agents — Replay surface, Periodic DBC surface, gap analysis + v1.5.1 baseline)

## 概述

v1.5.1 是一个 3 项 PATCH（1 项 user-facing MEDIUM + 1 项 user-facing MEDIUM + 1 项 LOW cleanup），关闭 v1.5.0 release notes §"Known follow-ups" + v1.4.2 PATCH release notes §"Known follow-ups" 共同列出的 2 项 carry-over（Replay time-range filter + Periodic DBC send），外加 1 项 pre-existing defect cleanup（`ICyclicSendService.SendCount` 的 `[Obsolete]` deadline 写 "remove in v1.2.13"，已过期 12 个 release）。

| # | Item | User-facing | Severity |
|---|------|-------------|----------|
| 1 | **Replay time-range filter** — `StartTimestamp?` / `EndTimestamp?` on `IReplayService`, enforced at `ReplayTimeline.OnTick` iteration boundary (NOT emit boundary) | Yes (UI: 2 TextBox + inline error) | MEDIUM |
| 2 | **Periodic DBC send** — new `ICyclicDbcSendService` + `CyclicDbcSendService` (independent of `CyclicSendService`); mirrors timer + generation + lock pattern | Yes (UI: Start/Stop/Interval/counters in DBC mode Expander) | MEDIUM |
| 3 | **Remove stale `[Obsolete] SendCount`** on `ICyclicSendService` + `CyclicSendService` (deadline "v1.2.13" stale 12 releases) | No (compile-time only) | LOW |

MINOR 纪律保持（additive only — except Item 3's removal of a `[Obsolete]`-marked symbol, which is by definition allowed since the attribute is the project's signal that the symbol is safe to remove in a subsequent PATCH/MINOR）。

## 起源

| Source | Item | Severity |
|--------|------|----------|
| v1.5.0 release notes line 58 ("Known follow-ups") | MEDIUM — Replay time-range filter (start/end timestamps) | MEDIUM |
| v1.5.0 release notes line 58 ("Known follow-ups") | MEDIUM — Periodic DBC send (CyclicSendService integration; memory v1.2.12 lesson 4 known transient flakes) | MEDIUM |
| v1.4.2 PATCH release notes line 127-129 (verbatim carry-over from v1.5.0) | Same 2 items re-listed | (carry-over) |
| v1.5.0 design spec line 60-61 + line 30 | Explicitly deferred "v1.5.1 PATCH" anchors | (spec-time deferral) |
| Gap analysis Phase 2.5 §8 MEDIUM candidates | LOW — `[Obsolete] SendCount` deadline stale by 12 releases (ICyclicSendService.cs:21, CyclicSendService.cs:56) | LOW |

### Decomposition context (carry-over chain)

v1.5.1 PATCH closes v1.5.0 MINOR + v1.4.2 PATCH 共同列出的 2 项 user-facing carry-overs。v1.6.0 MINOR 仍 deferred：V8 sandbox + CanApi rate limit + DBC size/token limit + path normalization root restriction + OEM `IKeyDerivationAlgorithm` concrete impl。

### Brief drift history（项目 12-of-12+ 记录）

User brief "Replay time-range + Periodic DBC" 与 source code 存在两层 drift：

1. **"Replay time-range" 在 4 个文档里有 2 个解读**（Phase 2.5 Gap analysis §1 详查）：
   - v1.5.0 design spec line 60: "start/end timestamps"（true window filter，**未 ship**）
   - v1.5.0 release notes line 116 + v1.4.2 design line 549 + v1.4.2 release notes line 128: "post-EOF scrubbing to arbitrary timestamp"（**已通过 `IReplayService.Seek` + Slider ship**）
   - **本 spec 采用 v1.5.0 design spec 的 "start/end timestamps" 解读**——理由：(a) v1.5.0 design spec 是最早/最权威 spec；(b) 即使 scrub 已 ship，true range filter 仍有意义（cursor 跟随 window 边界，而不是 emit 跟随）；(c) 用户的 brief 是 v1.5.0 MINOR ship 后的继续，自然继承 design spec 的语义。

2. **"Periodic DBC send" 准确**——`DbcSendViewModel.SendAsync` (line 130-163) 是 one-shot only；grep 全仓 0 匹配 `CyclicDbc` / `PeriodicDbc` / `DbcSendTimer` / `StartWithDbc`。

## Product decision (state, do NOT re-litigate)

**"Range filter 在 timeline 迭代边界 enforce，不在 emit 边界 enforce"**。Range 是 time-window（语义：cursor 跳过 window 外的帧），不是 content filter（语义：cursor 不跳过，emit 仅丢弃）。这一区分决定了 `CurrentTimestamp` 是否跟随 cursor。

**Rationale**:
- v1.5.0 已 ship 的 `CanIdFilter` 是 content filter，在 `ReplayService.EmitFrame` (line 117-121) emit 边界 enforce——cursor 走过 filtered frame，timestamp 跟随。
- Time-range 是 time-domain filter，语义不同：cursor 应该跳过 window 外（用户预期 "skip from 5s to 10s"，不是 "emit 5-10s 但 cursor 仍走完整 timeline"）。
- 在 `ReplayTimeline.OnTick` while-loop predicate 内（line 163）增加 range check，与现有 `frame.Timestamp <= now` 复合；emit 边界保持纯净（无 range check）。

**Rejected alternative**: "Range 在 emit 边界 enforce"——会与 CanIdFilter 行为混淆，cursor 跳过 window 时 timestamp 不跟随，scrubber UX 不一致（用户拖 slider 到 7s 时若 range 是 [5,10]，预期 cursor 在 7s 但 emit 跳过；不是预期 cursor 跳到 10s）。

**"Periodic DBC 是独立 service，不扩展 CyclicSendService"**。

**Rationale**:
- `CyclicSendService` 有 known transient-flaky race test history（memory v1.2.12 lesson 4，每个 release notes 都在提及）；最近一次 race fix 在 v1.2.12 PATCH Item 10。
- 扩展 `CyclicSendService` 引入 DBC encode 责任，会污染已 frozen 的 v1.2.12 race-fix invariants（lock + generation + stale-timer drop）。
- 独立 service 独立 race-test，不互相影响。
- DI topology 干净：`DbcEncodeService` 是 DI singleton，`CyclicDbcSendService` 可直接依赖。
- UI 镜像：`CyclicSendService` 在 `CyclicExpander` (SendView.xaml:42-61)；新 `CyclicDbcSendService` 在 `DbcModeExpander` (SendView.xaml:109-133) 内的 sibling StackPanel。

**Rejected alternatives**:
- Option B (`Func<CanFrame>` overload on `CyclicSendService`)：把 encode 责任推到 VM，thread-safety 转坏；encode exception 不能被 `_sendService.SendAsync` 的 try/catch 捕获；违反 Interface Segregation。
- Option C (`StartWithDbc(Message, Func<DbcSignalValues>, TimeSpan)` on `CyclicSendService`)：force `DbcEncodeService` 注入到 `CyclicSendService`，service 变 kitchen sink；两种 failure mode 混在同一 counter pair。

**"Stale [Obsolete] SendCount 清理窗口就是现在"**。

**Rationale**:
- `ICyclicSendService.SendCount` (line 21) + `CyclicSendService.SendCount` (line 56) 标 `"remove in v1.2.13"`，v1.2.13 已 ship；deadline 过期 12 个 release。
- v1.5.1 PATCH 动 Periodic DBC 设计讨论时，触发 `CyclicSendService` 周边文件回顾——自然窗口清理。
- 唯一 consumer 是 `SendViewModel.cs:57`（一行表达式）和测试——1-2 行更新。
- 项目惯例：`[Obsolete]` attribute 是项目的 "safe to remove" signal，PATCH 纪律允许 remove 已标记 symbol。

**Rejected alternative**: "保留到未来 cleanup PATCH"——已推迟 12 release 仍没做，且每次 release notes 都 mention；项目 PATCH 周期短（平均 1-2 天），不必 split。

## Phase 2.5 actual code exploration findings

实际读 v1.4.2 PATCH shipped 代码（main @ `a77191a`）确认 brief 描述准确 + 锁定关键 design points：

### Item 1 — Replay time-range filter

| Assumption | Phase 2.5 actual |
|---|---|
| `IReplayService` 已有 `Loop` (v1.5.0) + `CanIdFilter` (v1.5.0) 两个 filter，没有 `StartTimestamp`/`EndTimestamp`/`TimeRange` 任何 type | **确认**。`IReplayService.cs` line 32 + line 43. Grep 全仓 0 匹配 `StartTimestamp` / `EndTimestamp` / `TimeRange` / `TimeWindow` / `RangeStart` / `RangeEnd` / `WindowStart`。唯一 `_playStartTimestamp` 是 ReplayTimeline 的 play-start anchor（line 25, 83, 104, 122, 136, 151, 179），不是 range。 |
| `ReplayTimeline.OnTick` emit predicate 是 `_frames[_nextFrameIndex].Timestamp <= now` (line 163)，没有 upper/lower bound | **确认**。Line 163-169 是 while-loop emit；line 173-187 是 EOF branch（Loop rewind or stop + raise PlaybackEnded）。 |
| `CanIdFilter` 当前在 `ReplayService.EmitFrame` (line 117-121) enforce，不在 `ReplayFrameSinkAdapter` | **确认**（brief drift 修正）。Adapter (`ReplayFrameSinkAdapter.cs`) 是 pure pass-through to `SendService.SendAsync`，不知道 filter 存在。CanIdFilter 检查在 `ReplayService.EmitFrame` filter-drop 路径（line 117-121）**之前** `_sink.SendFrameAsync` 调用（line 131）。 |
| `ReplayFrame.Timestamp` 是 seconds from recording start（file-relative） | **确认**。`ReplayFrame.cs:5` doc comment + `AscParser.cs:151` `double.TryParse(tokens[0])` — 解析 ASC line 的第一列。Frames sorted ascending by `AscParser.cs:131`。`TotalDuration` / `CurrentTimestamp` 也用同一 file-relative 单位。无单位转换。 |
| `Loop` rewind (`OnTick` line 175-181) 重置 cursor 到 0，与 range filter 需要重新协调 | **确认**。Line 175-181: `_nextFrameIndex=0, _currentTimestamp=0.0, _playStartTimestamp=0.0, _playStartWallClock=UtcNow`。若 `StartTimestamp > 0`，rewind 后 cursor 从 0 走到 `StartTimestamp`，range predicate 在 while-loop 复合后跳过 window 外 frames，`_currentTimestamp` 跟随最近走过 frame。 |
| `Seek(timestamp)` (line 99-113) 当前在 range 内 advance `_nextFrameIndex` 到 `Timestamp >= target` | **确认**。Line 108-111: linear scan。`Seek` 不 enforce range filter（cursor move，不是 emit）。 |
| `IReplayService` 当前 sink-throw tolerance 在 `ReplayTimeline.OnTick` (line 199-205) 通过 `_sinkException` field 捕获 first-failure | **确认**。v1.4.2 PATCH Item 3 刚 ship：`ReplayedFirstFailure` 在 `OnTick` foreach catch (line 199-205) → `_onSinkThrew?.Invoke(ex)` → `ReplayService.OnSinkThrewFromTimeline` (line 70-78) → raise `PlaybackEnded` with `Error`。本 spec 不动这条 path。 |
| `PlaybackEndedEventArgs.Error` 当前承载 sink exception；"ended normally" vs "ended with error" 区分通过 `Error is null` vs not null | **确认**。`PlaybackEndedEventArgs.cs:15`。Range 排除所有 frames → cursor walk 到 EOF → raise PlaybackEnded with `Error = null`（normal EOF）。无需新 event arg shape。 |
| ReplayVM 用 `[ObservableProperty] _loop` + `OnLoopChanged` partial method (line 79-80, 126-132) 写穿到 `_service.Loop` | **确认**。Mirror 此 pattern 给 `StartTimestamp` / `EndTimestamp` VM properties。 |
| ReplayVM 的 `CanIdFilterText` parser (line 86-93, 147-223) 是 free-form text (hex/decimal mix), error via `CanIdFilterError` | **确认**。Mirror 此 pattern 给 `StartTimestampText` / `EndTimestampText` + 单一 `RangeFilterError`。两个 text box 共享一个 error property（"Start > End"）。 |

### Item 2 — Periodic DBC send

| Assumption | Phase 2.5 actual |
|---|---|
| `CyclicSendService` 当前只接受 raw `CanFrame` (line 82 `Start(CanFrame, TimeSpan)`)，不接 DBC | **确认**。Grep `ICyclicDbcSendService` / `CyclicDbc` / `PeriodicDbc` / `StartWithDbc` / `DbcSendTimer` 全仓 0 匹配。 |
| `DbcEncodeService.Encode(Message, IReadOnlyDictionary<string, double>)` 是 DI singleton (`AppHostBuilder.cs:200`) | **确认**。`DbcEncodeService.cs:24-81` 是 stateless。 |
| `DbcSendViewModel.SendAsync` (line 130-163) 是 one-shot only，构建 `Dictionary<string, double>` from `SignalRows` (line 137-141) → `_encoder.Encode(msg, values)` (line 142) → `_sendService.SendAsync(frame)` | **确认**。Periodic version 复用此 encode pipeline，但 value provider 从 `Func<IReadOnlyDictionary<string, double>>` 动态读取（每 tick 反映用户编辑）。 |
| `SendService` 住在 App 层 (`src/PeakCan.Host.App/Services/SendService.cs:5`)，不是 Core 层 | **确认**。`SendService.SendAsync` (line 73) 是 `virtual`，允许 test subclass override（`CountingSendService` / `FakeSendService` precedent）。新 `CyclicDbcSendService` 也在 App 层，与 `CyclicSendService` 同 namespace。 |
| `CyclicSendService` 已知 transient-flaky race test（memory v1.2.12 lesson 4） | **确认**。`CyclicSendServiceRaceTests.cs` (153 lines, 5 tests) 有 transient flake；最近一次 race fix v1.2.12 PATCH Item 10 split counters + generation + stale-timer drop。新 service 必须独立 race-test，复用同一 pattern 但不共享代码（PATCH 范围控制，避免抽出 base/helper 引入 scope creep）。 |
| Signal values 在 `DbcSignalRowViewModel._value` (line 177) mutable post-load；用户可在 periodic send 运行时编辑 | **确认**。`Func<IReadOnlyDictionary<string, double>>` 在每次 tick 重新读取，自然反映用户编辑。 |
| `DbcModeExpander` (SendView.xaml:109-133) 当前只有 Message combo + SignalRows DataGrid + one-shot Send button | **确认**。新 periodic UI 加 sibling StackPanel（mirror `CyclicExpander` pattern at lines 42-61）。 |
| `SelectedDbcMessage` change (line 110-118) 清空 + 重建 `SignalRows`；用户切换 message 时旧 signal values 丢失 | **确认**。Periodic send 应 capture `Message` reference by ref；若 tick 时 `_currentMessageId != capturedMessageId` → stop + increment failure（"message changed mid-cyclic-send" leak）。 |

### Item 3 — Remove stale [Obsolete] SendCount

| Assumption | Phase 2.5 actual |
|---|---|
| `ICyclicSendService.SendCount` (line 21-22) 是 `[Obsolete("Use SuccessCount + FailureCount. v1.2.12 split the mixed counter; remove in v1.2.13.")]` | **确认**。Deadline "v1.2.13" stale。 |
| `CyclicSendService.SendCount` (line 56-57) 是同 `[Obsolete]` | **确认**。Implementation: `Interlocked.Read(ref _sendSuccessCount) + Interlocked.Read(ref _sendFailureCount)`。 |
| 唯一 consumer 是 `SendViewModel.cs:57` (`[ObservableProperty] SendCount` + `OnSendCountChanged`) | **确认**。需 grep + 更新 `SendViewModel.cs`。XAML binding 在 `SendView.xaml:42-61` CyclicExpander 用 `SendCount` 显旧—— v1.5.0 已 ship `SuccessCount` + `FailureCount` 替代；当前 XAML 已用新 counters（grep 验证 line 56-60）。`SendCount` binding 可能残留——待 Phase 2.5 grep 验证。 |
| `CyclicSendServiceTests.cs` / `CyclicSendServiceRaceTests.cs` 不引用 `SendCount` | **确认**。grep `SendCount` 在 tests/ 下零匹配（v1.2.12 PATCH Item 10 已切走）。 |

### Brief drift cautions (memory `phase-2-5-brief-drift-correction` 12-of-12+)

1. **"Replay time-range" 文档歧义**：4 文档 2 解读。本 spec 采用 v1.5.0 design spec "start/end timestamps" 解读；不要按 "post-EOF scrubbing" 误读（已 ship via Seek+Slider）。Brief reader 看到 "scrubbing" 字眼应警觉——以本 spec 为 ground truth。
2. **CanIdFilter 位置**：在 `ReplayService.EmitFrame` 不在 `ReplayFrameSinkAdapter`。Range filter **不**应 mirror 到 adapter（adapter 是 pure pass-through）。Range 在 `ReplayTimeline.OnTick` 复合，与 emit boundary 解耦。
3. **`ReplayState.Stopped` 含义**：cover "no file loaded" AND "explicitly stopped"（`ReplayViewModel.cs:27-30` comment）。Range 清空时机不是 `State==Stopped`，是 `OpenAsync` 成功 load 后（新 file 的 timestamps 范围不同，old bounds 可能 out-of-range）。
4. **`Message.Id` 含 IDE bit**：Extended frame bit 31 set。DbcSendViewModel.cs:147-150 有 bit-31 check；新 `CyclicDbcSendService` per-tick encode 后构造 `CanFrame` 必须同样检查。
5. **`SendService` 在 App 层**：不是 Core。新 `CyclicDbcSendService` 在 App 层（与 `CyclicSendService` 同 namespace）；DI 拓扑 + test 模式（virtual override for test seam）与 `CyclicSendService` 一致。
6. **`[Obsolete] SendCount` 移除后 `SendViewModel.cs:57` 必须同步更新**：1 行 `[ObservableProperty]` 删除 + 1 行 `OnSendCountChanged` partial method 删除。XAML `SendCount` 引用若残留也需清理（待 grep 验证）。
7. **`CyclicSendServiceRaceTests` transient-flaky caveat**：新 `CyclicDbcSendServiceRaceTests` 必须同样在 PR description 注明 "CI re-run 3× 已知 flaky"；plan 必须包含 race-regression 测试套（与 `CyclicSendServiceRaceTests.cs:153` lines 1:1 mirror）。
8. **`UdsSecurity`/`UdsClient`/`UdsSession` 等 UDS 文件不动**：本 PATCH 范围严格限于 Replay + CyclicSend + DbcSend 周边。**DO NOT** scope-creep 进 UDS carry-over。
9. **`PathNormalizer` defense-in-depth（v1.5.0 ADR-1）**：新 file 如果涉及路径处理，必须用 `PathNormalizer.Normalize`，不引入新 IO call site bypass。Item 2 不动路径（`Message` + `Dictionary` 入参，无 IO），自然合规。

## Scope

3 PATCH items = 2 user-facing MEDIUM (1 Replay surface + 1 新 DBC service) + 1 LOW cleanup。

| # | Item | 组件 | 工作 | Source | Severity |
|---|------|------|------|--------|----------|
| 1 | **Replay time-range filter** | Core: `IReplayService.cs` (+2 props), `ReplayService.cs` (+2 props proxy), `ReplayTimeline.cs` (+2 fields + props + OnTick while-loop predicate composite) / App: `ReplayViewModel.cs` (+2 ObservableProperty + 2 OnXxxChanged + validation + OpenAsync clear), `ReplayView.xaml` (+Row 5 with 2 TextBox + error TextBlock) / Tests: `IReplayServiceTests.cs` (+5), `ReplayTimelineTests.cs` (+9), `ReplayViewModelTests.cs` (+4) | Add `double? StartTimestamp`/`double? EndTimestamp` to `IReplayService`; enforce in `ReplayTimeline.OnTick`; validate `Start <= End` in VM setter. | v1.5.0 design spec line 60 + v1.5.0/v1.4.2 release notes | MEDIUM |
| 2 | **Periodic DBC send** | App: new `ICyclicDbcSendService.cs` (~30 LOC), new `CyclicDbcSendService.cs` (~150 LOC, mirrors CyclicSendService timer+gen+lock pattern), `AppHostBuilder.cs` (+2 DI lines), `DbcSendViewModel.cs` (+2 ObservableProperty + 2 RelayCommand + Func<IReadOnlyDictionary<string,double>> value provider), `SendView.xaml` (DBC mode Expander +sibling StackPanel) / Tests: new `CyclicDbcSendServiceTests.cs` (~120 LOC) + new `CyclicDbcSendServiceRaceTests.cs` (~150 LOC) + new `DbcSendViewModelCyclicTests.cs` (~80 LOC) | New service independent of `CyclicSendService`; per-tick encode via `DbcEncodeService` then send via `SendService`; capture `Message` ref by id-check on each tick (auto-stop if changed). | v1.5.0 release notes line 58 + v1.4.2 carry-over | MEDIUM |
| 3 | **Remove stale `[Obsolete] SendCount`** | App: `ICyclicSendService.cs` (-2 lines), `CyclicSendService.cs` (-2 lines), `SendViewModel.cs` (delete `[ObservableProperty] SendCount` line 57 + `OnSendCountChanged` partial method body) + grep+update XAML if any `SendCount` binding残留 | Remove attribute + property/impl + consumer. Project convention: `[Obsolete]` = safe to remove in PATCH. | Gap analysis §8 MEDIUM candidate | LOW |

## Non-Goals

- **v1.6.0 MINOR 全部 carry-overs**：V8 sandbox hardening + CanApi rate limit + DBC size/token limit + path normalization root restriction + OEM `IKeyDerivationAlgorithm` concrete impl——explicitly deferred。
- **DBC Value table encoding**（text→int via `DbcDocument.ValueTables`）+ **Multiplexed signal groups UI**（auto-show only valid mux）+ **Replay→Trace auto-load**——pre-existing Non-Goals since v1.4.0/release notes carry-overs; defer to future MINOR。
- **Range filter 的 `CanIdFilter` 风格 tri-state enum**——不需要；`(double? Start, double? End)` 足够（null = unbounded，double = bound；no third state has meaning）。
- **`ReplayState` enum 扩展**（加 `RangeFiltered` 等新 state）——不需要；现有 Stopped/Playing/Paused 三态足够，range 是 filter 不是 state。
- **`CyclicSendService` 重构 / 抽 base class `CyclicTimer<T>`**——defer 到未来 refactor PATCH。Option 3 (duplicate ~80 LOC) 用于 v1.5.1 范围控制。
- **Periodic DBC 的 per-tick signal 编辑广播到 UI**——defer；VM 仅暴露 counters + IsRunning，SignalRows 用户编辑自然流转（Func<> 重新读取）。若未来需要 "highlight changed signal" 等 UX，再加。
- **Replay time-range 的 "Save range preset" / "Apply preset to next file"**——超出 scope；用户每次手动设。
- **UDS carry-over / UdsSecurity hardening**——v1.4.2 PATCH 已 ship HIGH (SetSeed preserves lockout)；不动。
- **`ReplayExceptions` 新类型**——本 PATCH 不引入新 exception type（range filter 不抛 exception；用户错误 surface via `RangeFilterError`）。
- **DI 拓扑变更 beyond Item 2 + Item 3 的最小注册**——不重构现有 DI；AppHostBuilder 仅 +2 lines（`CyclicDbcSendService` singleton + interface mapping）。

## 设计决策 (open / proposed)

### Decision 1: Item 1 — Range filter 在 timeline iteration 边界 enforce（不emit 边界）

**选项 A** (推荐): 在 `ReplayTimeline.OnTick` while-loop predicate 复合 `frame.Timestamp >= startOrMin && frame.Timestamp <= endOrMax`（line 163 内）；emit 边界 `ReplayService.EmitFrame` 不动（继续负责 `CanIdFilter`）。

**选项 B**: 在 `ReplayService.EmitFrame` filter-drop 路径（line 117-121 旁）增加 range check。

**决策**: A. 理由: 见 "Product decision" 段。Range 是 time-domain filter，cursor 跟随 window 边界；emit 边界只负责 content filter (`CanIdFilter`)。两 filter 复合但语义独立。

### Decision 2: Item 1 — Range 双 nullable `double? Start, double? End`（不 tri-state enum）

**选项 A** (推荐): `double? StartTimestamp { get; set; }` + `double? EndTimestamp { get; set; }`，每个独立 nullable。

**选项 B**: enum `RangeState { Unbounded, InRange, Empty }` + single property。

**选项 C**: 单一 nullable range struct `TimeRange? Range { get; set; }`。

**决策**: A. 理由: 每个 endpoint 独立 unbounded 最直观；用户心智模型 "start 到 end，可只设一边"。tri-state enum 引入第三状态（"empty"）无意义；TimeRange struct 增加 type surface 但 API 用法变 verbose（`Range = new TimeRange(0, 10)` vs `StartTimestamp = 0, EndTimestamp = 10`）。

### Decision 3: Item 1 — Boundary inclusive `[Start, End]`

**选项 A** (推荐): 闭区间，两端 inclusive。`frame.Timestamp >= start && frame.Timestamp <= end`。

**选项 B**: 半开 `[Start, End)`，end exclusive。

**决策**: A. 理由: (a) 现有 emit predicate `_frames[i].Timestamp <= now` 是 left-inclusive，新 range 复合 `&& Timestamp >= start` 后两个 inclusive，symmetry 一致；(b) half-open 在 edge case 静默丢 frame at exact boundary（用户预期 inclusive）；(c) 项目惯例（`Loop` rewind 是 inclusive `i == 0`）也是 inclusive。

### Decision 4: Item 1 — Validation `Start > End` 行为

**选项 A** (推荐): VM setter 在 `OnStartTimestampChanged`/`OnEndTimestampChanged` 检查 `value > currentOtherSide` → 设置 `RangeFilterError = "Start must be ≤ End"`，拒绝 set（保留 prior value）。XAML inline error TextBlock 显示。

**选项 B**: 抛 `ArgumentException`。

**决策**: A. 理由: 与 `CanIdFilterText` pattern (line 216-271) 一致——inline error + keep prior filter。`ArgumentException` 抛出在 WPF two-way binding 路径会变 unhandled exception dialog，UX 差。

### Decision 5: Item 1 — `OpenAsync` 成功 load 后清空 range

**选项 A** (推荐): `DbcSendViewModel.OpenAsync` 不适用；`ReplayViewModel.OpenAsync` (line 234-257) 在 `LoadAsync` 成功后清空 `StartTimestamp = null, EndTimestamp = null, RangeFilterError = null`。

**选项 B**: 不清空；用户重设。

**决策**: A. 理由: 新 file 的 timestamps 范围不同（旧 file 总 duration 60s，新 file 5s，旧 `EndTimestamp = 60` 永远 out-of-range）。清空是 safer default；用户可重设。`CanIdFilter` 当前不清空（v1.5.0 spec decision，ID 集合跨文件仍 valid）——range 与 ID 不同，必须清。

### Decision 6: Item 1 — Range 排除所有 frames 时 `PlaybackEnded` 仍 fire

**选项 A** (推荐): range predicate 在 `OnTick` while-loop 内；cursor 走到 EOF（无论 range 内有没有 frame），现有 EOF branch (line 173-187) fire `_onPlaybackEnded` 一次，`Error = null`（normal EOF，VM 当作 clean end 设 `IsPlaying = false`）。

**选项 B**: range 排除所有 frames → 立即 fire PlaybackEnded，不等 cursor 走完。

**决策**: A. 理由: (a) 现有 EOF branch 已经基于 cursor 走到 end 触发，与 emit count 无关；(b) 立即 fire 会破坏 "Play 后 cursor 跟着 wall-clock 走" 的 UX consistency（用户预期 cursor 走完 timeline 后才结束）。`CurrentTimestamp` 跟随 cursor 到 `TotalDuration` 是合理 UX。

### Decision 7: Item 2 — Periodic DBC 为独立 service（不扩展 CyclicSendService）

**选项 A** (推荐): new `ICyclicDbcSendService` + `CyclicDbcSendService`，与 `ICyclicSendService` + `CyclicSendService` 平级。

**选项 B**: extend `ICyclicSendService` with `Func<CanFrame>` overload。

**选项 C**: add `StartWithDbc(Message, Func<DbcSignalValues>, TimeSpan)` to existing service。

**决策**: A. 理由: 见 "Product decision" 段。Risk containment + clean DI topology + UI symmetry + failure-type semantics。

### Decision 8: Item 2 — Value provider `Func<IReadOnlyDictionary<string, double>>`（不 closure-captured dictionary）

**选项 A** (推荐): `Start(Message message, Func<IReadOnlyDictionary<string, double>> valueProvider, TimeSpan interval)`。每 tick invoke provider，dictionary 是 fresh snapshot。

**选项 B**: `Start(Message message, IReadOnlyDictionary<string, double> initialValues, TimeSpan interval)`。Capture once；用户编辑不流入 periodic send。

**选项 C**: `Start(Message message, DbcSendViewModel source, TimeSpan interval)`。Service 直接调 VM。

**决策**: A. 理由: (a) 用户编辑 SignalRows 应自然流转到 periodic send；(b) closure capture 是 stale 问题；(c) VM reference 反向依赖 service 破坏 DI 拓扑。`Func<>` 是 idiomatic .NET 解耦 pattern。

### Decision 9: Item 2 — `Message` reference stale 检测

**选项 A** (推荐): 在 `Start` capture `Message` by ref (`_capturedMessage = message`)；每 tick 在 `lock(_lock)` 内对比 `_capturedMessage` 与 provider current message（如暴露 `Message CurrentMessage { get; }` 由 VM 提供）。若 `provider()` 返回 dictionary 但 message 已变 → stop + log warning + `FailureCount++`（one-time leak）。

**选项 B**: 不检测；user 改 message 时 periodic send 继续用旧 message 编码 → encode failure per tick (`DbcSignalNotFoundException` from missing new signals)，counter 一直 fail。

**决策**: A. 理由: Option B 是 user-hostile silent degradation（counter 一直涨但实际没 emit 正确 frame）；Option A 是 fail-fast + clear log。VM 暴露 `Message CurrentMessage { get; }` 是已有 property (`SelectedDbcMessage`)。

**Refinement**: provider signature 改为 `Func<(Message message, IReadOnlyDictionary<string, double> values)>` —— 一次返回 message + values，避免双 source-of-truth。

### Decision 10: Item 2 — Encode failure 计入 FailureCount（不抛）

**选项 A** (推荐): per-tick try/catch `DbcSignalEncodeException` → `FailureCount++` + log every 100th (`[LoggerMessage]` source-gen partial method)；continue to next tick。

**选项 B**: encode failure → stop + raise some event。

**决策**: A. 理由: 与 `CyclicSendService.SendAsync` 失败行为 mirror (line 154-159 log every 100th failure)；encode 错误通常是 user transient（输入错位、超 range），stop 太 aggressive。Counter pair (`SuccessCount` + `FailureCount`) 让 UI 区分。

### Decision 11: Item 2 — Race-regression test transient-flaky caveat 写入 plan

**选项 A** (推荐): PR description 明文 "CyclicDbcSendServiceRaceTests known transient-flaky; CI re-run 3×"（memory v1.2.12 lesson 4）。Plan §Race regression tests section 显式 callout。

**选项 B**: 加 retry attribute 或 wait helper 自动化 re-run。

**决策**: A. 理由: 项目惯例（`CyclicSendServiceRaceTests.cs` 没 retry attribute）；retry helper 是 scope creep。本 PATCH plan/notes/PR description 三处显式 mention。

### Decision 12: Item 3 — `[Obsolete] SendCount` 移除 + consumer 同步更新

**选项 A** (推荐): 移除 `ICyclicSendService.SendCount` 属性 + `CyclicSendService.SendCount` 实现 + `SendViewModel.cs:57` `[ObservableProperty]` 行 + `OnSendCountChanged` partial method + grep XAML `SendCount` 残留。

**选项 B**: 保留属性 + 只移除 `[Obsolete]` attribute（不再 deprecate 但保留）。

**决策**: A. 理由: `[Obsolete]` 标记本就是 "safe to remove" signal；保留不 deprecate 等于 deferred removal（已 deferred 12 release）。项目 PATCH 纪律允许。

## Architecture / API surface

### Item 1 — Replay time-range API

```csharp
// src/PeakCan.Host.Core/Replay/IReplayService.cs (add after line 43)

/// <summary>
/// Inclusive lower bound on emitted frames' <see cref="ReplayFrame.Timestamp"/>.
/// null = unbounded below. Range filter changes take effect on the next emit
/// (no buffering). Composes with <see cref="CanIdFilter"/> (range filter is
/// applied first at the timeline iteration boundary; CanIdFilter is applied
/// second at the emit boundary). Re-applied after <see cref="Loop"/> rewind.
/// </summary>
double? StartTimestamp { get; set; }

/// <summary>
/// Inclusive upper bound on emitted frames' <see cref="ReplayFrame.Timestamp"/>.
/// null = unbounded above. Same composition + re-application semantics as
/// <see cref="StartTimestamp"/>.
/// </summary>
double? EndTimestamp { get; set; }
```

```csharp
// src/PeakCan.Host.Core/Replay/ReplayTimeline.cs (modify OnTick line 163)
while (_nextFrameIndex < _frames.Count
    && _frames[_nextFrameIndex].Timestamp <= now
    && _frames[_nextFrameIndex].Timestamp >= startOrMin  // NEW
    && _frames[_nextFrameIndex].Timestamp <= endOrMax)   // NEW
{
    // ...emit
}

// Capture startOrMin/endOrMax once at top of OnTick (line 162):
var startOrMin = _startTimestamp ?? double.NegativeInfinity;
var endOrMax   = _endTimestamp   ?? double.PositiveInfinity;
```

```csharp
// src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs (add after line 93)

[ObservableProperty]
private double? _startTimestamp;
partial void OnStartTimestampChanged(double? value)
{
    // validate against EndTimestamp
    if (value.HasValue && EndTimestamp.HasValue && value > EndTimestamp)
    {
        RangeFilterError = "Start must be ≤ End";
        return;
    }
    _service.StartTimestamp = value;
    RangeFilterError = null;
}

[ObservableProperty]
private double? _endTimestamp;
partial void OnEndTimestampChanged(double? value) { /* mirror */ }

[ObservableProperty]
private string? _rangeFilterError;

// In OpenAsync (line 234-257), after successful LoadAsync:
StartTimestamp = null;
EndTimestamp = null;
RangeFilterError = null;
```

### Item 2 — Periodic DBC send API

```csharp
// src/PeakCan.Host.App/Services/ICyclicDbcSendService.cs (NEW)

namespace PeakCan.Host.App.Services;

public interface ICyclicDbcSendService
{
    bool IsRunning { get; }
    long SuccessCount { get; }
    long FailureCount { get; }

    /// <summary>
    /// Start periodic transmission of a DBC message. The <paramref name="frameProvider"/>
    /// is invoked on each tick: returns (Message, signalValues) pair. The captured
    /// Message reference is checked each tick; if it differs from the provider's
    /// current Message, the service stops itself (one-time leak, surfaces via
    /// FailureCount++ + log). signalValues are encoded via DbcEncodeService.Encode
    /// on each tick to reflect user edits in real time.
    /// </summary>
    void Start(
        Func<(Message message, IReadOnlyDictionary<string, double> values)> frameProvider,
        TimeSpan interval);

    void Stop();
}
```

```csharp
// src/PeakCan.Host.App/Services/CyclicDbcSendService.cs (NEW, ~150 LOC)
// Mirrors CyclicSendService timer + generation + lock pattern exactly.
// Dependencies: DbcEncodeService (encode per tick) + SendService (send) + ILogger.
// Source-gen LoggerMessage partials for 4 log sites (started/stopped/failed/threw).
```

```csharp
// src/PeakCan.Host.App/ViewModels/DbcSendViewModel.cs (add after line 47)
[ObservableProperty]
private string? _dbcCyclicIntervalText = "100";  // ms

[ObservableProperty]
private bool _isDbcCyclicRunning;

[ObservableProperty]
private long _dbcCyclicSuccessCount;

[ObservableProperty]
private long _dbcCyclicFailureCount;

[RelayCommand(CanExecute = nameof(CanStartDbcCyclic))]
private void StartDbcCyclic()
{
    if (!SelectedDbcMessage is { } msg) return;
    if (!TimeSpan.TryParse(DbcCyclicIntervalText, out var interval)) return;
    _cyclicDbc.Start(
        () => (SelectedDbcMessage!,
               BuildCurrentSignalValues()),
        interval);
    IsDbcCyclicRunning = true;
}

[RelayCommand(CanExecute = nameof(CanStopDbcCyclic))]
private void StopDbcCyclic()
{
    _cyclicDbc.Stop();
    IsDbcCyclicRunning = false;
}

// In ctor: subscribe to _cyclicDbc.IsRunning / SuccessCount / FailureCount
// via a polling DispatcherTimer (mirror SendViewModel.cs:118-128 pattern).
// Or: PeriodicService exposes events; VM subscribes.

private Dictionary<string, double> BuildCurrentSignalValues()
{
    var values = new Dictionary<string, double>(StringComparer.Ordinal);
    foreach (var row in SignalRows)
        if (row.Value.HasValue) values[row.Signal.Name] = row.Value.Value;
    return values;
}
```

### Item 3 — Remove stale [Obsolete] SendCount

```csharp
// src/PeakCan.Host.App/Services/ICyclicSendService.cs (DELETE lines 20-22)
// [Obsolete("Use SuccessCount + FailureCount. v1.2.12 split the mixed counter; remove in v1.2.13.")]
// long SendCount { get; }

// src/PeakCan.Host.App/Services/CyclicSendService.cs (DELETE lines 55-57)
// [Obsolete("Use SuccessCount + FailureCount. v1.2.12 split the mixed counter; remove in v1.2.13.")]
// public long SendCount => ...

// src/PeakCan.Host.App/ViewModels/SendViewModel.cs (DELETE SendCount ObservableProperty line 57 + OnSendCountChanged partial method body if any)

// src/PeakCan.Host.App/Views/SendView.xaml (grep + verify no SendCount binding残留; v1.5.0 已 ship SuccessCount/FailureCount, but old binding may remain)
```

## Test strategy

### Item 1 — Replay time-range tests

**Service-level** (`IReplayServiceTests.cs`, +5 tests after line 342):
- `SetStartTimestamp_GetterReturnsWhatWasSet`
- `SetEndTimestamp_GetterReturnsWhatWasSet`
- `SetStartTimestamp_PropagatesToInternalTimeline` (reflection into private `_timeline` field, mirror `SetLoop_PropagatesToInternalTimeline` line 237-263 pattern)
- `SetStartTimestamp_Null_MeansUnbounded`
- `SetStartTimestamp_GreaterThanEndTimestamp_ValidationHandledByVm` (service-level doesn't enforce; VM does)

**Timeline-level** (`ReplayTimelineTests.cs`, +9 tests after line 376):
- `OnTick_StartTimestampSet_SkipsFramesBeforeStart`
- `OnTick_EndTimestampSet_SkipsFramesAfterEnd`
- `OnTick_StartAndEndTimestampSet_EmitsOnlyFramesInRange`
- `OnTick_RangeFilter_BoundaryInclusive` (frame at exactly Start AND exactly End both emit)
- `OnTick_RangeFilter_NullMeansUnbounded` (null on one side = no bound on that side)
- `OnTick_RangeFilter_LoopRewindReappliesRange` (after loop rewind, next frame is Start, not frame 0)
- `OnTick_RangeFilter_EmptiesAllFrames_StillRaisesPlaybackEndedOnEof` (range excludes all; EOF branch still fires with Error=null)
- `OnTick_RangeFilter_SeekOutsideRange_EmitsNothingOnPlay` (Seek to t=10 with range [20, 30] emits nothing, then EOF)
- `OnTick_RangeFilter_ChangedAtRuntime_TakesEffectImmediately` (hot-swap test, mirror 288-311)

**VM-level** (`ReplayViewModelTests.cs`, +4 tests after line 322):
- `StartTimestamp_Set_PropagatesToService`
- `StartTimestamp_Null_ClearsToService`
- `StartTimestamp_GreaterThanEndTimestamp_SetsRangeFilterError`
- `OpenAsync_ClearsRangeFilter` (after successful load)

**UI**: `ReplayView.xaml` Row 5 with 2 TextBox + 1 error TextBlock (manual smoke test in WPF; no automated UI test for v1.5.1 scope).

### Item 2 — Periodic DBC send tests

**Happy-path** (`CyclicDbcSendServiceTests.cs`, NEW ~120 LOC, mirror `CyclicSendServiceTests.cs:128`):
- `IsRunning_False_By_Default`
- `Start_Sets_IsRunning_True`
- `Stop_Sets_IsRunning_False`
- `Stop_Is_Idempotent`
- `Start_EncodesDbcMessage_Periodically` (capture encoded frames via `FakeSendService : SendService`)
- `Start_EncodesWithUpdatedSignalValues_OnEachTick` (provider returns different values each call)
- `Start_Stops_Previous_Cyclic_DbcSend`
- `Start_EncodeFailure_IncrementsFailureCount` (provider returns out-of-range value)
- `Start_MessageChangedMidRun_StopsService` (capture old message, provider returns new message)

**Race regression** (`CyclicDbcSendServiceRaceTests.cs`, NEW ~150 LOC, mirror `CyclicSendServiceRaceTests.cs:153`):
- `OnTimerTick_After_Stop_Does_Not_Send`
- `OnTimerTick_Generation_Mismatch_Does_Not_Send`
- `Encode_Failure_Increments_FailureCount_Not_SuccessCount`
- `Send_Success_Increments_SuccessCount_Not_FailureCount`
- `SuccessCount_And_FailureCount_Exposed_Via_Properties`
- **Note in test file header**: "Known transient-flaky (memory v1.2.12 lesson 4); CI re-run 3×"

**VM-level** (`DbcSendViewModelCyclicTests.cs`, NEW ~80 LOC):
- `StartDbcCyclic_WithoutSelectedMessage_DoesNothing`
- `StartDbcCyclic_WithSelectedMessage_CallsService_AndSetsIsRunningTrue`
- `StopDbcCyclic_CallsService_AndSetsIsRunningFalse`
- `StartDbcCyclic_WithInvalidIntervalText_DoesNothing`
- `StartDbcCyclic_BuildsCurrentSignalValuesFromSignalRows`

### Item 3 — Remove [Obsolete] SendCount tests

**Verification only**: grep `SendCount` 全仓 after removal → 0 matches (XAML + cs + tests)。若残留，单独修。

`CyclicSendServiceTests.cs` 不引用 `SendCount`（v1.2.12 PATCH Item 10 已切走）；`CyclicSendServiceRaceTests.cs` 同样无引用。Test 套 0 改动。

`SendViewModelTests.cs` (if any uses `SendCount`) — 删除引用。Phase 2.5 grep 0 匹配（待实施 grep 验证）。

## Test files to modify

| File | Change | Lines |
|---|---|---|
| `tests/PeakCan.Host.Core.Tests/Replay/IReplayServiceTests.cs` | +5 [Fact] methods after line 342 | ~80 LOC |
| `tests/PeakCan.Host.Core.Tests/Replay/ReplayTimelineTests.cs` | +9 [Fact] methods after line 376 | ~180 LOC |
| `tests/PeakCan.Host.App.Tests/ViewModels/ReplayViewModelTests.cs` | +4 [Fact] methods after line 322 | ~60 LOC |
| `tests/PeakCan.Host.App.Tests/Services/CyclicDbcSendServiceTests.cs` | NEW (mirror CyclicSendServiceTests.cs structure) | ~120 LOC |
| `tests/PeakCan.Host.App.Tests/Services/CyclicDbcSendServiceRaceTests.cs` | NEW (mirror CyclicSendServiceRaceTests.cs structure) | ~150 LOC |
| `tests/PeakCan.Host.App.Tests/ViewModels/DbcSendViewModelCyclicTests.cs` | NEW (subset of DbcSendViewModelTests.cs pattern) | ~80 LOC |

Total: +5 +9 +4 + 120 +150 +80 = 368 LOC of tests. Expected Core +17, App +5 + 120 +150 +80 = 355.

## Risks

### Item 1 — Replay time-range
- **LOW**: API surface additive (2 nullable double props); no breaking changes to existing call sites.
- **LOW**: Range predicate in OnTick while-loop adds 2 boolean comparisons per frame — negligible perf impact.
- **MEDIUM**: Misinterpretation of `CurrentTimestamp` behavior with Loop + range (user may expect cursor to "jump" to first in-range frame after rewind, but actual behavior is cursor walks to in-range frame). Mitigated by tests + Decision 1 doc-comment.
- **MEDIUM**: Phase 2.5 grep identified zero `Replay→Trace auto-load` related hooks; range filter does NOT propagate to Trace (out-of-scope; pre-existing Non-Goal).

### Item 2 — Periodic DBC send
- **MEDIUM**: Transient-flaky race tests (memory v1.2.12 lesson 4). Mitigated by independent service + mirror existing tested pattern + PR description caveat.
- **MEDIUM**: `SendService` virtual override seam — Phase 2.5 confirms `FakeSendService` + `CountingSendService` patterns work; new tests follow same pattern.
- **LOW**: DI topology +2 lines in AppHostBuilder; no breaking changes.
- **LOW**: UI in DbcModeExpander is additive (sibling StackPanel inside existing Expander).

### Item 3 — Remove [Obsolete] SendCount
- **LOW**: Compile error if any consumer残留。Mitigated by grep verification before ship.
- **LOW**: `SendViewModel.cs` consumer is 1 line delete + 1 partial method delete; mechanical.

## Cross-cutting concerns (from v1.4.2 PATCH + project conventions)

| Concern | Action |
|---|---|
| Sink first-failure pattern (`_sinkException` capture) | Item 1 range filter 不动这条 path；Item 2 new service 不动 ReplaySink。 |
| InternalsVisibleTo for Core.Tests | 已 in place (`AssemblyInfo.cs:17`)。新 `internal` test seams (if any) follow precedent. |
| Source-gen LoggerMessage | Item 2 new `CyclicDbcSendService` 必须用 `partial class` + `[LoggerMessage]` partial methods (4 log sites)。 |
| SynchronizationContext capture in VMs | `DbcSendViewModel` ctor 已 capture；新增 periodic state polling via DispatcherTimer (mirror `SendViewModel.cs:118-128`)。 |
| `[Obsolete]` migration deadlines | Item 3 is the disposition of one stale deadline. |
| `PathNormalizer` defense-in-depth | Item 1 + Item 2 不动 IO（Item 1 是 in-memory state mutation；Item 2 value provider returns dictionary, no IO）。自然合规。 |
| STA-WPF test discipline | App.Tests 用 NSubstitute mocks + FakeSendService virtual override。无真实 WPF control。 |
| CA2012 suppression for `ValueTask` fire-and-forget | Item 2 new service 不需要 fire-and-forget（per-tick 是 sync `await _sendService.SendAsync(...).GetAwaiter().GetResult()` in lock，mirror `ReplayService.cs:131`）。如需异步 fire-and-forget，follow `#pragma warning disable CA2012` pattern。 |
| Reflection-based test introspection | Item 1 用 reflection into `_timeline` field (mirror `SetLoop_PropagatesToInternalTimeline` line 237-263)。Item 2 不需要 reflection（new service 是 testable via direct interface + virtual override）。 |

## Ship method (mirror v1.4.2 PATCH)

1. Feature branch `feature/v1-5-1-patch` (DONE, cut from main `a77191a`).
2. Spec + plan committed to feature branch (this file + `docs/superpowers/plans/2026-06-29-v1-5-1-patch.md`).
3. TDD per-task commits (RED → GREEN → IMPROVE × 2-3 tasks).
4. Pre-ship `code-reviewer` subagent → address 0C/0H.
5. `docs/release-notes-v1.5.1.md` authored.
6. `git push -u origin feature/v1-5-1-patch` (explicit proxy unset per network note).
7. PR → `gh pr merge --squash --delete-branch` (expect first-attempt fast-forward proxy error; recover via `git fetch origin main` + `git reset --hard origin/main`).
8. Tag `v1.5.1` + `git push origin v1.5.1`.
9. `gh release create v1.5.1 --notes-file docs/release-notes-v1.5.1.md`.
10. Update `MEMORY.md` index entries (consolidate per harness 24.4KB size limit warning).

## Open Questions

- **Item 2**: Periodic DBC send UI — 是否需要 "Apply interval to next message change" 持久化？MVP 不做；每次切换 message 用户手动重设。
- **Item 2**: `Func<(Message, Dictionary)>` vs `Func<Message>` + separate value property？本 spec 用 tuple（Decision 9 refinement）。
- **Item 1**: Range filter 是否需要 "Reset on Stop" 行为？本 spec 不 reset（mirror `Loop` 行为，user persists across pause/stop）。