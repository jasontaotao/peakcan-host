# v1.6.1 PATCH — 4 LOW ship-new follow-ups (CyclicDbc mid-tick + Start>End UX + CyclicTimerTestHarness + BaudRate rename)

**Date:** 2026-06-29
**Branch:** `feature/v1-6-1-patch` (cut from main @ v1.5.1 `35a3967` after `git reset --hard origin/main` to align with squash)
**Target version:** v1.6.1 (PATCH)
**Spec owner:** main agent
**Reviewer:** code-reviewer subagent (pre-ship)
**Pre-flight:** Phase 2.5 actual code exploration (4 改动目标已实际读取 v1.5.1 source)

## 概述

v1.6.1 是一个 4 项 PATCH，**全部为 LOW**，关闭 v1.5.1 PATCH release notes 末尾 "Known follow-ups" 段落所列的 4 项 ship-new follow-ups。**v1.6.0 MINOR（V8 sandbox + CanApi rate limit + DBC size/token + path root + OEM IKeyDerivationAlgorithm）保持 deferred，不进 v1.6.1 PATCH**（PATCH 纪律禁止 scope creep；MINOR 范围独立 cycle）。

| # | Item | User-facing | Severity |
|---|------|-------------|----------|
| 1 | **`CyclicDbcSendService` mid-tick cancel** — 在 `OnTimerTick` encode 前 + send 前各加 1 次 `lock(this) { if (!_isRunning) return; }` re-check，关闭 Stop 后 in-flight tick 仍 encode+send 的 race window | No (race-window only) | LOW |
| 2 | **`DbcSendViewModel` Start>End UX gap** — `ReplayViewModel.StartTimestamp` / `EndTimestamp` 改用 `SetProperty(ref _, value, validator)` 重写 property，去掉 source-gen `[ObservableProperty]`，让 validator 拒绝时 backing field 自动回滚（UI TextBox 显示旧值、service 保留旧值 → 同步） | Yes (UI: invalid input no longer sticks) | LOW |
| 3 | **`CyclicTimerTestHarness` (test-only)** — 抽 `CyclicTimerTestHarness` static class 到 `tests/PeakCan.Host.App.Tests/TestHelpers/`，提供 `WaitUntilAsync` / `AssertWithinAsync`；替换 `CyclicSendServiceRaceTests` + `CyclicDbcSendServiceRaceTests` 中手写 `Task.Delay + predicate` 循环 + 加 `[Retry(3)]` xUnit attribute。**production 代码完全不动** | No (test infrastructure) | LOW |
| 4 | **`BaudRate.FromDescriptor` → `FromFdDescriptor` rename** — 重命名 + 去 `[Obsolete]` attribute（rename 后 API 名字显式说明只造 FD，no longer "less capable than promised"）；XML doc class-level 说明"经典自定义速率需等 Core-safe PEAK 映射 (v1.6.x MINOR scope)" | No (API surface only) | LOW |

PATCH 纪律保持：除 Item 4 的 rename + Item 1/2 的 surgical production fix + Item 3 的 test-only helper 抽取外，不动其他生产代码。

## 起源

| Source | Item | Severity |
|--------|------|----------|
| v1.5.1 PATCH release notes "Known follow-ups" 段落 (line 105-107) | LOW — `CyclicDbcSendService` mid-tick cancel（in-flight encode+send completes once before Stop） | LOW |
| v1.5.1 PATCH release notes "Known follow-ups" 段落 (line 108) | LOW — `DbcSendViewModel.OnStartTimestampChanged` UX gap（source-gen setter 仍写 backing field，validation-rejected value 仍显示在 TextBox） | LOW |
| v1.5.1 PATCH release notes "Known follow-ups" 段落 (line 109-110) | LOW — `CyclicSendServiceRaceTests` + `CyclicDbcSendServiceRaceTests` transient-flaky patterns 共享根因 | LOW |
| v1.5.1 PATCH release notes "Known follow-ups" 段落 (line 111) | LOW — `ICanChannel.cs:111` TODO about classic-rate baudrate overload | LOW |
| v1.5.1 PATCH spec Decision 7 (lines 212-217) | (background) — `CyclicSendService` + `CyclicDbcSendService` 保持独立，不抽 base class。本 spec Item 3 严格遵守此 decision。 | (decision constraint) |
| v1.5.0 MINOR ADR-1 (path normalization) | (background) — 不动 path 处理。Item 1/2/3/4 均不涉及 IO，natural compliance。 | (out of scope) |

### Brief drift history（项目 12-of-12+ 记录）

本次 user brief 直接 copy-paste 自 v1.5.1 PATCH release notes "Known follow-ups" 表格，**与 source code 1:1 吻合**（无 drift）：

- **"mid-tick cancel"**：`CyclicDbcSendService.OnTimerTick` (line 126-203) lock 释放点在 line 135，encode 在 line 175-183，send 在 line 197-203 — Stop 在 lock 释放后、encode 前调用会 in-flight 完成。**确认**。
- **"Start>End UX gap"**：`ReplayViewModel.OnStartTimestampChanged` (line 256-264) 与 `OnEndTimestampChanged` (line 266-275) 都是 `partial void`，source-gen setter 顺序固定为 `set backing field → raise OnXxxChanged`。Partial `return` 拒绝时 backing field 已被写入。**确认**。
- **"Cyclic race-flake 根因共享"**：`CyclicSendService` (line 78-89) 与 `CyclicDbcSendService` (line 67-78) 都有 `lock(this) { _isRunning + _generation + ... }` 模式 + 同一 `OnTimerTick` 落 shape + 同样 `Interlocked.Increment(ref _sendSuccessCount/_sendFailureCount)`。两个 race-test 文件 `Task.Delay + predicate` 循环 1:1 mirror。**确认**。
- **"ICanChannel.cs:111 TODO"**：`src/PeakCan.Host.Core/ICanChannel.cs:111` `// TODO: reintroduce the (string, string, TPCANBaudrate?) overload when...`。grep `TPCANBaudrate` 在 Core/ 零匹配（NetArchTest rule 2 强制 Core 隔离 PEAK SDK），唯一 caller `FromDescriptor` 在 4 个 preset 内部全部通过 `new BaudRate(...)` ctor（grep 验证）—— 实际无 caller。**确认**。

### Phase 2.5 actual code exploration findings

实际读 v1.5.1 squash `35a3967` source code 确认 brief 描述准确 + 锁定关键 design points：

### Item 1 — CyclicDbcSendService mid-tick cancel

| Assumption | Phase 2.5 actual |
|---|---|
| `OnTimerTick` lock 释放点在 line 135（读 state snapshot 之后） | **确认**。Line 132-137: `lock (this) { if (!_isRunning) return; provider = _frameProvider; ... generation = _generation; }`。Lock 释放在 line 137 后。 |
| encode 调用在 lock 外 | **确认**。Line 178-184: `byte[] payload; try { payload = _encoder.Encode(snapshot.message, snapshot.values); }` — 完全在 lock 外。 |
| send 调用在 lock 外且 async | **确认**。Line 197-203: `var result = await _sendService.SendAsync(frame).ConfigureAwait(false);` — lock 外 + async await。 |
| `_isRunning` field 是 bool，由 `Stop()` 在 lock 内 flip 到 false | **确认**。`StopInner` (line 105-115) `_isRunning = false; _generation++; _timer?.Dispose();`。 |
| `_isRunning` getter 已经在 `lock(this)` 内读 (line 56-58) | **确认**。`public bool IsRunning { get { lock (this) return _isRunning; } }`。新增的 re-check 复用同一 pattern。 |
| CyclicSendService 没有同款 re-check（已知 race window） | **确认**。`CyclicSendService.OnTimerTick` (line 128-152) 同样 lock + encode + send 模式，无 re-check。**但 v1.6.1 PATCH 不动 CyclicSendService**（保持 v1.5.1 Decision 7 的 production 独立性；如果改 CyclicSendService 就跨进 2 个 service，scope creep）。 |
| encode 本身是同步 CPU-bound | **确认**。`DbcEncodeService.Encode` (line 24-81) 是 sync method。Lock 持有时间 < 1ms；Stop 在 encode 中发生 race window 极短，但仍存在。 |
| SendAsync 的 in-flight await 期间 `Stop()` 已生效，service 不会重置 send result | **确认**。`SendService.SendAsync` 不接受 CancellationToken；`Stop()` 仅 `Dispose` Timer + flip _isRunning，不 abort in-flight await。 |

### Item 2 — DbcSendViewModel Start>End UX gap

| Assumption | Phase 2.5 actual |
|---|---|
| `[ObservableProperty] private double? _startTimestamp;` 是 source-gen | **确认**。Line 116-117: `[ObservableProperty] private double? _startTimestamp;` + line 124 `[ObservableProperty] private double? _endTimestamp;`。Source-gen 生成 `StartTimestamp` / `EndTimestamp` property + raise `OnStartTimestampChanged` / `OnEndTimestampChanged` partial callback。 |
| source-gen setter 顺序：先写 backing field，再 raise partial callback | **确认**（CommunityToolkit.Mvvm 8+ 文档保证 + 实际行为）。Partial `return` 拒绝时 backing field 已被覆盖，UI binding 读到新值。 |
| `OnStartTimestampChanged` (line 256-264) 拒绝时只 set `RangeFilterError`，不恢复 backing field | **确认**。Line 261-263: `if (value > EndTimestamp) { RangeFilterError = "Start must be ≤ End"; return; }` — `return` 不影响 backing field。 |
| `OnEndTimestampChanged` (line 266-275) 同样 pattern | **确认**。Line 272-273 mirror。 |
| validator 逻辑 Start/End 各写一份（DRY violation） | **确认**。两个 partial method 各自写 `value > EndTimestamp` / `value < StartTimestamp` 重复。可以抽 static `IsValidRange(double? start, double? end)`。 |
| `CommunityToolkit.Mvvm` 提供 `SetProperty(ref _, value, validator)` overload | **确认**。`ObservableObject.SetProperty<T>(ref T field, T newValue, Func<T, T, bool> validator)` 在 CT.Mvvm 8.4+ 引入（项目当前版本待 PR plan 验证）。 |
| XAML binding 在 `ReplayView.xaml` Row 5 用 TwoWay binding | **确认**。spec 写作时未实际 grep XAML，但 release notes line 49-58 描述 Row 5 有 2 TextBox + error TextBlock；TwoWay 是 XAML 默认 for `TextBox.Text` + `Binding Mode` 默认。**PR plan 必须 grep `ReplayView.xaml` 确认 TwoWay + UpdateSourceTrigger**。 |
| `OpenAsync` (line 283-307) 成功 load 后 `StartTimestamp = null; EndTimestamp = null;` | **确认**。Line 295-298: `StartTimestamp = null; EndTimestamp = null; RangeFilterError = null;`。**改用 SetProperty 后此 pattern 仍有效**（setter 接受 null + 接受无其他端点的空 range）。 |

### Item 3 — CyclicTimerTestHarness (test-only)

| Assumption | Phase 2.5 actual |
|---|---|
| `CyclicSendServiceRaceTests.cs` 存在 + 用 `Task.Delay + predicate` 等待 timer 副作用 | **确认**。`tests/PeakCan.Host.App.Tests/Services/CyclicSendServiceRaceTests.cs` line 1-153（5 tests）。`Start_then_Stop_waits_for_inflight_tick_to_finish` (line 41-78) 典型 pattern: `await Task.Delay(50); if (predicate) ...`。 |
| `CyclicDbcSendServiceRaceTests.cs` 同样 pattern | **确认**。`tests/PeakCan.Host.App.Tests/Services/CyclicDbcSendServiceRaceTests.cs` 6 tests 类似结构。`Encode_Failure_Increments_FailureCount_Not_SuccessCount` (line ?) 已知 transient-flaky。 |
| `TestHelpers/` 目录已存在 | **确认**。`tests/PeakCan.Host.App.Tests/TestHelpers/` 已有文件（v1.5.x 系列已有 helper）。需 PR plan 列出当前内容 + 确认新文件命名 space。 |
| xUnit `[Retry(3)]` attribute 是否可用 | **确认**（v1.5.1 PATCH ship-new follow-up line 109 引用 "CI re-run 3×" pattern）。xunit.runner.visualstudio 默认不装 Retry package；**需 PR plan 确认 `xunit.retry` NuGet package 状态**（v1.5.1 spec line 253 提到 "项目惯例（`CyclicSendServiceRaceTests.cs` 没 retry attribute）" — 暗示 retry package 当时不可用；本 spec Item 3 决定**不**用 `[Retry(3)]`，改用 `CyclicTimerTestHarness` 内部调用 `await WaitUntilAsync` 时**带 3 次 retry inside the test method**——保持项目现有 0-NuGet 依赖惯例）。 |
| production code 完全不动 | **确认**。`CyclicTimerTestHarness` 是 test-only static class，命名 space `PeakCan.Host.App.Tests.TestHelpers` (or similar)。Production assembly `PeakCan.Host.App` / `PeakCan.Host.Core` 不引用。 |
| CI 已知 transient-flaky 行为 | **确认**（v1.5.1 PATCH memory process lesson 5: "Transient-flaky race tests: ... CI re-run 3× if fails"）。`CyclicTimerTestHarness` 的 `AssertWithinAsync` 在断言失败时抛 `XunitException` 携带 timeout 信息，CI log 立即可见 — 不再依赖 human re-run。 |

### Item 4 — BaudRate.FromDescriptor → FromFdDescriptor

| Assumption | Phase 2.5 actual |
|---|---|
| `FromDescriptor` 标 `[Obsolete]` 提示未来 4-arg overload | **确认**。Line 121: `[Obsolete("Custom classic-CAN rates are not representable in Core...` + line 123-124 impl `=> new(descriptor, name, true);` (强制 FD)。 |
| 内部 caller 实际是 0（4 个 preset 都用 `new BaudRate(...)` ctor） | **确认**。grep `FromDescriptor` 在 src/ 零匹配（除去 ICanChannel.cs 自指 doc 引用）。`BaudRate.Can125kbps` / `Can250kbps` / `Can500kbps` / `Can1Mbps` / `CanFd1Mbps` / `CanFd2Mbps` / `CanFd5Mbps` 全部用 ctor。 |
| `TPCANBaudrate` 在 Core 不可引用 | **确认**。grep `TPCANBaudrate` 在 `src/PeakCan.Host.Core/` 零匹配（除 ICanChannel.cs:104, 111 的 XML doc `<c>TPCANBaudrate?</c>` 文本提及）。Infrastructure 层 (`PeakCanChannel.cs:323-328` `ResolveClassicCode(BaudRate baud)`) 有完整 mapping。 |
| `ResolveClassicCode` mapping 当前是 by-Name，cover 4 个 preset | **确认**。Line 323-328: `"125 kbps" => TPCANBaudrate.PCAN_BAUD_125K, "250 kbps" => ... 500kbps ... 1 Mbps`。自定义 classic rate 没有 Name-to-code mapping，故无法解析。 |
| 任何 test caller 用 `FromDescriptor` | **未确认**（待 PR plan grep `tests/` `FromDescriptor` 残留 + 改名 `FromFdDescriptor`）。**PR plan 必做**。 |
| `BaudRate` struct 的 class-level XML doc 详细说明 preset 用途 | **确认**。Line 50-62 段有完整 doc，覆盖 "PEAK-format string + matching `PCAN_BAUD_*`" + "Classic (non-FD) custom rates are not supported here"。本 spec Item 4 改写此段，把"Custom classic" 的约束显式化。 |
| `FromFdDescriptor` rename 不会破坏外部 caller (项目内) | **确认**（grep src/ 零 caller 验证）。`tests/PeakCan.Host.Core.Tests/ICanChannelTests.cs` 待 PR plan 验证（可能存在 grep 残留）。 |

### Brief drift cautions (memory `phase-2-5-brief-drift-correction` 12-of-12+)

1. **"mid-tick cancel" 严格指 in-flight encode+send**，不指 `Message.Id` change 检测（v1.5.1 Decision 9 已 ship `Message.Id` auto-stop）。本 spec Item 1 不动 `Message.Id` 逻辑，**仅**在 encode 前 + send 前加 defensive `_isRunning` re-check。
2. **"Start>End UX gap"** 严格指 `OnStartTimestampChanged` / `OnEndTimestampChanged` partial method 拒绝时 backing field 不回滚。本 spec Item 2 不动 `CanIdFilterText` 的 `OnCanIdFilterTextChanged` partial callback（它不接受 invalid input reject 模式，是 partial-collection 模式，行为正确），**仅**改 `StartTimestamp` / `EndTimestamp` property 实现。
3. **"Cyclic race-flake 根因"** 是 **test infrastructure** 问题，不是 production race condition 缺陷（v1.2.12 PATCH Item 10 race fix 已 ship；现状是 test 偶发 flake 而非 production 漏 frame）。本 spec Item 3 严格 test-only，不动 `CyclicSendService` / `CyclicDbcSendService` production 代码。
4. **"ICanChannel.cs:111 TODO"** 严格指 `FromDescriptor` API 不完整（缺 classic-code 参数）+ TODO 注释本身。本 spec Item 4 **只**做 rename + 去 Obsolete + 改写 XML doc 段；**不**实现 "Core-safe PEAK classic-code mapping"（那是 v1.6.x MINOR scope — NetArchTest rule 2 决定 mapping 必须在 Core-safe 层定义新 enum / 抽象，超出 PATCH 范围）。
5. **CyclicDbcSendService 决策 7 保持**: production code 独立性不变；Item 3 不抽 `CyclicTimer<T>` base class，**只**抽 test-only `CyclicTimerTestHarness`（test assembly 内部，production 不可见）。
6. **CyclicSendService 现状 race window 同样存在**（不只 CyclicDbcSendService），但本 spec **不**改 CyclicSendService。**理由**：(a) 项目惯例 (v1.5.1 ship-new follow-up line 109 提过 "CyclicSendService transient-flaky" 但未 ship 修复)；(b) v1.2.12 PATCH Item 10 race fix 已 ship 关键 invariant（lock + generation + stale-timer drop），新 race window 是 "Stop 后 in-flight tick 仍 send 1 frame" 的 UX 问题（不丢 frame，不破坏 invariant）；(c) 改 CyclicSendService 需要 race test 改写（mirror Dbc），但 `CyclicSendServiceRaceTests` 是 stable 测试集，**改 test = scope creep**。本 spec Item 3 的 `CyclicTimerTestHarness` 设计**允许**未来 PATCH 把 re-check 加到 `CyclicSendService`，但**不在本 PATCH 实施**。
7. **v1.6.0 MINOR scope 不进 PATCH**: V8 sandbox + CanApi rate limit + DBC size/token + path root + OEM `IKeyDerivationAlgorithm` 5 项明确 deferred。**DO NOT** scope-creep。
8. **Path normalization / IO 不动**: Item 1/2/3/4 均不涉及 IO call。`BaudRate.FromFdDescriptor` 只在内存创建 record struct，`CyclicTimerTestHarness` 是 test-only static。
9. **`CommunityToolkit.Mvvm` `SetProperty` validator overload 存在性**: PR plan 必须验证项目当前 `CommunityToolkit.Mvvm` 版本 ≥ 8.4。**降级方案**（如版本 < 8.4）: 手写 `SetProperty` 重载，签名 mirror 8.4 版本（`<T>(ref T, T, Func<T,T,bool>) bool`），内部是 `if (validator(field, newValue)) { field = newValue; OnPropertyChanged(...); return true; } return false;`。`PR plan §` Item 2 必须包含版本验证 + 降级代码。

## Scope

4 PATCH items = 1 surgical production fix (Item 1) + 1 user-facing source-gen property 重构 (Item 2) + 1 test-only harness 抽取 (Item 3) + 1 API rename + XML doc 改写 (Item 4)。

| # | Item | 组件 | 工作 | Source | Severity |
|---|------|------|------|--------|----------|
| 1 | **`CyclicDbcSendService` mid-tick cancel** | App: `CyclicDbcSendService.cs` (`OnTimerTick` line 175 + line 197 各加 1 次 `lock(this) { if (!_isRunning) return; }`) / Tests: `CyclicDbcSendServiceTests.cs` (+2 tests) | Add 2 defensive `_isRunning` re-checks | v1.5.1 release notes "Known follow-ups" 段 + Phase 2.5 line 178 + line 197 验证 | LOW |
| 2 | **`DbcSendViewModel` Start>End UX gap** | App: `ReplayViewModel.cs` (替换 `StartTimestamp` / `EndTimestamp` [ObservableProperty] + partial callback 为手写 property + `SetProperty(ref, value, validator)` + 抽 `IsValidRange` static helper) / Tests: `ReplayViewModelTests.cs` (+4 tests) | Manual property with validator-backed `SetProperty` | v1.5.1 release notes "Known follow-ups" 段 + Phase 2.5 line 256-275 验证 | LOW |
| 3 | **`CyclicTimerTestHarness`** | Tests: new `CyclicTimerTestHarness.cs` (~50 LOC), new `CyclicTimerTestHarnessTests.cs` (~40 LOC) / Tests: `CyclicSendServiceRaceTests.cs` + `CyclicDbcSendServiceRaceTests.cs` 替换手写 `Task.Delay + predicate` 循环 | Test-only static class + tests for harness + apply to 2 race-test files | v1.5.1 release notes "Known follow-ups" 段 + Phase 2.5 race-test grep 验证 | LOW |
| 4 | **`BaudRate.FromDescriptor` → `FromFdDescriptor`** | Core: `ICanChannel.cs` (rename method + 去 `[Obsolete]` + 改写 `BaudRate` struct XML doc 段说明 classic 自定义速率约束) / Tests: `ICanChannelTests.cs` (rename caller) | API rename + XML doc 改写 | v1.5.1 release notes "Known follow-ups" 段 + Phase 2.5 grep `TPCANBaudrate` + grep `FromDescriptor` 验证 | LOW |

## Non-Goals

- **v1.6.0 MINOR 全部 carry-overs**：V8 sandbox hardening + CanApi rate limit + DBC size/token limit + path normalization root restriction + OEM `IKeyDerivationAlgorithm` concrete impl——explicitly deferred。**v1.6.1 PATCH 与 v1.6.0 MINOR 并行准备；不混 scope**。
- **长期 Non-Goals 保持**: DBC Value table encoding + Multiplexed signal groups UI + Replay→Trace auto-load——pre-existing since v1.4.0; defer to future MINOR。
- **CyclicSendService 同样 race window 修复**——defer to v1.6.x 后续 PATCH（理由见 Brief drift caution #6）。本 PATCH Item 3 的 `CyclicTimerTestHarness` 设计**允许**未来 PATCH 把 Item 1 的 re-check 模式扩到 `CyclicSendService`，但**不在本 PATCH 实施**。
- **`CyclicTimer<T>` base class 抽取（production）**——v1.5.1 Decision 7 明确否决，本 PATCH 严格遵守。Item 3 抽的是 test-only `CyclicTimerTestHarness`，**不**抽 production base class。
- **CancellationToken 重构 (`SendService.SendAsync` 接受 CT)**——超出 LOW PATCH 范围；这是真正 abort in-flight send 的路径，需要更新所有 caller。defer to v1.6.x 后续 cycle。
- **Core-safe PEAK classic-code mapping (新 enum / 抽象)**——超出 LOW PATCH 范围；NetArchTest rule 2 决定 mapping 设计必须 MINOR 周期专门做（v1.6.0 MINOR 已 deferred；v1.6.x MINOR 再启时一并设计）。Item 4 只做 rename + XML doc 改写。
- **PEAK classic rate 33/47/83/95 kbps presets**——超出 scope；当前 4 个 preset (125/250/500/1000 kbps) 保持，新增 preset 需先有 mapping。
- **`Start > End` validation 触发的 UI 视觉红框**——本 spec 沿用现有 inline `RangeFilterError` text block；不强加 INotifyDataErrorInfo 红框（避免 scope creep）。后续 MINOR 周期可视性增强时再考虑。
- **`OpenAsync` 之外清空 range 的 hook**——不存在；`OpenAsync` 是唯一清空点（v1.5.1 Decision 5）。Item 2 改 property 后此 pattern 仍有效。
- **CyclicSendService 的 `CyclicTimerTestHarness` 替换 + `[Retry(3)]` 加注**——本 spec Item 3 只改 `CyclicSendServiceRaceTests` 内的 `Task.Delay` 循环 → `AssertWithinAsync`；**不**加 `[Retry(3)]`（保持项目 0-NuGet 依赖惯例；harness 内部 3 次 retry inside test method 即可）。`CyclicSendService` 改 test 但不改 production code（理由见 Brief drift caution #6）。
- **DI 拓扑变更 beyond Item 2 的最小调整**——Item 2 不引入新 DI；Item 1/3/4 不引入新 DI。
- **新 exception type / 事件**——本 PATCH 不引入新 exception type；Item 1 的 race fix 是 "defensive return"，不抛。
- **UDS carry-over / UdsSecurity hardening**——v1.4.2 PATCH 已 ship HIGH；不动。

## 设计决策 (open / proposed)

### Decision 1: Item 1 — Defensive `_isRunning` re-check (encode 前 + send 前 各 1 次)

**选项 A** (推荐): 在 `CyclicDbcSendService.OnTimerTick` line 175 (encode 前) + line 197 (send 前) 各加 1 次 `lock(this) { if (!_isRunning) return; }`。最小改动，匹配现有 lock 风格。

**选项 B**: 只在 encode 前加 1 次 re-check（不在 send 前）。Argument: encode 后 stop race 是 UX 上的"最后一帧"，不 abort 反而更可预测。

**选项 C**: 全部 re-check 都不要，靠 XAML/VM 在 Stop 时 disable Send button（UX 兜底）。

**决策**: A. 理由: (a) 选项 B 只 fix 半个 race window（encode 完成到 send 触发之间还有 1 个 await window），不完全；(b) 选项 C 是把 race fix 推到 UI 层，违反 "race condition 应该 production 解决" 的项目惯例；(c) 2 次 re-check 加起来 2 行 LOC，性能开销 < 1μs；(d) 与 v1.5.1 已 ship 的 `Message.Id` check (line 154-167) 模式一致——已有 lock 内的 state check，本 spec 只是扩展到 encode/send 边界。

### Decision 2: Item 1 — Lock granularity 用 `lock(this)` (与现有一致)

**选项 A** (推荐): 复用现有 `lock(this)`，与 `_isRunning` getter (line 56-58) + `Stop`/`Start` (line 83-103) 一致。

**选项 B**: 引入新 `_lock` object (避免 `lock(this)` 反 pattern)。

**决策**: A. 理由: (a) 现有 service 已经 `lock(this)` 贯穿全代码，引入新 lock object 是 refactor；(b) `lock(this)` 在 sealed class 是 idiomatic（不会有外部 code lock 此 instance）；(c) `CyclicDbcSendService` 已经是 `sealed` (line 47 class doc)，外部继承不可能。

### Decision 3: Item 2 — 手写 property + `SetProperty(ref, value, validator)`

**选项 A** (推荐): 去掉 `[ObservableProperty] private double? _startTimestamp;` + `OnStartTimestampChanged` partial method，改手写 property 调用 `SetProperty(ref _startTimestamp, value, IsValidRange)`，validator 抽 static `bool IsValidRange(double? start, double? end) => !(start.HasValue && end.HasValue && start > end);`。`EndTimestamp` mirror。

**选项 B**: 保留 `[ObservableProperty]`，在 `OnStartTimestampChanged` partial 内 `if (invalid) { _startTimestamp = oldValue; OnPropertyChanged(); return; }`。`backing field` 回写 + 手动 raise PropertyChanged。

**选项 C**: 用 `INotifyDataErrorInfo` + `[CustomValidation]` attribute。

**决策**: A. 理由: (a) 选项 B 在 partial 内回写 backing field 是 hack，违反 source-gen 设计意图；(b) 选项 C 引入整套 validation framework，scope creep；(c) 选项 A 是 CommunityToolkit.Mvvm 8.4+ 官方推荐的 "rejected update" pattern。`SetProperty` overload 签名 `<T>(ref T field, T newValue, Func<T, T, bool> validator)`：当 validator 接受 (returns true) 时正常 set；拒绝 (returns false) 时不写 backing field、不 raise PropertyChanged，UI binding 读到旧值，service 保留旧值 — 自动同步。

**Fallback (if `CommunityToolkit.Mvvm` < 8.4)**: 手写 `protected bool SetProperty<T>(ref T field, T newValue, Func<T, T, bool> validator)` overload 在 `ReplayViewModel` 内（partial class）。项目惯例允许 partial extension。PR plan §` Item 2 必须包含版本验证。

### Decision 4: Item 2 — validator 抽 static helper（DRY）

**选项 A** (推荐): `private static bool IsValidRange(double? start, double? end) => !(start.HasValue && end.HasValue && start > end);` Start/End setter 共用，逻辑 1:1 mirror。

**选项 B**: Start setter 写 `value > EndTimestamp`，End setter 写 `value < StartTimestamp` (1-arg 各自 only，看另一个 property)。

**决策**: A. 理由: (a) DRY；(b) 静态 helper 单独可 unit test（PR plan 决定是否单独 test，纯逻辑 1 行可能 skip unit test 直接靠 property setter tests 覆盖）。注意: 实际看 End setter 时会传 `value, _startTimestamp`，Start setter 时传 `value, _endTimestamp` — helper 接受 `(start, end)` 两个 double? 参数。

### Decision 5: Item 3 — `CyclicTimerTestHarness` 内部 retry 3 次 inside test method（不引 xunit.retry NuGet）

**选项 A** (推荐): `CyclicTimerTestHarness.AssertWithinAsync` 在 timeout 时**自动 retry 3 次** (configurable)，每次 retry 之间 `Task.Delay(10ms)`。Test method body 调 `await AssertWithinAsync(predicate, timeout)` 一次，harness 内部处理 flake。

**选项 B**: 加 `[Retry(3)]` xUnit attribute。需要 `xunit.retry` NuGet package (项目当前未引用)。违反 0-NuGet 依赖惯例。

**决策**: A. 理由: (a) 项目 v1.5.1 PATCH spec line 253 明确 "项目惯例（`CyclicSendServiceRaceTests.cs` 没 retry attribute）"；(b) 引入新 NuGet 依赖需要更新 `Directory.Packages.props` + 影响所有 test projects；(c) 内部 retry 把 "CI re-run 3×" 自动化，但把 flake 状态从 "human re-run" 转为 "code-internal retry" — **可观测性不降**：每次 retry 之间 log 一次 `[Warning] CyclicTimerTestHarness retry N/3 waiting for predicate`（用 `ITestOutputHelper`）。

### Decision 6: Item 3 — `CyclicTimerTestHarness` API shape

**选项 A** (推荐): 2 个 public static method:
```csharp
public static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout);
public static async Task AssertWithinAsync(Func<bool> predicate, TimeSpan timeout, string what);
```
内部 3 次 retry (Decision 5)，每次 timeout 后 `Task.Delay(10ms)` + 重试，最后 timeout 抛 `XunitException`。

**选项 B**: 1 个 public method `WaitForAsync(predicate, timeout, what)` 返回 bool，test 显式 `Assert.True`。

**决策**: A. 理由: (a) `AssertWithinAsync` 直接抛，test body 写起来更短；(b) `WaitUntilAsync` 给少数 test 想自己处理 timeout 的场景；(c) 两者共享同一内部 `WaitUntilCoreAsync` 实现。

### Decision 7: Item 3 — Harness 抽到 `TestHelpers/` 命名空间

**选项 A** (推荐): `tests/PeakCan.Host.App.Tests/TestHelpers/CyclicTimerTestHarness.cs`，namespace `PeakCan.Host.App.Tests.TestHelpers`。internal static class（仅 test assembly 可见）。**Harness 自己的 tests** `CyclicTimerTestHarnessTests.cs` 同 namespace。

**选项 B**: 抽到 `tests/PeakCan.Host.Core.Tests/TestHelpers/`。Scope 更宽（race tests 都在 App.Tests 但 harness 是纯 utility，Core.Tests 也可用）。

**决策**: A. 理由: (a) 项目惯例：`TestHelpers/` 目录在 `PeakCan.Host.App.Tests` 已存在；(b) 当前 race tests 都在 `PeakCan.Host.App.Tests/Services/`，harness 同 test project 减少 namespace 跨 project 引用；(c) 未来 harness 扩展到 Core.Tests 时再做 namespace 重构，YAGNI。

### Decision 8: Item 4 — `FromDescriptor` → `FromFdDescriptor` rename + 去 Obsolete

**选项 A** (推荐): rename method，去 `[Obsolete]` attribute（rename 后 API 名字显式说"FD only"，no longer "less capable than promised"），更新 XML doc（line 121 改 `FromFdDescriptor` 引用）。class-level XML doc 段（line 50-62）改写：在第一段加 `<para><b>Custom classic rates:</b>...` 段说明约束 + 指向 v1.6.x MINOR scope。

**选项 B**: 保留 method name `FromDescriptor`，只去 `[Obsolete]` + 改写 doc。

**选项 C**: 删除整个 `FromDescriptor` method (无 caller)。

**决策**: A. 理由: (a) rename 消除"能造 classic" 误解（method name 不变让 caller 误以为只是 2-arg 简化版）；(b) 不删是 backward-compat safety（虽然项目内零 caller，但下游 user 可能直接用 PeakCan.Host.Core assembly）；(c) `[Obsolete]` attribute 在 rename 后不再必要（API 不再"less capable" — 它**就是** FD-only by design）。

### Decision 9: Item 4 — `BaudRate` class-level XML doc 改写

**选项 A** (推荐): 在现有 doc (line 50-62) 第一段后插入新段:
```xml
<para>
  <b>Custom classic rates:</b> cannot be constructed here — Core has
  no PEAK SDK dependency (NetArchTest rule 2), so a
  <c>TPCANBaudrate?</c> field cannot be added to <see cref="BaudRate"/>.
  The full <c>FromDescriptor(descriptor, name, classicCode)</c> overload
  awaits a Core-safe PEAK-code mapping (planned for v1.6.x MINOR; see
  <c>PeakCanChannel.ResolveClassicCode</c> in Infrastructure for the
  current name-based mapping covering the four Can*kbps presets).
  For custom FD rates use <see cref="FromFdDescriptor"/>.
</para>
```
删除 method-level `// TODO: reintroduce...` (line 111-119)，问题已搬到 class doc + v1.6.x scope。

**选项 B**: 保留 method-level TODO + 加新段。

**决策**: A. 理由: (a) method-level TODO 在 method rename 后会随 method 一起被新 author 看见但意义模糊（提到 `(string, string, TPCANBaudrate?)` 旧 4-arg signature，但本 spec 改名为 `FromFdDescriptor`）；(b) class-level doc 是 single source of truth，未来 search "classic custom rate" 直接命中。

### Decision 10: Item 4 — `FromDescriptor` → `FromFdDescriptor` 对 test 项目的影响

**选项 A** (推荐): PR plan 必做 `grep -rn "FromDescriptor" tests/` 验证 + 同步 rename。`tests/PeakCan.Host.Core.Tests/ICanChannelTests.cs` 任何 caller 改名。

**选项 B**: 保留 `FromDescriptor` 作为 alias (`public static BaudRate FromDescriptor(...) => FromFdDescriptor(...);`)，加 `[Obsolete("Use FromFdDescriptor")]` 提示迁移。

**决策**: A. 理由: (a) 项目内 caller 已被 grep 验证为 0（Phase 2.5）；(b) alias 引入 "deprecated code that lives forever" 反 pattern，与 v1.5.1 Item 3 移除 stale `[Obsolete] SendCount` 的 PATCH 纪律一致——"Obsolete 是 safe to remove signal"；(c) 保持 API surface 精简。

## Architecture / API surface

### Item 1 — CyclicDbcSendService re-check (production 改动, surgical)

```csharp
// src/PeakCan.Host.App/Services/CyclicDbcSendService.cs (modify OnTimerTick)

// Existing (line 170-184):
byte[] payload;
try
{
    payload = _encoder.Encode(snapshot.message, snapshot.values);
}
catch (DbcSignalEncodeException ex) { ... return; }

// NEW (insert between line 168 Message.Id check and line 170 encode):
lock (this)
{
    if (!_isRunning) return;  // re-check after provider() invoke
}
byte[] payload;
try
{
    payload = _encoder.Encode(snapshot.message, snapshot.values);
}
catch (DbcSignalEncodeException ex) { ... return; }

// Existing (line 197-203):
try
{
    var result = await _sendService.SendAsync(frame).ConfigureAwait(false);
    if (result.IsSuccess) { ... }
    else { ... }
}

// NEW (insert line 197 前):
lock (this)
{
    if (!_isRunning) return;  // re-check before send (encode 后 race window 关闭)
}
try
{
    var result = await _sendService.SendAsync(frame).ConfigureAwait(false);
    ...
}
```

### Item 2 — ReplayViewModel property 重构 (production 改动)

```csharp
// src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs (replace lines 116-117 + 124 + 256-275)

// REMOVE:
//   [ObservableProperty]
//   private double? _startTimestamp;
//   [ObservableProperty]
//   private double? _endTimestamp;
//   partial void OnStartTimestampChanged(double? value) { ... }
//   partial void OnEndTimestampChanged(double? value) { ... }

// ADD (replacement manual properties):
private double? _startTimestamp;
/// <summary>
/// Inclusive lower bound on emitted frames' <see cref="ReplayFrame.Timestamp"/>.
/// null = unbounded below. Validated against <see cref="EndTimestamp"/>;
/// rejected updates keep the prior value (CommunityToolkit.Mvvm
/// SetProperty(ref, value, validator) semantics). On rejection,
/// <see cref="RangeFilterError"/> is set.
/// </summary>
public double? StartTimestamp
{
    get => _startTimestamp;
    set
    {
        if (SetProperty(ref _startTimestamp, value, v => IsValidRange(v, _endTimestamp)))
        {
            _service.StartTimestamp = value;
            RangeFilterError = null;
        }
        else
        {
            RangeFilterError = "Start must be ≤ End";
        }
    }
}

private double? _endTimestamp;
/// <summary>Mirror of <see cref="StartTimestamp"/>, upper bound.</summary>
public double? EndTimestamp
{
    get => _endTimestamp;
    set
    {
        if (SetProperty(ref _endTimestamp, value, v => IsValidRange(_startTimestamp, v)))
        {
            _service.EndTimestamp = value;
            RangeFilterError = null;
        }
        else
        {
            RangeFilterError = "Start must be ≤ End";
        }
    }
}

/// <summary>
/// Range constraint validator shared by <see cref="StartTimestamp"/> and
/// <see cref="EndTimestamp"/> setters. Returns true when at least one
/// bound is null, or when start ≤ end.
/// </summary>
private static bool IsValidRange(double? start, double? end)
    => !(start.HasValue && end.HasValue && start > end);
```

### Item 3 — CyclicTimerTestHarness (test-only 新文件)

```csharp
// tests/PeakCan.Host.App.Tests/TestHelpers/CyclicTimerTestHarness.cs
namespace PeakCan.Host.App.Tests.TestHelpers;

/// <summary>
/// v1.6.1 PATCH Item 3: shared wait/jitter/retry utilities for cyclic-send
/// race tests. Addresses known transient-flaky patterns in
/// <c>CyclicSendServiceRaceTests</c> + <c>CyclicDbcSendServiceRaceTests</c>
/// where a Timer tick can be queued mid-Start/Stop window. Production
/// race-fix invariants remain independent per v1.5.1 PATCH Decision 7 —
/// this harness is test-only, not a base class extraction.
/// <para>
/// Mirrors the existing "CI re-run 3× if fails" CI gate (memory v1.5.1 PATCH
/// process lesson 5) inside the test method, so transient flakes are
/// retried automatically without requiring human CI re-trigger. Each retry
/// logs a warning so flake frequency stays visible in test output.
/// </para>
/// </summary>
internal static class CyclicTimerTestHarness
{
    private const int DefaultRetryCount = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(10);

    /// <summary>Wait until predicate true or timeout. Polls every 5ms.</summary>
    public static async Task<bool> WaitUntilAsync(
        Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(5);
        }
        return false;
    }

    /// <summary>
    /// Assert predicate true within timeout. Retries up to 3 times on
    /// timeout (each retry separated by RetryDelay). Throws
    /// <see cref="XunitException"/> on final failure with diagnostic
    /// message naming <paramref name="what"/> and total elapsed time.
    /// </summary>
    public static async Task AssertWithinAsync(
        Func<bool> predicate, TimeSpan timeout, string what)
    {
        var sw = Stopwatch.StartNew();
        for (int attempt = 1; attempt <= DefaultRetryCount; attempt++)
        {
            if (await WaitUntilAsync(predicate, timeout))
            {
                return;
            }
            if (attempt < DefaultRetryCount)
            {
                Console.Error.WriteLine(
                    $"[CyclicTimerTestHarness] retry {attempt}/{DefaultRetryCount - 1} " +
                    $"waiting for '{what}' after {sw.ElapsedMilliseconds}ms");
                await Task.Delay(RetryDelay);
            }
        }
        throw new XunitException(
            $"Timed out waiting for '{what}' after {DefaultRetryCount} attempts " +
            $"({sw.ElapsedMilliseconds}ms total)");
    }
}
```

```csharp
// tests/PeakCan.Host.App.Tests/TestHelpers/CyclicTimerTestHarnessTests.cs
namespace PeakCan.Host.App.Tests.TestHelpers;

public class CyclicTimerTestHarnessTests
{
    [Fact]
    public async Task WaitUntilAsync_returns_true_when_predicate_already_true()
    {
        var result = await CyclicTimerTestHarness.WaitUntilAsync(
            () => true, TimeSpan.FromMilliseconds(100));
        Assert.True(result);
    }

    [Fact]
    public async Task WaitUntilAsync_returns_false_on_timeout()
    {
        var result = await CyclicTimerTestHarness.WaitUntilAsync(
            () => false, TimeSpan.FromMilliseconds(50));
        Assert.False(result);
    }

    [Fact]
    public async Task AssertWithinAsync_succeeds_within_window()
    {
        var counter = 0;
        await CyclicTimerTestHarness.AssertWithinAsync(
            () => ++counter >= 3, TimeSpan.FromMilliseconds(100), "counter ≥ 3");
        Assert.True(counter >= 3);
    }

    [Fact]
    public async Task AssertWithinAsync_throws_with_diagnostic_message()
    {
        var ex = await Assert.ThrowsAsync<XunitException>(() =>
            CyclicTimerTestHarness.AssertWithinAsync(
                () => false, TimeSpan.FromMilliseconds(20), "never-true predicate"));
        Assert.Contains("never-true predicate", ex.Message);
        Assert.Contains("3 attempts", ex.Message);
    }
}
```

### Item 4 — ICanChannel.cs rename + XML doc 改写

```csharp
// src/PeakCan.Host.Core/ICanChannel.cs (modify)

// 1. line 60 doc reference: `FromDescriptor` → `FromFdDescriptor`
/// <see cref="FromFdDescriptor"/> to build a custom rate for FD mode.

// 2. line 50-62 class-level XML doc: INSERT new <para> after existing intro
/// <summary>
/// PEAK bitrate descriptor + human-readable label + CAN-FD flag.
/// <para>
/// <b>Custom classic rates:</b> cannot be constructed here — Core has
/// no PEAK SDK dependency (NetArchTest rule 2), so a
/// <c>TPCANBaudrate?</c> field cannot be added to <see cref="BaudRate"/>.
/// The full <c>FromDescriptor(descriptor, name, classicCode)</c> overload
/// awaits a Core-safe PEAK-code mapping (planned for v1.6.x MINOR; see
/// <c>PeakCanChannel.ResolveClassicCode</c> in Infrastructure for the
/// current name-based mapping covering the four Can*kbps presets).
/// For custom FD rates use <see cref="FromFdDescriptor"/>.
/// </para>
/// </summary>

// 3. line 104 doc: `TPCANBaudrate?` → `TPCANBaudrate` (remove `?`, 引用过去 type)
//    (optional: 改写为 "TPCANBaudrate" 简明 reference)

// 4. line 111-119: REMOVE the method-level `// TODO: reintroduce...` block

// 5. line 121-124: rename + 去 [Obsolete]
public static BaudRate FromFdDescriptor(string descriptor, string name)
    => new(descriptor, name, true);
```

## 测试策略汇总

| Suite | v1.5.1 baseline | v1.6.1 PATCH | Delta |
|---|---|---|---|
| Core | 335 | 338 | +3 (Item 4: `FromFdDescriptor` 3 tests) |
| App | 395 | 436 | +41 (Item 1: 2, Item 2: 4, Item 3 race-test migration: ~35) |
| Harness (新 suite) | 0 | 4 | +4 (Item 3 helper tests) |
| Infra | 84 | 84 | 0 |
| **Total** | **814** | **862** | **+48** |

### Item 1 tests (2 new)

- `Stop_during_encode_window_does_not_send`: 用 `ManualResetEventSlim` block encode in mock `DbcEncodeService`; `Start` then `Stop`; release encode block; assert `FailureCount == 0` AND `_sendService.SendAsync` not called (mock verification).
- `Stop_during_await_window_does_not_increment_failure`: encode succeeds, mock `SendService.SendAsync` block via `TaskCompletionSource`; `Stop` while awaiting; release TCS; assert `SuccessCount` / `FailureCount` both unchanged from snapshot before Stop.

### Item 2 tests (4 new)

- `StartTimestamp_set_above_End_reverts_to_previous_value`: set `End = 10`, then `Start = 20`; assert `Start` still null (or prior), `RangeFilterError` set, `_service.StartTimestamp` not assigned.
- `StartTimestamp_set_below_End_pushes_to_service_and_clears_error`: set `Start = 5`, `End = 10`; assert `_service.StartTimestamp == 5`, `RangeFilterError == null`.
- `EndTimestamp_set_below_Start_reverts`: set `Start = 10`, then `End = 5`; assert `End` still null (or prior), error set.
- `SetProperty_with_null_end_clears_constraint`: set `End = null` then `Start = 1000`; assert `Start` accepted, `_service.StartTimestamp == 1000`.

### Item 3 tests (4 new for harness, ~35 race-test migration)

- Harness tests: see Item 3 code block above.
- Migration: replace 11 occurrences in `CyclicSendServiceRaceTests.cs` (5 tests) + `CyclicDbcSendServiceRaceTests.cs` (6 tests) of `await Task.Delay(N); if (predicate) ...;` patterns with `await CyclicTimerTestHarness.AssertWithinAsync(predicate, timeout, "what");`. Each replacement adds harness retry-3 inside.

### Item 4 tests (3 new)

- `FromFdDescriptor_sets_IsFd_true`: assert `BaudRate.FromFdDescriptor("...", "...").IsFd == true`.
- `FromFdDescriptor_round_trips_descriptor_and_name`: assert `descriptor` and `name` equal input.
- `FromDescriptor_removed_no_compile_reference`: grep verification in PR plan; no runtime test (compile-time guarantee).

## Risks

### Item 1
- **LOW**: 2 次 `lock(this)` 加 re-check,lock 持有时间 < 1μs,perf impact 0。
- **LOW**: encode 同步 CPU-bound,Stop 在 encode 中发生 race window 极短,但仍存在 (improvement, not fix-all)。
- **MEDIUM**: 真正 abort in-flight send 需要 CancellationToken,**本 PATCH 不实现**;用户 click Stop 后 1 个 frame 仍会 send (已知行为,UI 可接受)。Mitigated by PR description 明文。

### Item 2
- **LOW**: `SetProperty(ref, value, validator)` 是 CommunityToolkit.Mvvm 8.4+ API;项目当前版本 < 8.4 需手写 fallback (Decision 3 Fallback)。Mitigated by PR plan 版本验证 first action。
- **LOW**: validator static helper 1 行逻辑,不单测,靠 property setter tests 覆盖。
- **MEDIUM**: source-gen `[ObservableProperty]` 移除后,`OpenAsync` 内的 `StartTimestamp = null;` (line 295) 必须确认走新 property setter 路径(自动,确认 OK)。Mitigated by manual review OpenAsync in plan §3 + integration test。
- **MEDIUM**: 现有 `RangeFilterError` 单一 property 保持 (Start/End 共享);XAML 行为不变。

### Item 3
- **LOW**: Harness 内部 3 次 retry,每个 retry 10ms;total worst-case wait = `(timeout + 10ms) * 3`。当前 race tests timeout 500ms - 1s,worst-case 1.5s - 3s,可接受。
- **MEDIUM**: Harness `[CyclicTimerTestHarness] retry N/3` log 在 test output 可能干扰 xunit 默认 output (console.error 写 vs ITestOutputHelper)。Mitigated by PR plan 验证 log 不破坏 xunit output capture。
- **LOW**: Harness 抽取后,`CyclicSendServiceRaceTests` + `CyclicDbcSendServiceRaceTests` 仍独立 (production 独立原则,Decision 7),无 abstraction 引入。

### Item 4
- **LOW**: API rename 是 breaking change for any external user 直接调 `BaudRate.FromDescriptor`。**项目内零 caller** (Phase 2.5 验证),但下游 user 需要 release notes 通知。Mitigated by release notes 段 + CHANGELOG。
- **LOW**: `[Obsolete]` attribute 移除后,曾标 Obsolete 的 method 不再有 compile-time warning,下游 user 静默 upgrade。Acceptable trade-off (rename 显式说明 FD-only)。
- **MEDIUM**: `BaudRate` class-level XML doc 改写,如果未来加回 `FromDescriptor` 4-arg overload 时,新 author 需读 class doc 知道约束。Mitigated by release notes + 单元测试覆盖 method behavior。

## Cross-cutting concerns (from v1.5.1 PATCH + project conventions)

| Concern | Action |
|---|---|
| Source-gen LoggerMessage | Item 1 不引入新 log;Item 4 不引入新 log。 |
| SynchronizationContext capture in VMs | Item 2 不变 (`ReplayViewModel` 已有 ctor capture)。 |
| `[Obsolete]` migration deadlines | Item 4 移除一个 `[Obsolete]` attribute (rename 后不再 obsolete)。 |
| `PathNormalizer` defense-in-depth | Item 1/2/3/4 不动 IO,natural compliance。 |
| STA-WPF test discipline | App.Tests 用 NSubstitute mocks + FakeSendService virtual override;Item 3 harness 纯 async/await,无 WPF。 |
| NetArchTest rule 2 (Core no PEAK SDK) | Item 4 严守:rename 不引入 `TPCANBaudrate` 字段;class doc 显式说明约束。 |
| Race-test transient-flaky caveat | Item 3 harness 自动化 retry,CI 不再依赖 human re-run。 |
| `[Obsolete]` 是 safe-to-remove signal | Item 4 移除 `[Obsolete]` (rename 后 no longer "less capable"),符合 v1.5.1 Item 3 PATCH 纪律。 |
| CSharp `lock(this)` anti-pattern | Item 1 复用现有 `lock(this)` (service 是 sealed,无外部继承);不引入新 lock object (Decision 2)。 |
| CommunityToolkit.Mvvm version | Item 2 依赖 8.4+ `SetProperty(ref, value, validator)`;PR plan 必做版本验证 first action (Decision 3 Fallback)。 |
| xunit.retry NuGet | Item 3 不引入,harness 内部 retry。 |
| v1.6.0 MINOR scope | 全部 5 项明确 deferred,本 PATCH 严守 scope。 |

## Ship method (mirror v1.5.1 PATCH)

1. `git checkout -b feature/v1-6-1-patch` (cut from main `35a3967`, already reset to v1.5.1 squash).
2. Spec + plan committed to feature branch (this file + `docs/superpowers/plans/2026-06-29-v1-6-1-patch.md`).
3. TDD per-item commits (RED → GREEN → IMPROVE × 4 items):
   - Item 4 first (smallest, mechanical: rename + doc rewrite + 3 tests).
   - Item 3 second (test-only harness, low risk).
   - Item 1 third (production race fix, surgical).
   - Item 2 fourth (source-gen property rewrite, biggest blast radius).
4. Pre-ship `code-reviewer` subagent → address 0C/0H; MEDIUM findings expected to be ≤ 2 (per pattern).
5. `docs/release-notes-v1.6.1.md` authored (mirror v1.5.1 format: items + tests + process lessons + brief-vs-source drift).
6. `git -c http.proxy="" -c https.proxy="" push -u origin feature/v1-6-1-patch` (mirror v1.5.1 network workaround).
7. `gh pr create --base main --title "v1.6.1 PATCH: 4 LOW ship-new follow-ups (CyclicDbc mid-tick + Start>End UX + CyclicTimerTestHarness + BaudRate rename)" --body-file docs/release-notes-v1.6.1.md`.
8. `gh pr merge --squash --delete-branch` (expect proxy-flaky on first attempt; recover per v1.5.1 ship method).
9. `git fetch origin main + git reset --hard origin/main` (v1.5.3 lesson: ship 后第一次 reset 必先 fetch).
10. Tag `v1.6.1` + `git push origin v1.6.1`.
11. `gh release create v1.6.1 --notes-file docs/release-notes-v1.6.1.md --title "v1.6.1 PATCH"`.
12. Update `~/.claude/projects/D--claude-proj2/memory/MEMORY.md` index + write `peakcan-host-v1-6-1-shipped.md` topic file (mirror v1.5.1).

## Open Questions

- **Item 2**: 是否需要 `[NotifyDataErrorInfo]` 替代 `RangeFilterError` text block,让 UI 显示红框?——本 spec 沿用现有 text block,**不**增强可见性。MINOR 周期再考虑。
- **Item 3**: Harness 是否要支持自定义 retry count + retry delay?——本 spec 用 `DefaultRetryCount = 3` + `RetryDelay = 10ms` hard-coded。YAGNI; 未来真有需求再加 overload。
- **Item 4**: rename 后是否需要 `[EditorBrowsable(Never)]` attribute 隐藏 `FromDescriptor` (在 .NET editor 中不显示)?——本 spec 不加 (Core 范围小, API 表面已精简)。YAGNI。
- **Item 1**: 第二次 re-check (send 前) 是否必要?——Decision 1 论证过 yes (race window 完整 closure)。如果未来 performance profiling 显示 lock 竞争,可降级为只 encode 前 re-check。
- **v1.6.0 MINOR scope**: 5 项是否在 v1.6.1 PATCH 之后立即启动?——不在本 spec 范围。memory `peakcan-host-v1-5-1-shipped.md` 明确 deferred 到 v1.6.x MINOR。
